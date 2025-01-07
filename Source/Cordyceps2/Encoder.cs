using System;
using System.Collections.Concurrent;
using FFmpeg.AutoGen;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cordyceps2;

// This class is blanket unsafe since it's dealing with unmanaged libav data everywhere
public unsafe class Encoder : IDisposable
{
    public const AVCodecID VideoCodec = AVCodecID.AV_CODEC_ID_H264; 
    
    private readonly ConcurrentQueue<Texture2D> _videoDataQueue = new();
    private readonly ConcurrentQueue<Pointer<AVPacket>> _packetQueue = new();

    private readonly BufferedEventWaitHandle _videoDataSubmitted = new();
    
    private SwsContext* _frameFormatter;
    private AVCodec* _videoCodec;
    private AVCodecContext* _videoCodecContext;
    
    private bool _disposed;
    
    public bool Running { get; private set; }
    public bool Stopping { get; private set; }
    
    public Encoder(VideoSettings conf)
    {
        _frameFormatter = ffmpeg.sws_getContext(
            conf.VideoInputWidth, conf.VideoInputHeight, AVPixelFormat.AV_PIX_FMT_RGBA,
            conf.VideoOutputWidth, conf.VideoOutputHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
            ffmpeg.SWS_BILINEAR, null, null, null
        );
        
        _videoCodec = ffmpeg.avcodec_find_encoder(VideoCodec);
        if (_videoCodec == null) throw new EncoderException("Could not find codec with ID: " + VideoCodec);
        
        _videoCodecContext = ffmpeg.avcodec_alloc_context3(_videoCodec);
        if (_videoCodecContext == null) throw new EncoderException("Failed to allocate video codec context.");
        _videoCodecContext->width = conf.VideoOutputWidth;
        _videoCodecContext->height = conf.VideoOutputHeight;
        _videoCodecContext->time_base = new AVRational { num = 1, den = conf.Framerate };
        _videoCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _videoCodecContext->gop_size = conf.KeyframeInterval;
        _videoCodecContext->bit_rate = 0; // Bitrate is determined by CRF
        ffmpeg.av_opt_set_double(_videoCodecContext->priv_data, "crf", conf.ConstantRateFactor, 0);
        ffmpeg.av_opt_set(_videoCodecContext->priv_data, "preset", conf.Preset, 0);
    }

    public void SubmitVideoData(Texture2D data)
    {
        if (!Running || Stopping) return;
        
        _videoDataQueue.Enqueue(data);
        _videoDataSubmitted.Set();
    }
    
    private void VideoCodecThread()
    {
        try
        {
            
        }
        finally
        {
            
        }
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

            while (_videoDataQueue.TryDequeue(out var texture))
                Object.Destroy(texture);
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
        
        int Framerate,
        int KeyframeInterval,
        float ConstantRateFactor,
        string Preset
    );
}