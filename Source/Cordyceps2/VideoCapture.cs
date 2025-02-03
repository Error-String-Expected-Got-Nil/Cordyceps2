using System;
using System.Collections;
using System.Threading;
using Unity.Collections;
using UnityEngine;

namespace Cordyceps2;

public class VideoCapture : MonoBehaviour
{
    private int _requestedFrames;

    public void RequestFrames(int frames) => Interlocked.Add(ref _requestedFrames, frames);

    private void LateUpdate()
    {
        if (Recording.Status != RecordStatus.Recording || _requestedFrames <= 0) return;
        Interlocked.Decrement(ref _requestedFrames);
        StartCoroutine(CaptureFrame());
    }

    private IEnumerator CaptureFrame()
    {
        yield return new WaitForEndOfFrame();
        var frame = ScreenCapture.CaptureScreenshotAsTexture();

        try
        {
            var buffer = Recording.Encoder.GetVideoDataBuffer();
            if (buffer == null)
            {
                Log("ERROR - Frame dropped due to null video data buffer. This means there were already too many " +
                    "video frames queued and another could not be accepted. Play the game at a lower speed, set " +
                    "the encoder to use a faster preset, or increase the video buffer pool depth.");
                Destroy(frame);
                Recording.Notify_FrameDropped();
                yield break;
            }

            var pixels = frame.GetPixelData<byte>(0);
            // NativeArray<byte> instance.CopyTo() uses the NativeArray's length for this, we'll instead use the
            // buffer's length just in case. It's also slighty faster since it skips a couple intermediate calls.
            NativeArray<byte>.Copy(pixels, 0, buffer, 0, buffer.Length);
            Recording.Encoder.SubmitVideoData(buffer);
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception while capturing video frame: {e}");
        }
        
        Destroy(frame);
    }
    
    // Yes, Rider, I know logging is expensive, but it will only be happening if things are already going wrong.
    // ReSharper disable Unity.PerformanceAnalysis
    private static void Log(string str) => Debug.Log($"[Cordyceps2/Recording/VideoCapture] {str}");
}