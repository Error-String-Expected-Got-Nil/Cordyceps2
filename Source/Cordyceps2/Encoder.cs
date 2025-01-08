using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
// ReSharper disable BitwiseOperatorOnEnumWithoutFlags

namespace Cordyceps2;

// This class is blanket unsafe since it's dealing with unmanaged libav data everywhere.
//
// Usage: Constructor performs initial setup and configuration of codecs and muxer. Start() begins the codec and muxer
// threads, also taking the path to the directory you want videos output to. Returns a Task<bool> which completes when
// all threads have been started and the Encoder is ready to accept input data, or if something went wrong. Value will
// be true if Encoder started successfull, false or faulted if it didn't.
//
// If an Encoder is stopped, or its return for Start() is false or faulted, it cannot be restarted. You must dispose it
// and instantiate a new Encoder to try again.

public unsafe class Encoder : IDisposable
{
    public const AVCodecID VideoCodec = AVCodecID.AV_CODEC_ID_H264;
    
    private readonly ConcurrentQueue<byte[]> _videoDataQueue = new();
    private readonly ConcurrentQueue<Pointer<AVPacket>> _packetQueue = new();

    private readonly SemaphoreSlim _videoDataSubmitted = new(0);
    private readonly SemaphoreSlim _packetSubmitted = new(0);
    
    private readonly AVRational _videoTimeBase;
    private readonly SwsContext* _frameFormatter;
    private readonly int _inputLinesize;
    private readonly AVCodec* _videoCodec;
    private readonly AVCodecContext* _videoCodecContext;
    private readonly AVFormatContext* _outputFormatContext;

    private AVStream* _videoStream;
    private Task _videoCodecThread;

    private long _framecount;
    
    private bool _hardStop;
    private bool _codecsStopped;
    private bool _disposed;
    
    public bool Running { get; private set; }
    public bool Stopping { get; private set; }
    public bool Stopped { get; private set; }
    public bool Faulted { get; private set; }

    public readonly VideoSettings VideoConfig;

    // Called when any of the processing threads in the Encoder stop due to an unhandled exception.
    public event OnFaultEvent OnFault;
    
    public Encoder(VideoSettings conf)
    {
        VideoConfig = conf;
        
        _frameFormatter = ffmpeg.sws_getContext(
            conf.VideoInputWidth, conf.VideoInputHeight, conf.InputPixelFormat,
            conf.VideoOutputWidth, conf.VideoOutputHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
            ffmpeg.SWS_BILINEAR, null, null, null
        );
        _inputLinesize = ffmpeg.av_image_get_linesize(conf.InputPixelFormat, conf.VideoInputWidth, 0);
        
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

        var outputFormat = ffmpeg.av_guess_format(null, "mp4", null);
        if (outputFormat == null) throw new EncoderException("Could not get mp4 output format.");
        
        AVFormatContext* fmtctx = null;
        ffmpeg.avformat_alloc_output_context2(&fmtctx, outputFormat, null, null);
        if (fmtctx == null) throw new EncoderException("Failed to allocate output format context.");

        _outputFormatContext = fmtctx;
    }

    // Submit one frame of video data as a byte array. Assumed to be a continuous array of a non-planar, packed pixel
    // format, such as RGBA. Does NOT check that the provided array is long enough to contain a full frame's worth of
    // data! Make sure it has at least that much or more to avoid access violations. 
    public void SubmitVideoData(byte[] data)
    {
        if (Faulted) throw new EncoderException("Attempt to submit video data to faulted encoder.");
        if (Stopped) throw new EncoderException("Attempt to submit video data to stopped encoder.");
        if (!Running || Stopping) return;
        
        _videoDataQueue.Enqueue(data);
        _videoDataSubmitted.Release();
    }
    
    private void VideoCodecThread()
    {
        AVPacket* packet = null;
        
        var inputData = new byte*[1];
        var linesize = new[] { _inputLinesize };

        // Only need to allocate frame once. The buffers created by av_frame_get_buffer are ref-counted, and the codec
        // will create its own reference to those buffers to save the data it needs. Calling av_frame_make_writable
        // will check if there is more than one reference, and create new buffers for the frame if there are. Thus, the
        // only thing that changes inside this frame are those buffers, so we only need to allocate one instance of the
        // frame, and set constant fields (width, height, pixel format) once.
        var frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new EncoderException("Failed to allocate video codec AVFrame.");

        frame->width = VideoConfig.VideoOutputWidth;
        frame->height = VideoConfig.VideoOutputHeight;
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                
        if (ffmpeg.av_frame_get_buffer(frame, 0) < 0)
            throw new EncoderException("Failed to allocate buffers for video codec AVFrame.");
        
        try
        {
            while (true)
            {
                _videoDataSubmitted.Wait();

                if (_hardStop) break;

                if (!_videoDataQueue.TryDequeue(out var buffer))
                {
                    if (Stopping) break;
                    continue;
                }

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

                frame->pts = _framecount;
                _framecount++;

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
                        // Have to rescale packet timestaps from video time base to stream time base. MINMAX flag makes
                        // the rounding pass ignore the special AV_NOPTS_VALUE for timestamps. NEAR_INF flag is the
                        // default round mode for av_rescale_q, the version of this function without options.
                        packet->pts = ffmpeg.av_rescale_q_rnd(
                            packet->pts, _videoTimeBase, _videoStream->time_base,
                            AVRounding.AV_ROUND_NEAR_INF | AVRounding.AV_ROUND_PASS_MINMAX);
                        packet->dts = ffmpeg.av_rescale_q_rnd(
                            packet->dts, _videoTimeBase, _videoStream->time_base,
                            AVRounding.AV_ROUND_NEAR_INF | AVRounding.AV_ROUND_PASS_MINMAX);
                        packet->duration = ffmpeg.av_rescale_q(
                            packet->duration, _videoTimeBase, _videoStream->time_base);
                        
                        SubmitPacket(packet);
                        packet = null;
                        continue;
                    }
                    
                    // No packet retrieved because encoder needs more input data, repeat outer loop to get next frame.
                    if (ret == ffmpeg.EAGAIN) break;
                    
                    // Otherwise, some other error occurred.
                    throw new EncoderException("Error on attempting to retrieve encoded video packet.");
                }
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private void SubmitPacket(AVPacket* packet)
    {
        if (_hardStop) return;
        
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
                    // TODO: Check on this, need to figure out exact procedure for stopping Encoder
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

    // TODO: Implement
    public void Start(string outputPath)
    {
        if (Faulted) 
            throw new EncoderException("Attempt to start faulted encoder.");
        if (Stopped)
            throw new EncoderException("Attempt to restart stopped encoder. You must create a new instance instead.");
        
        // Create video stream in output
        _videoStream = ffmpeg.avformat_new_stream(_outputFormatContext, _videoCodec);
        if (_videoStream == null) throw new EncoderException("Failed to create output video stream.");

        _videoStream->time_base = _videoTimeBase;
        _videoStream->avg_frame_rate = new AVRational { num = VideoConfig.Framerate, den = 1 };

        _videoCodecThread = Task.Run(VideoCodecThread).ContinueWith(vthread =>
            {
                Faulted = true;
                OnFault?.Invoke(this, "video codec", vthread.Exception, Stop());
            },
            TaskContinuationOptions.OnlyOnFaulted
        );

        // TODO: Start muxer

        Running = true;
    }
    
    // TODO: Implement
    // Procedure something like: Set Stopping to true to stop taking input, signal codec and muxer threads one more
    // time so they clear their queue and see that they're finished.
    // Once codecs and muxer threads return, flush the codecs, finish writing file, etc.
    public Task Stop()
    {
        throw new NotImplementedException();
    }

    
    // TODO: Implement
    // In-between for Stop() and ForceStop(). Stops without processing any remaining data or packets in buffers, but
    // still attempts to properly save the video file.
    public Task HardStop()
    {
        throw new NotImplementedException();
    }
    
    // TODO: Implement
    // Synchronous hard stop, makes all codec and muxer threads stop at the first opportunity, WITHOUT ensuring the
    // video file is properly saved. Should only be called if the Encoder is disposed witout being stopped first.
    private void ForceStop()
    {
        throw new NotImplementedException();
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
        string Preset
    );

    // "origin" is a string indicating which thread faulted, and "cause" is the exception that caused the fault
    // "stopTask" is the task generated by the Stop() method called on fault, so the event subscriber is able
    // to monitor when the Encoder finishes stopping.
    public delegate void OnFaultEvent(Encoder sender, string origin, AggregateException cause, Task stopTask);
}