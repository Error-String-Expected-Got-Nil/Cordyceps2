using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Cordyceps2;

// Terminology note:
// This assumes the game's audio is always in stereo, which it should be.
// Sample = 2 floats, 1 per channel
// Float = 1 float, half of a sample

// Must be attached to the game's AudioListener
public class AudioCapture : MonoBehaviour
{
    private int _requestedSamples;

    private byte[] _submitBuffer;
    private int _filledBytes;

    private float[] _sampleBuffer; // Intermediate buffer for samples copied from each filter read
    private double _sampleAccum; // Accumulator for tracking when to take the next sample
    
    public int SampleRate { get; private set; }

    private bool _debug;
    private byte[] _debugBuffer;
    private FileStream _debugOutput;
    public int _debugSamples;

    public void RequestSamples(int count)
    {
        Interlocked.Add(ref _requestedSamples, count);
    }
    
    private void Awake()
    {
        var config = AudioSettings.GetConfiguration();
        
        if (config.speakerMode != AudioSpeakerMode.Stereo) 
            Log("WARN - Speaker mode was not stereo, that shouldn't be possible! Audio capture assumes number of " +
                "channels is always 2, so this is going to break something.");

        var samplesPerFrame = config.dspBufferSize * 2;
        _sampleBuffer = new float[samplesPerFrame];
        
        SampleRate = config.sampleRate;
        
        // TODO: DEBUG
        Log($"dspBufferSize: {config.dspBufferSize}");
        Log($"sampleRate: {config.sampleRate}");
        _debugBuffer = new byte[samplesPerFrame * 4];
    }
    
    // TODO: Mostly works! But tick advance and very very low tickrates seem to result in staticky popping, not sure
    //  exactly why, but probably indicative of sample sequences that are supposed to be contiguous getting clipped.
    //  Possibly need to synchronize ArtificialTimeFactor more carefully?
    private void OnAudioFilterRead(float[] data, int channels)
    {
        // Values that are taken/modified by other threads and therefore may change during execution, so we save them
        // at the start for use during this read.
        var currentRequest = _requestedSamples;
        var timeFactor = TimeControl.ArtificialTimeFactor;

        // Do nothing if time is stopped, since we won't be reading any samples anyway.
        if (timeFactor == 0.0f) return;

        // TODO: Debug code has revealed some interesting results.
        //  - Strange "flat" sections in the audio track that appear to be garbage are almost exactly 1024 samples long
        //  - Sample request count is INCREASING over time! It *should* hover around 0, so what's going on there?
        //  - The recorded audio track is shorter than the recorded video track by precisely the amount of un-recorded
        //    samples.
        //  - Sample request count behaves as-expected for continuous recording, but not tick-advance.
        //  Possible desync. Seems like audio capture is NOT reading samples when it should, and occasionally reads
        //  garbage, possibly as a direct result of this. Supports ArtificialTimeFactor desync theory?
        if (_debug)
        {
            Log($"DEBUG - samples = {_debugSamples}; request = {currentRequest}; excess = {1024 - currentRequest}; " +
                $"write = {(currentRequest > 0 ? "yes" : "no")}");
        }
        
        // Also do nothing if there's no request. Attempt at simplification compared to previous version: Don't bother
        // saving any samples if there's no request, it may not actually be necessary.
        if (currentRequest <= 0) return;
        
        if (_debug)
        {
            _debugSamples += 1024;
            Buffer.BlockCopy(data, 0, _debugBuffer, 0, _debugBuffer.Length);
            _debugOutput.Write(_debugBuffer, 0, _debugBuffer.Length);
        }
        
        var floatCount = 0;
        for (var i = 0; i < data.Length; i += 2)
        {
            _sampleAccum += timeFactor;
            
            if (_sampleAccum < 1.0) continue;
            
            _sampleAccum -= 1.0;
            _sampleBuffer[floatCount] = data[i];
            _sampleBuffer[floatCount + 1] = data[i + 1];
            floatCount += 2;
        }
        
        // Another simplification: If there is a request, we submit all recorded samples and decrement the request
        // amount by that much, even into the negatives. Theory being that it doesn't really matter if we give too much
        // data since it's likely that the next frame is going to be asking for it anyway.
        // TODO: Reevaluate that assumption, it's possible that may not hold when considering tick advance
        FillSubmitBuffer(_sampleBuffer, floatCount);
        Interlocked.Add(ref _requestedSamples, -(floatCount / 2));
    }
    
    // Pulls data from source and copies it into the audio data submit buffer until either source is empty or the buffer
    // is full. If the buffer fills, it submits it, then gets a new one, and continues pulling from source. 
    //
    // start is the index in source to start copying from
    // count is number of floats; should be a multiple of the number of channels
    private void FillSubmitBuffer(float[] source, int count)
    {
        while (count > 0)
        {
            // Assumes there will always be an audio data buffer returned, which there should be, as I'm not going to
            // cap the pool depth for it, or provide a setting to do so.
            _submitBuffer ??= Recording.Encoder.GetAudioDataBuffer();

            // Number of floats to put into submit buffer this iteration
            var fillAmount = Math.Min(count, (_submitBuffer.Length - _filledBytes) / 4);
            count -= fillAmount;
            
            Buffer.BlockCopy(source, 0, _submitBuffer, _filledBytes, fillAmount * 4);
            _filledBytes += fillAmount * 4;

            if (_filledBytes != _submitBuffer.Length) continue;
            
            Recording.Encoder.SubmitAudioData(_submitBuffer);
            _submitBuffer = null;
            _filledBytes = 0;
        }
    }
    
    public void FlushSubmitBuffer()
    {
        _requestedSamples = 0;
        if (_submitBuffer == null) return;
        Array.Clear(_submitBuffer, _filledBytes, _submitBuffer.Length - _filledBytes);
        Recording.Encoder.SubmitAudioData(_submitBuffer);
        _submitBuffer = null;
        _filledBytes = 0;
    }

    public void BeginDebug(string filename)
    {
        _debugSamples = 0;
        _debugOutput = File.Create(@"C:\cordyceps2\" + filename);
        _debug = true;
    }

    public void EndDebug()
    {
        _debugOutput.Close();
        _debugOutput = null;
        _debug = false;
    }

    private static void Log(string str) => Debug.Log($"[Cordyceps2/Recording/AudioCapture] {str}");
}