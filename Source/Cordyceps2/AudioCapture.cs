using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace Cordyceps2;

// Must be attached to the game's AudioListener
public class AudioCapture : MonoBehaviour
{
    private int _requestedSamples;
    private long _lastRequestTimestamp;

    private byte[] _submitBuffer;
    private int _filledBytes;
    
    public void RequestSamples(int count)
    {
        _lastRequestTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _requestedSamples, count);
    }

    private void Awake()
    {
        throw new NotImplementedException();
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        // Consider the following:
        //  This is an exaggerated depcition of the timeline of game ticks (top) versus the timeline of audio filter
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
        
        // TODO: Add flag to indicate last read buffer was filled from a no-request read, timestamp comparison only
        //  needs to be performed if that was the case.
        var timestamp = Stopwatch.GetTimestamp();
    }

    // Pulls data from source and copies it into the audio data submit buffer until either source is empty or the buffer
    // is full. If the buffer fills, it submits it, then gets a new one, and continues pulling from source. 
    //
    // start is the index in source to start copying from
    // count is number of floats; should be a multiple of the number of channels
    private void FillSubmitBuffer(float[] source, int start, int count)
    {
        while (count > 0)
        {
            // Assumes there will always be an audio data buffer returned, which there should be, as I'm not going to
            // cap the pool depth for it, or provide a setting to do so.
            _submitBuffer ??= Recording.Encoder.GetAudioDataBuffer();

            // Number of floats to put into submit buffer this iteration
            var fillAmount = Math.Min(count, (_submitBuffer.Length - _filledBytes) / 4);
            count -= fillAmount;
            
            Buffer.BlockCopy(source, start * 4, _submitBuffer, _filledBytes, fillAmount * 4);
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
        if (_submitBuffer == null) return;
        Array.Clear(_submitBuffer, _filledBytes, _submitBuffer.Length - _filledBytes);
        Recording.Encoder.SubmitAudioData(_submitBuffer);
        _submitBuffer = null;
        _filledBytes = 0;
    }
}