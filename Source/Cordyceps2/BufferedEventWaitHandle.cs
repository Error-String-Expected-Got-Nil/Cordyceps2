using System;
using System.Threading;

namespace Cordyceps2;

// Kind of like a semaphore but without an upper limit. This will count the number of times it has been set, and permit
// that many waits without blocking, and then start blocking again when it runs out. I feel like there must be a 
// technical term for this, but I couldn't find one, so I just made it myself.
// I don't know if this is safe with more than one thread waiting on it, I don't think it is, but for my purposes
// there only needs to be one thread waiting on one of these at any given time, which will work fine.
public sealed class BufferedEventWaitHandle : IDisposable
{
    private readonly object _key = new();
    private readonly EventWaitHandle _ewh = new(false, EventResetMode.AutoReset);
    
    private uint _permits;
    private bool _disposed;

    public void Set()
    {
        lock (_key)
        {
            _permits++;
            _ewh.Set();
        }
    }

    public void WaitOne()
    {
        var canContinue = true;
        
        lock (_key)
        {
            if (_permits > 0) _permits--;
            else canContinue = false;
        }

        if (canContinue) return;

        _ewh.WaitOne();
        lock (_key) _permits--;
    }
    
    public void Reset()
    {
        lock (_key)
        {
            _ewh.Reset();
            _permits = 0;
        }
    }

    public void Dispose()
    {
        DisposeInternal();
        GC.SuppressFinalize(this);
    }

    private void DisposeInternal()
    {
        if (_disposed) return;

        _ewh.Dispose();

        _disposed = true;
    }

    ~BufferedEventWaitHandle() => DisposeInternal();
}