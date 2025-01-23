using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using UnityEngine;

namespace Cordyceps2;

// Handles starting, stopping, initialization, and passing of raw frame data to the encoder for recording.
// See the Encoder class for the code actually responsible for encoding and saving the data to a video.
public static class Recording
{
    private static double _frameRequestCounter;
    private static VideoCapture _videoCapture;

    public static RecordStatus Status { get; private set; } = RecordStatus.Stopped;
    
    public static bool BinariesLoaded;
    
    public static unsafe void Initialize()
    {
        var libavPath = Path.Combine(Cordyceps2Main.ModPath, "libav");

        Log("Searching for binaries at path: " + libavPath);
        
        if (!Directory.Exists(libavPath))
        {
            Log("Failed to load libav binares, directory did not exist.");
            return;
        }

        ffmpeg.RootPath = libavPath;
        DynamicallyLoadedBindings.Initialize();
        
        Log("libav version: " + ffmpeg.av_version_info());

        Log("Setting libav log callback.");
        av_log_set_callback_callback logCallback = (p0, level, format, v1) =>
        {
            if (level > ffmpeg.av_log_get_level()) return;

            var linesize = 1024;
            var linebuffer = stackalloc byte[linesize];
            var printPrefix = 1;
            ffmpeg.av_log_format_line(p0, level, format, v1, linebuffer, linesize, &printPrefix);
            var line = Marshal.PtrToStringAnsi((IntPtr)linebuffer);

            LogLibAv(line);
        };
        
        ffmpeg.av_log_set_callback(logCallback);
        
        Log("Bindings initialized.");
        BinariesLoaded = true;
        SetLibAvLogLevel();
    }

    public static void SetLibAvLogLevel()
    {
        if (!BinariesLoaded) return;
        ffmpeg.av_log_set_level(Cordyceps2Settings.LibAvLogLevelInt);
    }

    public static void NotifyFrameDropped()
    {
        // TODO: Implement
    }

    // Hook responsible for making frame the requests that eventually result in data being submitted to the encoder.
    // Being a hook on MainLoopProcess.Update, it is inherently synced with the time control already.
    // Only runs if currently recording and the current true main loop is this process, to make sure this only runs
    // once per update.
    public static void MainLoopProcess_Update_Hook(On.MainLoopProcess.orig_Update orig, MainLoopProcess self)
    {
        orig(self);
        
        if (Status != RecordStatus.Recording) return;
        
        try
        {
            if (self.manager.currentMainLoop != self) return;

            var tickrate = self is RainWorldGame 
                ? TimeControl.UnmodifiedTickrate 
                : self.framesPerSecond;
            
            _frameRequestCounter += (double)Cordyceps2Settings.RecordingFps.Value / tickrate;
            var requestCount = (int)Math.Floor(_frameRequestCounter);
            if (requestCount > 0) _videoCapture.RequestFrames(requestCount);
            _frameRequestCounter -= requestCount;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception in MainLoopProcess_Update_Hook: {e}");
        }
    }

    private static void Log(string str) => Debug.Log($"[Cordyceps2/Recording] {str}");
    private static void LogLibAv(string str) => Debug.Log($"[Cordyceps2/Recording/libav] {str}");
}