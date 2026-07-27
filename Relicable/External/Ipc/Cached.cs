using System;

namespace Relicable.External.Ipc;

// TTL-based memoization for IPC query gates. The controller tick polls status
// gates (IsRunning, PathfindInProgress, IsStopped, IsBusy) every frame; without
// this, that is one cross-plugin call per gate per frame. Cached collapses
// repeated reads inside the TTL window to a single underlying invocation, which
// is the primary defense against per-frame IPC cost.
//
// TTL guidance:
//   ~15 ms  : status a step polls and acts on within the same frame
//   ~50 ms  : slowly-changing state (duty stopped, plugin busy)
//   0 ms    : always recompute (effectively disables caching)
public sealed class Cached<T>
{
    private readonly Func<T> _compute;
    private readonly long _ttlMs;
    private long _stamp = long.MinValue;
    private T _value = default!;

    public Cached(Func<T> compute, long ttlMs)
    {
        _compute = compute;
        _ttlMs = ttlMs;
    }

    public T Value
    {
        get
        {
            var now = Environment.TickCount64;
            if (_stamp == long.MinValue || now - _stamp >= _ttlMs)
            {
                _value = _compute();
                _stamp = now;
            }
            return _value;
        }
    }

    // Force the next read to recompute (for example after issuing a command that
    // changes the polled state).
    public void Invalidate() => _stamp = long.MinValue;
}
