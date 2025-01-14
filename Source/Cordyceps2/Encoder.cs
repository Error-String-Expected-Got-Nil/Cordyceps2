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
// be true if Encoder started successfully, false or faulted if it didn't.
//
// If an Encoder is stopped, it cannot be restarted. You must dispose it and instantiate a new Encoder to try again.

public unsafe class Encoder : IDisposable
{
    public const AVCodecID VideoCodec = AVCodecID.AV_CODEC_ID_H264;
    
    private readonly ConcurrentQueue<byte[]> _videoDataQueue = new();
    private readonly ConcurrentQueue<Pointer<AVPacket>> _packetQueue = new();

    private readonly SemaphoreSlim _videoDataSubmitted = new(0);
    private readonly SemaphoreSlim _packetSubmitted = new(0);

    private readonly DataBufferPool _videoDataBufferPool;
    
    private readonly AVRational _videoTimeBase;
    private readonly SwsContext* _frameFormatter;
    private readonly int _inputLinesize;
    private readonly AVCodec* _videoCodec;
    private readonly AVCodecContext* _videoCodecContext;
    private readonly AVFormatContext* _outputFormatContext;

    private AVStream* _videoStream;
    private Task _videoCodecThread;
    private Task _muxerThread;
    private Task _stopTask;
    
    private long _framecount;
    
    private bool _hardStop;
    private bool _forceStop;
    private bool _codecsStopped;
    private bool _disposed;
    
    public bool Running { get; private set; }
    public bool Stopping { get; private set; }
    public bool Stopped { get; private set; }
    public bool Faulted { get; private set; }
    public int MaxVideoFramesQueued { get; private set; }

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

        if (VideoConfig.UseColorspaceInformation)
        {
            frame->color_primaries = VideoConfig.ColorPrimaries;
            frame->color_trc = VideoConfig.ColorTrc;
            frame->colorspace = VideoConfig.Colorspace;
        }
                
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

                if (VideoConfig.UsePooledDataBuffers)
                    _videoDataBufferPool.Push(buffer);

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
                        RescalePacketTimestamps(packet, _videoTimeBase, _videoStream->time_base);
                        SubmitPacket(packet);
                        packet = null;
                        continue;
                    }
                    
                    // No packet retrieved because encoder needs more input data, repeat outer loop to get next frame.
                    // FFmpeg.AutoGen has EAGAIN as positive, but the actual return code is negative; negate it.
                    if (ret == -ffmpeg.EAGAIN) break;
                    
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

    // Should only be called when encoder is stopping and video codec is ready to stop. Sends the flush command to the
    // codec and empties any buffered packets to the packet queue.
    private void DrainVideoCodec()
    {
        AVPacket* packet = null;

        try
        {
            if (ffmpeg.avcodec_send_frame(_videoCodecContext, null) < 0)
                throw new EncoderException("Failed to send flush packet to video codec.");

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
                    SubmitPacket(packet);
                    packet = null;
                    continue;
                }

                throw new EncoderException("Error receiving packet during video codec draining.");
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
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
        
        // Open the AVIO context for the muxer. This was allocated by the output context when it was created, and put
        // into the 'pb' field, so we grab it from there. Have to make it a local so we can take its address.
        var ctx = _outputFormatContext->pb;
        if (ffmpeg.avio_open(&ctx, outputPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
            throw new EncoderException("Failed to open output AVIO context. Given filepath: " + outputPath);
        _outputFormatContext->pb = ctx;
        
        // This may or may not actually write anything to a file, but this must be called to initialize the muxer.
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
            // Release an extra permit so that the video codec thread dequeues nothing when it runs out, allowing it to
            // see that it's stopping and break.
            _videoDataSubmitted.Release();
            WaitTaskCatch(_videoCodecThread);

            // Empty anything remaining in the video codec, unless hard stop was triggered.
            if (!_hardStop) DrainVideoCodec();

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
        
        // If true, you must use GetVideoDataBuffer to get the byte[] you pass into SubmitVideoData. These will be 
        // drawn from an automatically-expanding pool to ensure the buffers remain allocated for use and reuse across
        // the whole lifetime of the Encoder, easing pressure on the garbage collector.
        bool UsePooledDataBuffers = true,
        
        // Maximum number of buffers which may be released at any given time. If a buffer is requested when already at
        // the limit, GetVideoDataBuffer will return null instead. User is responsible for waiting before asking again.
        // Set to 0 to have no limit.
        int PoolDepth = 0,
        
        // Setting this to false makes the encoder ignore the next 3 entries, letting libav deal with it automatically.
        bool UseColorspaceInformation = true,
        
        // Extra information about source colorspace. Defaults are good for sRGB color input.
        // If you're getting RGB pixel data from somewhere, it's probably sRGB. This is a packed format where each
        // color channel (red, green, blue) has only one byte of linear data to indicate its strength. This is *fine,*
        // but colors can be much more precisely stored than that (and in fact, GPUs and shaders work with them in the
        // form of floats), so sRGB color input needs to be converted to the format actually in use. This informs 
        // the Encoder how to do so for the input data it will be receiving.
        AVColorPrimaries ColorPrimaries = AVColorPrimaries.AVCOL_PRI_BT709,
        AVColorTransferCharacteristic ColorTrc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
        AVColorSpace Colorspace = AVColorSpace.AVCOL_SPC_RGB
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

    public class EncoderException(string message) : Exception(message);
}