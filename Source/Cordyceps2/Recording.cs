using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cordyceps2;

// Handles starting, stopping, initialization, and passing of raw frame data to the encoder for recording.
// See the Encoder class for the code actually responsible for encoding and saving the data to a video.
public static class Recording
{
    private static double _frameRequestCounter;
    private static VideoCapture _videoCapture;

    public static RecordStatus Status { get; private set; } = RecordStatus.Stopped;
    public static decimal RecordTime { get; private set; }
    public static Encoder Encoder { get; private set; }
    
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

    public static void StartRecording()
    {
        Log("Attempting to start recording.");
        
        var path = Cordyceps2Settings.RecordingOutputDirectory.Value;
        if (!Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception e)
            {
                Log("ERROR - Failed to create directory with path: \"" + path + "\", it was probably invalid.");
                Log("Exception: " + e);
                return;
            }
        }

        path = Path.Combine(path, GetFilename());

        try
        {
            var file = new FileStream(path, FileMode.CreateNew);
            file.Close();
        }
        catch (Exception e)
        {
            Log("ERROR - Failed to create file to test if target directory was writable. Full path: \"" + path +
                "\". Most likely, the program was not allowed to write to this path.");
            Log("Exception: " + e);
            return;
        }

        
        // TODO: For audio, make alternate form to create with audio settings if audio recording is enabled
        try
        {
            Encoder = new Encoder(GetVideoSettings(), Cordyceps2Settings.DoProfiling.Value);
        }
        catch (Encoder.EncoderException e)
        {
            Log($"ERROR - Failed to construct encoder instance. Exception: {e}");
            return;
        }

        Encoder.OnFault += Notify_EncoderFault;

        // Need to attach the video capture script to *something,* and whatever object holds the mod's plugin is
        // probably fine. I don't think it should matter where it is, it just needs to exist somewhere.
        // TODO: Also add condition to add audio capture when that's implemented
        _videoCapture = Cordyceps2Main.Instance.gameObject.AddComponent<VideoCapture>();

        try
        {
            Encoder.Start(path);
        }
        catch (Encoder.EncoderException e)
        {
            Log($"ERROR - Failed to call start on encoder. Exception: {e}");
            Encoder.Dispose();
            return;
        }
        
        Log("Successfully started recording, output path: \"" + path + "\"");
        Status = RecordStatus.Recording;
    }

    private static string GetFilename() => "Cordyceps2 " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4";

    private static Encoder.VideoSettings GetVideoSettings()
    {
        var inputSize = Cordyceps2Settings.GetRecordingInputResolution();
        var outputSize = Cordyceps2Settings.GetRecordingOutputResolution();
        return new Encoder.VideoSettings(
            (int)inputSize.x,
            (int)inputSize.y,
            (int)outputSize.x,
            (int)outputSize.y,
            AVPixelFormat.AV_PIX_FMT_RGBA,
            Cordyceps2Settings.RecordingFps.Value,
            Cordyceps2Settings.KeyframeInterval.Value,
            Cordyceps2Settings.ConstantRateFactor.Value,
            Cordyceps2Settings.EncoderPreset.Value,
            true,
            Cordyceps2Settings.VideoBufferPoolDepth.Value
        );
    }

    public static void StopRecording()
    {
        Status = RecordStatus.Stopping;
        Log("Stopping recording.");
        Encoder.Stop().ContinueWith(RecordStopCallback);

        return;
        
        void RecordStopCallback(Task task)
        {
            Log("Recording stopped.");
            Log("Total record time: " + InfoPanel.FormatTime(RecordTime));
            if (Encoder.HasProfiling)
            {
                Log("Total video frames encoded: " + Encoder.Frames);
                Log("Total video encode time: " + InfoPanel.FormatTime(Encoder.VideoEncodeTime));
                Log("Average video encode time per frame: " + (Encoder.VideoEncodeTime / Encoder.Frames * 1000)
                    .ToString("0.00") + "ms");
                Log("Relative video encode rate: " + (RecordTime / Encoder.VideoEncodeTime)
                    .ToString("0.00") + "x");
            
                // TODO: Print audio profiling data if present
            }
        
            Encoder.Dispose();
            Object.Destroy(_videoCapture);
            // TODO: If audio capture, also destroy audio capture component
            Status = RecordStatus.Stopped;
        }
    }
    
    public static void Notify_FrameDropped()
    {
        // TODO: Automatically pause game, show info panel, and put warning message in it if this happens
        RecordTime -= (decimal)1 / Cordyceps2Settings.RecordingFps.Value;
    }

    private static void Notify_EncoderFault(Encoder sender, string origin, AggregateException cause, Task stopTask)
    {
        // TODO: Automatically pause game, show info panel, and put warning message in it if this happens
        Status = RecordStatus.Stopping;
        Log($"ERROR - Encoder encountered a fault during operation, stopping recording. Location: {origin}");
        Log($"Exception: {cause}");
        stopTask.ContinueWith(RecordStopCallback);

        return;

        void RecordStopCallback(Task task)
        {
            Log("Recording stopped.");
            Encoder.Dispose();
            Object.Destroy(_videoCapture);
            // TODO: If audio capture, also destroy audio capture component
            Status = RecordStatus.Stopped;
        }
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
            
            if (requestCount <= 0) return;
            
            _videoCapture.RequestFrames(requestCount);
            RecordTime += (decimal)requestCount / Cordyceps2Settings.RecordingFps.Value;
            _frameRequestCounter -= requestCount;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception in MainLoopProcess_Update_Hook: {e}");
        }
    }
    
    public static void SetLibAvLogLevel()
    {
        if (!BinariesLoaded) return;
        ffmpeg.av_log_set_level(Cordyceps2Settings.LibAvLogLevelInt);
    }

    private static void Log(string str) => Debug.Log($"[Cordyceps2/Recording] {str}");
    private static void LogLibAv(string str) => Debug.Log($"[Cordyceps2/Recording/libav] {str}");
}