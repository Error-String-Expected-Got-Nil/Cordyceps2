using System.Threading;
using UnityEngine;

namespace Cordyceps2;

// Must be attached to the game's AudioListener
public class AudioCapture : MonoBehaviour
{
    private int _requestedSamples;

    public void RequestSamples(int count) => Interlocked.Add(ref _requestedSamples, count);
    
    
}