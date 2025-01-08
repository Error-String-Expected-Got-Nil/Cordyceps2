using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cordyceps2;

public class LibAvException(string message, int libavErrorCode)
    : Exception(message + " (libav error: " + TranslateLibAvError(libavErrorCode) + ")")
{
    public static unsafe string TranslateLibAvError(int errorCode)
    {
        const int bufsize = 1024;
        var buffer = stackalloc byte[bufsize];
        ffmpeg.av_strerror(errorCode, buffer, bufsize);
        return Marshal.PtrToStringAnsi((IntPtr)buffer);
    }
}