using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Cordyceps2;

// Must be attached to the game's AudioListener
public class AudioCapture : MonoBehaviour
{
    private int _requestedSamples;
    private long _lastRequestTimestamp;

    private byte[] _submitBuffer;
    private int _filledBytes;

    private CircularSampleBuffer _idleExcessBuffer; // Excess buffer used when there's no request
    private bool _idle = true; // Flag indicating if there was no request last read
    private float[] _continuousExcessBuffer; // Excess buffer used when there are continuous requests
    private int _lastReadExcess; // Number of excess samples in the continuous buffer from the last read 
    
    // Portion of samples read each frame that are considered "new", this is not necessarily *every* sample read that
    // frame, as playing in slow motion will extend the length of every playing audio source. For example, playing the
    // game at half speed doubles the true, real-time length of every sound, meaning we only take half of read samples.
    private float[] _intermediateSampleBuffer;
    private int _intermediateSampleCount;
    private float _intermediateSampleCounter;

    private int _sampleRate;
    
    public void RequestSamples(int count)
    {
        _lastRequestTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _requestedSamples, count);
    }

    private void Awake()
    {
        var config = AudioSettings.GetConfiguration();
        
        if (config.speakerMode != AudioSpeakerMode.Stereo) 
            Log("WARN - Speaker mode was not stereo, that shouldn't be possible! Audio capture assumes number of " +
                "channels is always 2, so this is going to break something.");
        
        // dspBufferSize is the number of samples per channel per audio frame. Audio capture will assume there are
        // always 2 channels, as this is the default, and there's no reason it should be changed.
        var samplesPerFrame = config.dspBufferSize * 2;
        _idleExcessBuffer = new CircularSampleBuffer(samplesPerFrame);
        _continuousExcessBuffer = new float[samplesPerFrame];
        _intermediateSampleBuffer = new float[samplesPerFrame];
        
        _sampleRate = config.sampleRate;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        // Consider the following:
        //  This is an exaggerated depiction of the timeline of game ticks (top) versus the timeline of audio filter
        // read events (bottom).
        // +----------------------------------+----------------------------------+----------------------------------+
        // |      |      |      |      |      |      |      |      |      |      |      |      |      |      |      |
        // +----------------------------------+----------------------------------+----------------------------------+
        // |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |
        // +----------------------------------+----------------------------------+----------------------------------+
        //  These events are asynchronous, and not occurring at the same rate. Audio sample requests can only be made
        // on a game tick, but can only be processed on an audio filter read event. When a sample request is made, it
        // is asking for the audio *starting on that game tick,* not whatever audio happens to be processed next audio
        // filter read. This complicates matters, and mandates a solution.
        //  That solution is the main source of complexity below. First, when any audio data is read in excess of what
        // was requested, that excess is saved to a buffer. On any given read, if there is a request, we check the
        // timestamp of it and compare it to the timestamp of the read. A corresponding number of samples are then
        // pulled from the last-read buffer to account for the desync. 
        //  This is only necessary if performing a read when there were previously zero samples collected. If samples
        // are requested continuously across a period from then on, the entirety of the excess buffer can simply be 
        // pulled each read.
        //  This version of the timelines is highlighted. A sequence of the same number are a single game tick, 'L's 
        // are samples read from the excess buffer, 'R's are samples read from the current buffer, and 'E's are excess
        // samples placed into the excess buffer.
        // +----------------------------------+------111111122222223333333444444455555556666666---------------------+
        // |      |      |      |      |      |      |      |      |      |      |      |      |      |      |      |
        // +----------------------------------+------LLLRRRRE-----REEEE---LLRRRRR+----RREEE-------------------------+
        // |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |
        // +----------------------------------+-------------LRRRRR-LLLLRRREE-----RRRRR--LLLRRRR---------------------+
        
        // All the following need to be saved at the start of the read, in the event they change mid-function. This is
        // guaranteed for the timestamp, and unlikely but possible for timeFactor and currentRequest.
        var timestamp = Stopwatch.GetTimestamp();
        var timeFactor = 1.0f; // TODO: Set to the time dialation factor from TimeControl when that's added
        // TODO: Time dialation factor should be set at the START OF EACH UPDATE ONLY, not on raw updates, and this is
        //  what AudioCapture should check!
        var currentRequest = _requestedSamples;

        // If timeFactor was 0, no actual audio played this frame, so we don't do anything.
        if (timeFactor == 0.0f) return;

        // Collect only a fraction of the read samples equal to the current time factor.
        _intermediateSampleCount = 0;
        for (var i = 0; i < data.Length; i += 2)
        {
            _intermediateSampleCounter += timeFactor;
            if (_intermediateSampleCounter >= 1.0f)
            {
                _intermediateSampleCounter--;
                _intermediateSampleBuffer[_intermediateSampleCount] = data[i];
                _intermediateSampleBuffer[_intermediateSampleCount + 1] = data[i + 1];
                _intermediateSampleCount += 2;
            }
        }
        
        // TODO: Code currently assumes that the audio framerate is never any less than half the game's base tickrate.
        //  This is NOT necessarily true if there are factors affecting the base tickrate, such as a mushroom or an
        //  echo. Need to refactor some to account for this; main thing should be that the continuous excess buffer
        //  should be a List<byte> rather than a fixed-size array.
        
        if (currentRequest == 0)
        {
            // TODO: If there is any excess, fill the intermediate into the excess buffer and return without going idle
            _idleExcessBuffer.Push(_intermediateSampleBuffer, 0, _intermediateSampleCount);
            _idle = true;
            return;
        }

        if (_idle)
        {
            _idle = false;
            var deltaTime = (float)(timestamp - _lastRequestTimestamp) / Stopwatch.Frequency;
            var extraSamples = (int)(deltaTime * _sampleRate);
            // A game tick is always longer than an audio frame so we can safely pop the maximum number of extra
            // samples every time, since a request from idle will always be >= the size of the idle excess buffer.
            PopIdleExcessBufferToSubmitBuffer(extraSamples * 2); // Double because there are 2 channels
            currentRequest -= extraSamples;
            Interlocked.Add(ref _requestedSamples, -extraSamples);
        }
        else if (_lastReadExcess > 0)
        {
            var excessFillAmount = Math.Min(_lastReadExcess, currentRequest);
            FillSubmitBuffer(_continuousExcessBuffer, excessFillAmount * 2);
            _lastReadExcess -= excessFillAmount;
            if (_lastReadExcess != 0) 
                Log("WARN - Continuous excess sample count was not 0 after being used to fill current request. " +
                    $"Number of unspent excess samples is {_lastReadExcess}. This shouldn't be able to happen, but " +
                    "should only result in a minor audio desync, assuming nothing worse is occurring.");
            currentRequest -= excessFillAmount;
            Interlocked.Add(ref _requestedSamples, -excessFillAmount);
        }

        var intermediateFillAmount = Math.Min(_intermediateSampleCount, currentRequest);
        FillSubmitBuffer(_intermediateSampleBuffer, intermediateFillAmount * 2);
        Interlocked.Add(ref _requestedSamples, -intermediateFillAmount);
        var excessSamples = _intermediateSampleCount - intermediateFillAmount;
        Array.Copy(_intermediateSampleBuffer, _intermediateSampleCount - excessSamples,
            _continuousExcessBuffer, 0, excessSamples);
        _lastReadExcess = excessSamples;
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

    // Pops directly from the circular idle excess buffer to the submit buffer without needing a temporary array.
    private void PopIdleExcessBufferToSubmitBuffer(int count)
    {
        while (count > 0)
        {
            // Assumes there will always be an audio data buffer returned, which there should be, as I'm not going to
            // cap the pool depth for it, or provide a setting to do so.
            _submitBuffer ??= Recording.Encoder.GetAudioDataBuffer();

            // Number of floats to put into submit buffer this iteration
            var fillAmount = Math.Min(count, (_submitBuffer.Length - _filledBytes) / 4);
            count -= fillAmount;

            _idleExcessBuffer.PopBytes(_submitBuffer, _filledBytes, fillAmount);
            _filledBytes += fillAmount * 4;

            if (_filledBytes != _submitBuffer.Length) continue;
            
            Recording.Encoder.SubmitAudioData(_submitBuffer);
            _submitBuffer = null;
            _filledBytes = 0;
        }
    }

    // Zeroes out any un-filled space in the submit buffer, then submits it.
    public void FlushSubmitBuffer()
    {
        FillSubmitBuffer(_continuousExcessBuffer, _lastReadExcess);
        _lastReadExcess = 0;
        if (_submitBuffer == null) return;
        Array.Clear(_submitBuffer, _filledBytes, _submitBuffer.Length - _filledBytes);
        Recording.Encoder.SubmitAudioData(_submitBuffer);
        _submitBuffer = null;
        _filledBytes = 0;
    }

    private class CircularSampleBuffer(int size)
    {
        private readonly float[] _buffer = new float[size];
        private int _top;

        public void Push(float[] source, int start, int count)
        {
            var remainingSpace = size - _top;
        
            if (count < remainingSpace) 
                Array.Copy(source, start, _buffer, _top, count);
            else
            {
                Array.Copy(source, start, _buffer, _top, remainingSpace);
                Array.Copy(source, start + remainingSpace, _buffer, 0, 
                    count - remainingSpace);
            }
        
            _top = (_top + count) % size;
        }
        
        // Elements are copied to dest such that the first copied element is the oldest element popped.
        // count is a number of samples/floats, *not* a number of bytes; it is multiplied by 4 when input as the length
        // argument for the copy operations.
        public void PopBytes(byte[] dest, int start, int count)
        {
            if (count < _top)
                Buffer.BlockCopy(_buffer, _top - count, dest, start, 
                    count * 4);
            else
            {
                Buffer.BlockCopy(_buffer, size - count + _top, dest, start,
                    (count - _top) * 4);
                Buffer.BlockCopy(_buffer, 0, dest, start + count - _top, 
                    _top * 4); // Length 0 copy is valid so this works if Top is 0
            }

            _top = (_top + size - count) % count;
        }
    }
    
    private static void Log(string str) => Debug.Log($"[Cordyceps2/Recording/AudioCapture] {str}");
}