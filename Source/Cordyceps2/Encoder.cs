using System;
using System.Collections.Concurrent;
using System.Threading;
using FFmpeg.AutoGen;

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
    
    // Wait events for data-consuming threads are handled by a SemaphoreSlim, which must have a set upper limit on the
    // maximum number of permitted releases. A "release" in this case indicates a data item in the queue waiting to be
    // processed. In this context, the limit is fine, since the queue should only grow beyond a small size if data
    // is being submitted faster than it is being consumed, which would be VERY BAD, and the encoder throwing an 
    // exception because of it is a good thing.
    public const int QueueLimit = 1024;
    
    private readonly ConcurrentQueue<byte[]> _videoDataQueue = new();
    private readonly ConcurrentQueue<Pointer<AVPacket>> _packetQueue = new();

    private readonly SemaphoreSlim _videoDataSubmitted = new(QueueLimit);
    private readonly SemaphoreSlim _packetSubmitted = new(QueueLimit);
    
    private readonly SwsContext* _frameFormatter;
    private readonly int _inputLinesize;
    private readonly AVCodecContext* _videoCodecContext;

    private long _framecount;
    
    private bool _forcedStop;
    private bool _disposed;
    
    public bool Running { get; private set; }
    public bool Stopping { get; private set; }

    public readonly VideoSettings VideoConfig;
    
    public Encoder(VideoSettings conf)
    {
        VideoConfig = conf;
        
        _frameFormatter = ffmpeg.sws_getContext(
            conf.VideoInputWidth, conf.VideoInputHeight, conf.InputPixelFormat,
            conf.VideoOutputWidth, conf.VideoOutputHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
            ffmpeg.SWS_BILINEAR, null, null, null
        );
        _inputLinesize = ffmpeg.av_image_get_linesize(conf.InputPixelFormat, conf.VideoInputWidth, 0);
        
        var videoCodec = ffmpeg.avcodec_find_encoder(VideoCodec);
        if (videoCodec == null) throw new EncoderException("Could not find codec with ID: " + VideoCodec);
        
        _videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
        if (_videoCodecContext == null) throw new EncoderException("Failed to allocate video codec context.");
        _videoCodecContext->width = conf.VideoOutputWidth;
        _videoCodecContext->height = conf.VideoOutputHeight;
        _videoCodecContext->time_base = new AVRational { num = 1, den = conf.Framerate };
        _videoCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _videoCodecContext->gop_size = conf.KeyframeInterval;
        _videoCodecContext->bit_rate = 0; // Bitrate is determined by CRF
        ffmpeg.av_opt_set_double(_videoCodecContext->priv_data, "crf", conf.ConstantRateFactor, 0);
        ffmpeg.av_opt_set(_videoCodecContext->priv_data, "preset", conf.Preset, 0);
        
        if (ffmpeg.avcodec_open2(_videoCodecContext, videoCodec, null) < 0)
            throw new EncoderException("Failed to open video codec.");
    }

    public void SubmitVideoData(byte[] data)
    {
        if (!Running || Stopping) return;
        
        _videoDataQueue.Enqueue(data);

        try
        {
            _videoDataSubmitted.Release();
        }
        catch (SemaphoreFullException)
        {
            throw new EncoderException("Video data input queue was overrun.");
        }
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

                if (_forcedStop) break;

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
        if (_forcedStop) return;
        
        _packetQueue.Enqueue(packet);

        try
        {
            _packetSubmitted.Release();
        }
        catch (SemaphoreFullException)
        {
            throw new EncoderException("Muxer packet data queue was overrun.");
        }
    }

    // TODO: Implement
    // Procedure something like: Set Stopping to true to stop taking input, signal codec and muxer threads one more
    // time so they clear their queue and see that they're finished.
    // Once codecs and muxer threads return, flush the codecs, finish writing file, etc.
    public void Stop()
    {
        throw new NotImplementedException();
    }
    
    // Synchronous hard stop, makes all codec and muxer threads stop at the first opportunity, WITHOUT ensuring the
    // video file is properly saved. Should only be called if the Encoder is disposed witout being stopped first.
    private void ForceStop()
    {
        // TODO: Implement
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
}