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
        SetLibAvLogLevel();
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
    }

    public static void SetLibAvLogLevel() => ffmpeg.av_log_set_level(Cordyceps2Settings.LibAvLogLevelInt);

    private static void Log(string str) => Debug.Log($"[Cordyceps2/Recording] {str}");
    private static void LogLibAv(string str) => Debug.Log($"[Cordyceps2/Recording/libav] {str}");
}