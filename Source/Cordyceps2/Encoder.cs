using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
// ReSharper disable BitwiseOperatorOnEnumWithoutFlags

namespace Cordyceps2;

// This class is blanket unsafe since it's dealing with unmanaged libav data everywhere.
//
// Usage: Constructor performs initial setup and configuration of codecs and muxer. Start() begins the codec and muxer
// threads, also taking the path to the directory you want videos output to. Raw video/audio data may then be submitted
// via their respective functions, one audio/video frame at a time. It is recommended you use the pooled data buffers
// functionality to get the buffer arrays you fill with data, to avoid allocating too many large arrays.
//
// If an Encoder is stopped, it cannot be restarted. You must dispose it and instantiate a new Encoder to try again.

public unsafe class Encoder : IDisposable
{
    public const AVCodecID VideoCodec = AVCodecID.AV_CODEC_ID_H264;
    public const AVCodecID AudioCodec = AVCodecID.AV_CODEC_ID_AAC;

    private readonly Stopwatch _videoStopwatch;
    private readonly Stopwatch _audioStopwatch;
    
    private readonly ConcurrentQueue<byte[]> _videoDataQueue = new();
    private readonly ConcurrentQueue<byte[]> _audioDataQueue = new();
    private readonly ConcurrentQueue<Pointer<AVPacket>> _packetQueue = new();

    private readonly SemaphoreSlim _videoDataSubmitted = new(0);
    private readonly SemaphoreSlim _audioDataSubmitted = new(0);
    private readonly SemaphoreSlim _packetSubmitted = new(0);

    private readonly DataBufferPool _videoDataBufferPool;
    private readonly DataBufferPool _audioDataBufferPool;
    
    private readonly AVRational _videoTimeBase;
    private readonly AVRational _audioTimeBase;
    private readonly SwsContext* _frameFormatter;
    private readonly SwrContext* _sampleFormatter;
    private readonly int _inputLinesize;
    private readonly int[] _samplePlaneIndicies;
    private readonly AVCodec* _videoCodec;
    private readonly AVCodec* _audioCodec;
    private readonly AVCodecContext* _videoCodecContext;
    private readonly AVCodecContext* _audioCodecContext;
    private readonly AVFormatContext* _outputFormatContext;

    private AVStream* _videoStream;
    private AVStream* _audioStream;
    private int _videoStreamIndex;
    private int _audioStreamIndex;
    private Task _videoCodecThread;
    private Task _audioCodecThread;
    private Task _muxerThread;
    private Task _stopTask;
    
    private long _samplecount;
    
    private bool _hardStop;
    private bool _forceStop;
    private bool _codecsStopped;
    private bool _disposed;
    
    public bool Running { get; private set; }
    public bool Stopping { get; private set; }
    public bool Stopped { get; private set; }
    public bool Faulted { get; private set; }
    public int MaxVideoFramesQueued { get; private set; }
    public int MaxAudioFramesQueued { get; private set; }
    public long Frames { get; private set; }
    public long AudioFrames { get; private set; }
    public decimal VideoEncodeTime { get; private set; }
    public decimal AudioEncodeTime { get; private set; }
    
    public readonly VideoSettings VideoConfig;
    public readonly AudioSettings AudioConfig;
    public readonly bool HasAudio;
    public readonly bool HasProfiling;
    
    // Video frames are an intuitive concept, audio frames generally less so. In short, an audio frame is a sequence of
    // audio samples that cannot be encoded or decoded independently, a video frame is the same but with pixels. The
    // size of an audio frame is more or less arbitrary, and some encoders only support a particular set of sizes for
    // audio frames. In the case of the MP4 format's AAC encoder, this is 1024. This field contains the number of bytes
    // in an audio frame for the encoder, and is provided so you know how to size your buffers if you are not utilizing
    // pooled data buffers for audio frames.
    public readonly int AudioFrameSize;
    // Number of audio samples per audio frame.
    public readonly int SamplesPerFrame;

    // Called when any of the processing threads in the Encoder stop due to an unhandled exception.
    public event OnFaultEvent OnFault;
    
    // TODO: Verify no memory leaks if exception occurs in constructor? May be unnecessary due to how unlikely that is,
    //  though; and they'd be small anyways
    public Encoder(VideoSettings conf)
    {
        if (Environment.Is64BitProcess)
            throw new EncoderException("Encoder currently only supports being run in a 32-bit process.");
        
        VideoConfig = conf;

        if (ffmpeg.av_pix_fmt_count_planes(conf.InputPixelFormat) > 1)
            throw new NotImplementedException("Encoder currently does not support input pixel formats with more than " +
                                              "one data plane (i.e. you must use a packed format like RGB24 or RGBA).");
        
        _frameFormatter = ffmpeg.sws_getContext(
            conf.VideoInputWidth, conf.VideoInputHeight, conf.InputPixelFormat,
            conf.VideoOutputWidth, conf.VideoOutputHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
            conf.SwsFlags, null, null, null
        );
        _inputLinesize = ffmpeg.av_image_get_linesize(conf.InputPixelFormat, conf.VideoInputWidth, 0);

        if (conf.UsePooledDataBuffers)
            _videoDataBufferPool = new DataBufferPool(_inputLinesize * conf.VideoInputHeight, conf.PoolDepth);
        
        _videoCodec = ffmpeg.avcodec_find_encoder(VideoCodec);
        if (_videoCodec == null) throw new EncoderException("Could not find codec with ID: " + VideoCodec);

        _videoTimeBase = new AVRational { num = 1, den = conf.Framerate };
        
        _videoCodecContext = ffmpeg.avcodec_alloc_context3(_videoCodec);
        if (_videoCodecContext == null) throw new EncoderException("Failed to allocate video codec context.");
        _videoCodecContext->width = conf.VideoOutputWidth;
        _videoCodecContext->height = conf.VideoOutputHeight;
        _videoCodecContext->time_base = _videoTimeBase;
        _videoCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _videoCodecContext->gop_size = conf.KeyframeInterval;
        _videoCodecContext->bit_rate = 0; // Bitrate is determined by CRF
        ffmpeg.av_opt_set_double(_videoCodecContext->priv_data, "crf", conf.ConstantRateFactor, 0);
        ffmpeg.av_opt_set(_videoCodecContext->priv_data, "preset", conf.Preset, 0);
        
        if (ffmpeg.avcodec_open2(_videoCodecContext, _videoCodec, null) < 0)
            throw new EncoderException("Failed to open video codec.");

        var outputFormat = ffmpeg.av_guess_format("mp4", null, null);
        if (outputFormat == null) throw new EncoderException("Could not get mp4 output format.");
        
        AVFormatContext* fmtctx = null;
        ffmpeg.avformat_alloc_output_context2(&fmtctx, outputFormat, null, null);
        if (fmtctx == null) throw new EncoderException("Failed to allocate output format context.");

        _outputFormatContext = fmtctx;
    }

    // Alternate form also including an audio track.
    public Encoder(VideoSettings vconf, AudioSettings aconf) : this(vconf)
    {
        HasAudio = true;
        AudioConfig = aconf;

        _audioCodec = ffmpeg.avcodec_find_encoder(AudioCodec);
        if (_audioCodec == null) throw new EncoderException("Could not find audio codec with ID: " + AudioCodec);

        var supportedSampleRate = _audioCodec->supported_samplerates;
        var foundSampleRate = false;
        while (*supportedSampleRate != 0)
        {
            if (*supportedSampleRate == aconf.SampleRate)
            {
                foundSampleRate = true;
                break;
            }
            
            supportedSampleRate++;
        }

        if (!foundSampleRate) throw new EncoderException("Given sample rate was not supported by audio codec.");
        
        _audioTimeBase = new AVRational { num = 1, den = aconf.SampleRate };

        _audioCodecContext = ffmpeg.avcodec_alloc_context3(_audioCodec);
        if (_audioCodecContext == null) throw new EncoderException("Failed to allocate audio codec context.");

        _audioCodecContext->bit_rate = aconf.Bitrate;
        _audioCodecContext->time_base = _audioTimeBase;
        _audioCodecContext->sample_rate = aconf.SampleRate;
        _audioCodecContext->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;

        var channelLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&channelLayout, aconf.Channels);
        _audioCodecContext->ch_layout = channelLayout;

        if (ffmpeg.avcodec_open2(_audioCodecContext, _audioCodec, null) < 0)
            throw new EncoderException("Failed to open audio codec.");

        SwrContext* swrctx = null;
        if (ffmpeg.swr_alloc_set_opts2(&swrctx, &channelLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, 
                aconf.SampleRate, &channelLayout, aconf.SampleFormat, aconf.SampleRate, 
                0, null) < 0)
            throw new EncoderException("Failed to create SwrContext.");
        if (ffmpeg.swr_init(swrctx) < 0)
            throw new EncoderException("Failed to initialize SwrContext.");
        _sampleFormatter = swrctx;

        SamplesPerFrame = _audioCodecContext->frame_size;
        AudioFrameSize = SamplesPerFrame
                         * aconf.Channels
                         * ffmpeg.av_get_bytes_per_sample(aconf.SampleFormat);

        // Determine what indicies of the audio sample data buffer are the start of each plane in the buffer. This is
        // needed later in order to properly pass pointers to these planes when resampling with swr_convert().
        if (ffmpeg.av_sample_fmt_is_planar(aconf.SampleFormat) == 1 || aconf.Channels == 1)
        {
            _samplePlaneIndicies = new int[aconf.Channels];
            var indexIncrement = AudioFrameSize / aconf.Channels;
            for (var i = 0; i < aconf.Channels; i++)
                _samplePlaneIndicies[i] = indexIncrement * i;
        }
        else
            _samplePlaneIndicies = [0];
        
        if (!aconf.UsePooledDataBuffers) return;
        _audioDataBufferPool = new DataBufferPool(AudioFrameSize, aconf.PoolDepth);
    }
    
    public Encoder(VideoSettings conf, bool doProfiling) : this(conf)
    {
        HasProfiling = doProfiling;
        if (!doProfiling) return;
        _videoStopwatch = new Stopwatch();
    }

    public Encoder(VideoSettings vconf, AudioSettings aconf, bool doProfiling) : this(vconf, aconf)
    {
        HasProfiling = doProfiling;
        if (!doProfiling) return;
        _videoStopwatch = new Stopwatch();
        _audioStopwatch = new Stopwatch();
    }
    
    // Get a buffer for frame data from a pool to avoid excessive memory allocation for raw video data frame arrays.
    // Throws an exception if this was not enabled in the video settings on instantiation of the Encoder.
    // If a limit has been set on pool depth, will return null if too many buffers have already been released.
    public byte[] GetVideoDataBuffer() 
        => (_videoDataBufferPool 
            ?? throw new EncoderException("Attempt to get a pooled video data buffer when not using pooled buffers."))
            .Pop();
    
    // Use this to return a pooled buffer without submitting it. Does nothing if not using pooled buffers.
    public void ReturnVideoDataBuffer(byte[] buffer) => _videoDataBufferPool?.Push(buffer);
    
    // Submit one frame of video data as a byte array. Assumed to be a continuous array of a non-planar, packed pixel
    // format, such as RGBA. Does NOT check that the provided array is long enough to contain a full frame's worth of
    // data! Make sure it has at least that much or more to avoid access violations. 
    //
    // Return value indicates whether any data was queued or not.
    public bool SubmitVideoData(byte[] data)
    {
        if (Faulted) throw new EncoderException("Attempt to submit video data to faulted encoder.");
        if (Stopped) throw new EncoderException("Attempt to submit video data to stopped encoder.");
        if (!Running || Stopping) return false;
        
        _videoDataQueue.Enqueue(data);
        MaxVideoFramesQueued = Math.Max(MaxVideoFramesQueued, _videoDataQueue.Count);
        _videoDataSubmitted.Release();

        return true;
    }

    // Same as above methods, but for audio data.
    public byte[] GetAudioDataBuffer()
        => (_audioDataBufferPool
            ?? throw new EncoderException("Attempt to get a pooled audio data buffer when not using pooled buffers."))
            .Pop();

    public void ReturnAudioDataBuffer(byte[] buffer) => _audioDataBufferPool?.Push(buffer);

    public bool SubmitAudioData(byte[] data)
    {
        if (!HasAudio) throw new EncoderException("Attempt to submit audio data to encoder without audio track.");
        if (Faulted) throw new EncoderException("Attempt to submit audio data to faulted encoder.");
        if (Stopped) throw new EncoderException("Attempt to submit audio data to stopped encoder.");
        if (!Running || Stopping) return false;
        
        _audioDataQueue.Enqueue(data);
        MaxAudioFramesQueued = Math.Max(MaxAudioFramesQueued, _audioDataQueue.Count);
        _audioDataSubmitted.Release();

        return true;
    }
    
    private void VideoCodecThread()
    {
        AVPacket* packet = null;
        
        var inputData = new byte*[1];
        var linesize = new[] { _inputLinesize };

        // Height of chroma planes in output frame, necessary for vertical flip, if enabled.
        var chromaHeight = VideoConfig.VideoOutputHeight / 2 + VideoConfig.VideoOutputHeight % 2;

        // Only need to allocate frame once. The buffers created by av_frame_get_buffer are ref-counted, and the codec
        // will create its own reference to those buffers to save the data it needs. Calling av_frame_make_writable
        // will check if there is more than one reference, and create new buffers for the frame if there are. Thus, the
        // only thing that changes inside this frame are those buffers, so we only need to allocate one instance of the
        // frame, and set constant fields (width, height, pixel format) once.
        var frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new EncoderException("Failed to allocate video codec AVFrame.");

        // See definition of AVFrame32 for reasoning as to why this pointer cast is performed.
        var frame32 = (AVFrame32*)frame;
        
        frame32->width = VideoConfig.VideoOutputWidth;
        frame32->height = VideoConfig.VideoOutputHeight;
        frame32->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

        if (VideoConfig.UseColorspaceInformation)
        {
            frame32->color_range = VideoConfig.ColorRange;
            frame32->color_primaries = VideoConfig.ColorPrimaries;
            frame32->color_trc = VideoConfig.ColorTrc;
            frame32->colorspace = VideoConfig.Colorspace;
        }
        
        try
        {
            if (ffmpeg.av_frame_get_buffer(frame, 0) < 0)
                throw new EncoderException("Failed to get buffers for video codec AVFrame.");
            
            while (true)
            {
                _videoDataSubmitted.Wait();

                if (_hardStop) break;

                if (!_videoDataQueue.TryDequeue(out var buffer))
                {
                    if (Stopping) break;
                    continue;
                }
                
                if (HasProfiling) _videoStopwatch.Start();

                if (ffmpeg.av_frame_make_writable(frame) < 0)
                    throw new EncoderException("Failed to make video codec AVFrame writable.");

                fixed (byte* pixelData = buffer)
                {
                    // sws_scale is designed to work with multi-plane pixel formats, and as such accepts the input data
                    // and linesize arguments as *arrays*, where each element is the data/linesize for the respective
                    // plane in the input format. In this case the arrays have been pre-allocated at the start of this
                    // method, and we just need to set the pointer for the inputData, since the linesize is constant.
                    inputData[0] = pixelData;
                    
                    ffmpeg.sws_scale(
                        _frameFormatter,
                        inputData, linesize,
                        0, VideoConfig.VideoInputHeight,
                        frame->data, frame->linesize
                    );
                }

                if (VideoConfig.UsePooledDataBuffers)
                    _videoDataBufferPool.Push(buffer);

                if (VideoConfig.VerticalFlip)
                {
                    // Bumping the data pointers to the start of the final line in each and then making the linesize
                    // negative allows the frame to effectively be vertically flipped without actually changing
                    // the data in any way.
                    frame->data[0] += (frame->height - 1) * frame->linesize[0];
                    frame->data[1] += (chromaHeight - 1) * frame->linesize[1];
                    frame->data[2] += (chromaHeight - 1) * frame->linesize[2];

                    frame->linesize[0] = -frame->linesize[0];
                    frame->linesize[1] = -frame->linesize[1];
                    frame->linesize[2] = -frame->linesize[2];
                }
                
                frame->pts = Frames;
                Frames++;

                if (ffmpeg.avcodec_send_frame(_videoCodecContext, frame) < 0)
                    throw new EncoderException("Error on attempting to encode video frame.");

                // As per FFmpeg/libav documentation, it is technically permissible for an encoder to produce more than
                // one packet per sent frame. I don't think the H264 codec does this, but for the sake of robustness
                // I'll be implementing it as if it might.
                while (true)
                {
                    if (packet == null) packet = ffmpeg.av_packet_alloc();
                    if (packet == null) throw new EncoderException("Failed to allocate video codec output packet.");

                    var ret = ffmpeg.avcodec_receive_packet(_videoCodecContext, packet);
                    
                    // Packet successfully received
                    if (ret == 0)
                    {
                        RescalePacketTimestamps(packet, _videoTimeBase, _videoStream->time_base);
                        packet->stream_index = _videoStreamIndex;
                        SubmitPacket(packet);
                        packet = null;
                        continue;
                    }
                    
                    // No packet retrieved because encoder needs more input data, repeat outer loop to get next frame.
                    // FFmpeg.AutoGen has EAGAIN as positive, but the actual return code is negative; negate it.
                    if (ret == -ffmpeg.EAGAIN)
                    {
                        if (!HasProfiling) break;
                        
                        _videoStopwatch.Stop();
                        VideoEncodeTime += (decimal)_videoStopwatch.ElapsedTicks / Stopwatch.Frequency;
                        _videoStopwatch.Reset();
                        
                        break;
                    }
                    
                    // Otherwise, some other error occurred.
                    throw new EncoderException("Error on attempting to retrieve encoded video packet.");
                }
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            _videoStopwatch?.Reset();
        }
    }

    // Should only be called when encoder is stopping and video codec is ready to stop. Sends the flush command to the
    // codec and empties any buffered packets to the packet queue.
    private void DrainVideoCodec()
    {
        AVPacket* packet = null;

        try
        {
            if (ffmpeg.avcodec_send_frame(_videoCodecContext, null) < 0)
                throw new EncoderException("Failed to send flush packet to video codec.");

            if (HasProfiling) _videoStopwatch.Start();
            
            while (true)
            {
                packet = ffmpeg.av_packet_alloc();
                if (packet == null) throw new EncoderException("Failed to allocate video codec output packet.");

                var ret = ffmpeg.avcodec_receive_packet(_videoCodecContext, packet);

                // No more packets buffered, draining is finished.
                if (ret == ffmpeg.AVERROR_EOF) break;

                if (ret == 0)
                {
                    RescalePacketTimestamps(packet, _videoTimeBase, _videoStream->time_base);
                    packet->stream_index = _videoStreamIndex;
                    SubmitPacket(packet);
                    packet = null;
                    continue;
                }

                throw new EncoderException("Error receiving packet during video codec draining.");
            }

            if (HasProfiling)
            {
                _videoStopwatch.Stop();
                VideoEncodeTime += (decimal)_videoStopwatch.ElapsedTicks / Stopwatch.Frequency;
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            _videoStopwatch?.Reset();
        }
    }

    private void AudioCodecThread()
    {
        AVPacket* packet = null;

        var inputData = stackalloc byte*[_samplePlaneIndicies.Length];
        
        var frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new EncoderException("Failed to allocate audio codec AVFrame.");
        
        // See definition of AVFrame32 for reasoning as to why this pointer cast is performed.
        var frame32 = (AVFrame32*)frame;
        
        frame32->nb_samples = SamplesPerFrame;
        frame32->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        frame32->ch_layout = _audioCodecContext->ch_layout;
        
        try
        {
            if (ffmpeg.av_frame_get_buffer(frame, 0) < 0)
                throw new EncoderException("Failed to get buffers for audio codec AVFrame.");
            
            while (true)
            {
                _audioDataSubmitted.Wait();

                if (_hardStop) break;

                if (!_audioDataQueue.TryDequeue(out var buffer))
                {
                    if (Stopping) break;
                    continue;
                }

                if (HasProfiling) _audioStopwatch.Start();

                if (ffmpeg.av_frame_make_writable(frame) < 0)
                    throw new EncoderException("Failed to make audio codec AVFrame writable.");

                fixed (byte* sampleData = buffer)
                {
                    for (var i = 0; i < _samplePlaneIndicies.Length; i++)
                        inputData[i] = sampleData + _samplePlaneIndicies[i];

                    // Number of output samples should always be equal to number of input samples when the sample rate
                    // is the same for input and output; this is why that was required in the audio config. Not having
                    // to deal with buffering extra samples makes things significantly easier.
                    // This weird cast of (byte**)&frame->data is due to how FFmpegAutoGen stores the data field of
                    // AVFrames; they have a special type for it. In raw C you would just do frame->data.
                    ffmpeg.swr_convert(_sampleFormatter, 
                        (byte**)&frame->data, SamplesPerFrame, 
                        &inputData[0], SamplesPerFrame);
                }
                
                if (AudioConfig.UsePooledDataBuffers)
                    _audioDataBufferPool.Push(buffer);

                frame->pts = _samplecount;
                _samplecount += SamplesPerFrame;
                AudioFrames++;

                if (ffmpeg.avcodec_send_frame(_audioCodecContext, frame) < 0)
                    throw new EncoderException("Error on attempting to encode audio frame.");

                while (true)
                {
                    if (packet == null) packet = ffmpeg.av_packet_alloc();
                    if (packet == null) throw new EncoderException("Failed to allocate audio codec output packet.");

                    var ret = ffmpeg.avcodec_receive_packet(_audioCodecContext, packet);
                    
                    if (ret == 0)
                    {
                        RescalePacketTimestamps(packet, _audioTimeBase, _audioStream->time_base);
                        packet->stream_index = _audioStreamIndex;
                        SubmitPacket(packet);
                        packet = null;
                        continue;
                    }
                    
                    if (ret == -ffmpeg.EAGAIN)
                    {
                        if (!HasProfiling) break;
                        
                        _audioStopwatch.Stop();
                        AudioEncodeTime += (decimal)_audioStopwatch.ElapsedTicks / Stopwatch.Frequency;
                        _audioStopwatch.Reset();
                        
                        break;
                    }
                    
                    throw new EncoderException("Error on attempting to retrieve encoded audio packet.");
                }
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            _audioStopwatch?.Reset();
        }
    }

    private void DrainAudioCodec()
    {
        AVPacket* packet = null;

        try
        {
            if (ffmpeg.avcodec_send_frame(_audioCodecContext, null) < 0)
                throw new EncoderException("Failed to send flush packet to audio codec.");

            if (HasProfiling) _audioStopwatch.Start();
            
            while (true)
            {
                packet = ffmpeg.av_packet_alloc();
                if (packet == null) throw new EncoderException("Failed to allocate audio codec output packet.");

                var ret = ffmpeg.avcodec_receive_packet(_audioCodecContext, packet);

                // No more packets buffered, draining is finished.
                if (ret == ffmpeg.AVERROR_EOF) break;

                if (ret == 0)
                {
                    RescalePacketTimestamps(packet, _audioTimeBase, _audioStream->time_base);
                    packet->stream_index = _audioStreamIndex;
                    SubmitPacket(packet);
                    packet = null;
                    continue;
                }

                throw new EncoderException("Error receiving packet during audio codec draining.");
            }

            if (HasProfiling)
            {
                _audioStopwatch.Stop();
                AudioEncodeTime += (decimal)_audioStopwatch.ElapsedTicks / Stopwatch.Frequency;
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            _audioStopwatch?.Reset();
        }
    }

    private void SubmitPacket(AVPacket* packet)
    {
        if (_hardStop)
        {
            // If in hard stop, any packets submitted are taken and discarded immediately.
            ffmpeg.av_packet_free(&packet);
            return;
        }
        
        _packetQueue.Enqueue(packet);
        _packetSubmitted.Release();
    }

    private void MuxerThread()
    {
        AVPacket* packet = null;
        
        try
        {
            while (true)
            {
                _packetSubmitted.Wait();
                
                if (_hardStop) break;

                if (!_packetQueue.TryDequeue(out var packetRef))
                {
                    if (_codecsStopped) break;
                    continue;
                }

                packet = packetRef;
                
                if (ffmpeg.av_interleaved_write_frame(_outputFormatContext, packet) < 0)
                    throw new EncoderException("Error trying to write frame to output.");
                
                ffmpeg.av_packet_free(&packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    private void CloseMuxer()
    {
        // Flush interleave buffer and write the trailer.
        if (ffmpeg.av_interleaved_write_frame(_outputFormatContext, null) < 0)
            throw new EncoderException("Error trying to flush buffered interleaved packets.");
        if (ffmpeg.av_write_trailer(_outputFormatContext) < 0)
            throw new EncoderException("Error trying to write video file trailer.");

        // AVIO context must be explicitly closed, it is not done when the AVFormatContext is freed.
        if (ffmpeg.avio_close(_outputFormatContext->pb) < 0)
            throw new EncoderException("Error trying to close AVIO output resource.");
    }

    // Have to rescale packet timestaps from video/audio time base to stream time base. MINMAX flag makes
    // the rounding pass ignore the special AV_NOPTS_VALUE for timestamps. NEAR_INF flag is the
    // default round mode for av_rescale_q, the version of this function without options.
    private static void RescalePacketTimestamps(AVPacket* packet, AVRational from, AVRational to)
    {
        packet->pts = ffmpeg.av_rescale_q_rnd(
            packet->pts, from, to,
            AVRounding.AV_ROUND_NEAR_INF | AVRounding.AV_ROUND_PASS_MINMAX);
        packet->dts = ffmpeg.av_rescale_q_rnd(
            packet->dts, from, to,
            AVRounding.AV_ROUND_NEAR_INF | AVRounding.AV_ROUND_PASS_MINMAX);
        packet->duration = ffmpeg.av_rescale_q(packet->duration, from, to);
    }

    // Assumes that the given outputPath has already been verified to be valid before calling.
    public void Start(string outputPath)
    {
        if (Faulted) 
            throw new EncoderException("Attempt to start faulted encoder.");
        if (Stopped)
            throw new EncoderException("Attempt to restart stopped encoder. You must create a new instance instead.");
        
        // Create video stream in output. This represents the portion of data in the output file that is the video data
        // (rather than audio, subtitles, etc., if we happened to have those as well).
        _videoStream = ffmpeg.avformat_new_stream(_outputFormatContext, _videoCodec);
        if (_videoStream == null) throw new EncoderException("Failed to create output video stream.");

        // All of these should be explicitly set to initialize the stream.
        _videoStream->time_base = _videoTimeBase;
        _videoStream->avg_frame_rate = new AVRational { num = VideoConfig.Framerate, den = 1 };
        _videoStream->id = (int)_outputFormatContext->nb_streams - 1;
        _videoStreamIndex = _videoStream->id;
        if (ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _videoCodecContext) < 0)
            throw new EncoderException("Failed to get video codec parameters.");

        _videoCodecThread = Task.Run(VideoCodecThread);
        _videoCodecThread.ContinueWith(vthread =>
            {
                Faulted = true;
                OnFault?.Invoke(this, "video codec", vthread.Exception, Stop());
            },
            TaskContinuationOptions.OnlyOnFaulted
        );

        if (HasAudio)
        {
            _audioStream = ffmpeg.avformat_new_stream(_outputFormatContext, _audioCodec);
            if (_audioStream == null) throw new EncoderException("Failed to create output audio stream.");

            _audioStream->time_base = _audioTimeBase;
            _audioStream->id = (int)_outputFormatContext->nb_streams - 1;
            _audioStreamIndex = _audioStream->id;
            if (ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCodecContext) < 0)
                throw new EncoderException("Failed to get audio codec parameters.");

            _audioCodecThread = Task.Run(AudioCodecThread);
            _audioCodecThread.ContinueWith(athread =>
                {
                    Faulted = true;
                    OnFault?.Invoke(this, "audio codec", athread.Exception, Stop());
                },
                TaskContinuationOptions.OnlyOnFaulted
            );
        }
        
        // Open the AVIO context for the muxer. This was allocated by the output context when it was created, and put
        // into the 'pb' field, so we grab it from there. Have to make it a local so we can take its address.
        var ctx = _outputFormatContext->pb;
        if (ffmpeg.avio_open(&ctx, outputPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
            throw new EncoderException("Failed to open output AVIO context. Given filepath: " + outputPath);
        _outputFormatContext->pb = ctx;
        
        // This may or may not actually write anything to a file, but this must be called to initialize the muxer.
        // TODO: Add options to make output mp4 fragmented in video settings
        //  Would work by adding to options dictonary: "movflags" -> "+frag_keyframe+empty_moov"
        if (ffmpeg.avformat_write_header(_outputFormatContext, null) < 0)
            throw new EncoderException("Failed to write output file header/initialize muxer.");

        _muxerThread = Task.Run(MuxerThread);
        _muxerThread.ContinueWith(mthread =>
            {
                Faulted = true;
                OnFault?.Invoke(this, "muxer", mthread.Exception, HardStop());    
            }, 
            TaskContinuationOptions.OnlyOnFaulted
        );

        Running = true;
    }
    
    // Stops the Encoder, emptying any remaining buffered data and saving the video file. Encoder has stopped once the
    // returned Task completes. Does nothing and returns null if Encoder is not running.
    //
    // This would make more sense as an async method but unfortunately await statements are not permitted in an unsafe
    // context, and making this class not unsafe would require rewriting it to not use pointer fields, which would be
    // much worse than the alternative here of using Task.Run().
    public Task Stop()
    {
        if (!Running) return null;
        
        Stopping = true;
        _stopTask = Task.Run(StopProcedure);
        return _stopTask;
        
        void StopProcedure()
        {
            // Release an extra permit so that the codecs thread dequeue nothing when they runs out, allowing them to
            // see that they're stopping and break.
            _videoDataSubmitted.Release();
            WaitTaskCatch(_videoCodecThread);

            if (HasAudio)
            {
                _audioDataSubmitted.Release();
                WaitTaskCatch(_audioCodecThread);
            }

            // Empty anything remaining in the codecs, unless hard stop was triggered.
            if (!_hardStop)
            {
                // TODO: May need to also add a catch around the drain functions in case they fault
                DrainVideoCodec();
                if (HasAudio) DrainAudioCodec();
            }

            // Codecs are finished processing, notify the muxer in the same way as the codecs.
            _codecsStopped = true;
            _packetSubmitted.Release();
            WaitTaskCatch(_muxerThread);
            
            // Finish the video file and close the AVIO stream, unless force stop was triggered.
            if (!_forceStop) CloseMuxer();

            Running = false;
            Stopping = false;
            Stopped = true;
            _stopTask = null;
        }
    }

    
    // In-between for Stop() and ForceStop(). Stops without processing any remaining data or packets in buffers, but
    // still attempts to properly save the video file. If already stopping, sets _hardStop to true and returns the
    // existing stop task.
    public Task HardStop()
    {
        if (Stopping)
        {
            _hardStop = true;
            return _stopTask;
        }
        
        if (!Running) return null;
        
        _hardStop = true;
        Stopping = true;

        _stopTask = Task.Run(HardStopProcedure);
        return _stopTask;
        
        void HardStopProcedure()
        {
            _videoDataSubmitted.Release();
            WaitTaskCatch(_videoCodecThread);

            if (HasAudio)
            {
                _audioDataSubmitted.Release();
                WaitTaskCatch(_audioCodecThread);
            }

            _packetSubmitted.Release();
            WaitTaskCatch(_muxerThread);
            
            if (!_forceStop) CloseMuxer();
            
            Running = false;
            Stopping = false;
            Stopped = true;
            _stopTask = null;
        }
    }
    
    // Synchronous hard stop, makes all codec and muxer threads stop at the first opportunity, WITHOUT ensuring the
    // video file is properly saved. Should only be called if the Encoder is disposed witout being stopped first.
    // If already stopping, sets hard stop and force stop to true and waits for the existing stop task to complete.
    private void ForceStop()
    {
        if (Stopping)
        {
            _hardStop = true;
            _forceStop = true;
            WaitTaskCatch(_stopTask);
        }
        
        if (!Running) return;
        
        _hardStop = true;
        Stopping = true;

        _videoDataSubmitted.Release();
        WaitTaskCatch(_videoCodecThread);
        
        if (HasAudio)
        {
            _audioDataSubmitted.Release();
            WaitTaskCatch(_audioCodecThread);
        }

        _packetSubmitted.Release();
        WaitTaskCatch(_muxerThread);

        // No check to see if the AVIO stream closed successfully because throwing an exception from Dispose() is bad.
        ffmpeg.avio_close(_outputFormatContext->pb);
        
        Running = false;
        Stopping = false;
        Stopped = true;
    }

    // Waits for a task to complete synchronously, suppressing any exceptions it threw. This is for the codec/muxer
    // threads, since they handle exceptions using continuations already, and we don't want the stop functions throwing
    // an exception propagated from a codec/muxer thread, since we may be stopping *because* they faulted.
    private static void WaitTaskCatch(Task task)
    {
        try
        {
            task.Wait();
        }
        catch (Exception)
        {
            // Intentionally eat any exceptions.
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (Running) ForceStop();
        
        if (disposing)
        {
            _videoDataSubmitted.Dispose();
            _audioDataSubmitted.Dispose();
            _packetSubmitted.Dispose();
        }

        while (_packetQueue.TryDequeue(out var packet))
        {
            var packetPointer = (AVPacket*)packet;
            ffmpeg.av_packet_free(&packetPointer);
        }

        ffmpeg.sws_freeContext(_frameFormatter);

        var vctx = _videoCodecContext;
        ffmpeg.avcodec_free_context(&vctx);

        if (HasAudio)
        {
            var actx = _audioCodecContext;
            ffmpeg.avcodec_free_context(&actx);
        }
        
        ffmpeg.avformat_free_context(_outputFormatContext);
        
        _disposed = true;
    }

    ~Encoder() => Dispose(false);

    public record struct VideoSettings(
        int VideoInputWidth,
        int VideoInputHeight,
        int VideoOutputWidth,
        int VideoOutputHeight,
        
        AVPixelFormat InputPixelFormat,
        
        int Framerate,
        int KeyframeInterval,
        float ConstantRateFactor,
        string Preset,
        
        // If enabled, modifies how the frame data is stored to veritcally flip the output frame with almost no extra
        // processing requirement. May be necessary if you are, for example, pulling frames from a game's rendering
        // output, as graphics APIs have the image origin in the bottom left, while videos usually use the upper left.
        bool VerticalFlip = false,
        
        // If true, you must use GetVideoDataBuffer to get the byte[] you pass into SubmitVideoData. These will be 
        // drawn from an automatically-expanding pool to ensure the buffers remain allocated for use and reuse across
        // the whole lifetime of the Encoder, easing pressure on the garbage collector.
        bool UsePooledDataBuffers = true,
        
        // Maximum number of buffers which may be released at any given time. If a buffer is requested when already at
        // the limit, GetVideoDataBuffer will return null instead. User is responsible for waiting before asking again.
        // Set to 0 to have no limit.
        int PoolDepth = 0,
        
        // Flag bitfield to pass to the video frame SWS context, to control how it is reformatted.
        int SwsFlags = ffmpeg.SWS_BILINEAR,
        
        // Setting this to false makes the encoder ignore the next 3 entries, letting libav deal with it automatically.
        bool UseColorspaceInformation = true,
        
        // Extra information about source colorspace. Defaults are good for sRGB color input.
        // If you're getting RGB pixel data from somewhere, it's probably sRGB. This is a packed format where each
        // color channel (red, green, blue) has only one byte of linear data to indicate its strength. This is *fine,*
        // but colors can be much more precisely stored than that (and in fact, GPUs and shaders work with them in the
        // form of floats), so sRGB color input needs to be converted to the format actually in use. This informs 
        // the Encoder how to do so for the input data it will be receiving.
        AVColorRange ColorRange = AVColorRange.AVCOL_RANGE_JPEG,
        AVColorPrimaries ColorPrimaries = AVColorPrimaries.AVCOL_PRI_BT709,
        AVColorTransferCharacteristic ColorTrc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
        AVColorSpace Colorspace = AVColorSpace.AVCOL_SPC_RGB
    );

    public record struct AudioSettings(
        // Input and output sample rate must be the same. You can design things such that they don't have to be, but
        // this makes things easier for resampling.
        int SampleRate,
        int Channels,
        AVSampleFormat SampleFormat,
        
        int Bitrate = 160000,
            
        // Same as pool options for video settings. Note that a pool depth of 0 is less bad for audio, since audio
        // frames are much smaller than video frames. Also note that pools will be pre-sized to that of the required
        // audio frame size, and should be filled completely unless they are the last audio frame, in which case
        // leftover space should be zeroed out. If not using pools, same applies: MAKE SURE your audio buffers are the
        // size of an audio frame! Use the AudioFrameSize property to check this.
        bool UsePooledDataBuffers = true,
        int PoolDepth = 0
    );

    // "origin" is a string indicating which thread faulted, and "cause" is the exception that caused the fault
    // "stopTask" is the task generated by the Stop() method called on fault, so the event subscriber is able
    // to monitor when the Encoder finishes stopping.
    public delegate void OnFaultEvent(Encoder sender, string origin, AggregateException cause, Task stopTask);

    private class DataBufferPool(int bufferSize, int depth)
    {
        private readonly ConcurrentBag<byte[]> _pool = [];
        
        private int _extantCount;

        public int BufferSize { get; private set; } = bufferSize;
        public int Depth { get; private set; } = depth;
        public int ExtantCount => _extantCount;
        public int TotalCount => _extantCount + _pool.Count;
        
        public byte[] Pop()
        {
            if (Depth != 0 && _extantCount >= Depth) return null;
            Interlocked.Increment(ref _extantCount);
            return _pool.TryTake(out var item) ? item : new byte[BufferSize];
        }

        public void Push(byte[] item)
        {
            if (_extantCount == 0)
                throw new ArgumentException("Attempt to push buffer to DataBufferPool with no extant items.");
            if (item.Length != BufferSize)
                throw new ArgumentException("Attempt to push buffer to DataBufferPool of different size than the " +
                                            "buffers provided by the pool.");
            
            Interlocked.Decrement(ref _extantCount);
            _pool.Add(item);
        }
    }
    
    // Have to use this wrapper because you can't have pointers as generic type arguments.
    private class Pointer<T>(T* ptr) where T : unmanaged
    {
        public T* Value = ptr;

        public static implicit operator T*(Pointer<T> ptr) => ptr.Value;
        public static implicit operator Pointer<T>(T* ptr) => new(ptr);
    }
    
    // Alternate version of the AVFrame struct from FFmpeg.AutoGen which has the type of the 'crop_*' fields changed
    // from ulong to uint. This is necessary when using 32-bit/x86 FFmpeg bindings, as these fields are natively typed
    // as 'size_t', which is 4 bytes for 32-bit, and 8 bytes for 64-bit.
    // It took me far, far too long to figure this out.
    // ReSharper disable InconsistentNaming
    // ReSharper disable NotAccessedField.Local
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
    private struct AVFrame32
    {
        public byte_ptrArray8 data;
        public int_array8 linesize;
        public byte** extended_data;
        public int width;
        public int height;
        public int nb_samples;
        public int format;
        public int key_frame;
        public AVPictureType pict_type;
        public AVRational sample_aspect_ratio;
        public long pts;
        public long pkt_dts;
        public AVRational time_base;
        public int quality;
        public void* opaque;
        public int repeat_pict;
        public int interlaced_frame;
        public int top_field_first;
        public int palette_has_changed;
        public int sample_rate;
        public AVBufferRef_ptrArray8 buf;
        public AVBufferRef** extended_buf;
        public int nb_extended_buf;
        public AVFrameSideData** side_data;
        public int nb_side_data;
        public int flags;
        public AVColorRange color_range;
        public AVColorPrimaries color_primaries;
        public AVColorTransferCharacteristic color_trc;
        public AVColorSpace colorspace;
        public AVChromaLocation chroma_location;
        public long best_effort_timestamp;
        public long pkt_pos;
        public AVDictionary* metadata;
        public int decode_error_flags;
        public int pkt_size;
        public AVBufferRef* hw_frames_ctx; 
        public AVBufferRef* opaque_ref;
        public uint crop_top;
        public uint crop_bottom;
        public uint crop_left;
        public uint crop_right;
        public AVBufferRef* private_ref;
        public AVChannelLayout ch_layout;
        public long duration;
    }
#pragma warning restore CS0649 // Field is never assigned to, and will always have its default value
    // ReSharper restore NotAccessedField.Local
    // ReSharper restore InconsistentNaming

    public class EncoderException(string message) : Exception(message);
}