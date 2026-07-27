using System;
using System.Collections.Generic;

namespace Relicable.External.Ipc;

// Fire-on-change dispatch for IPC command gates. Executors call commands such as
// "enable rotation" or "move to destination" every tick; sending them every tick
// causes stutter (RSR re-arming) or movement restarts. EdgeTrigger remembers the
// last dispatched value and invokes the action only when the value changes,
// turning a level-triggered caller into an edge-triggered command stream.
public sealed class EdgeTrigger<T>
{
    private readonly Action<T> _action;
    private readonly IEqualityComparer<T> _comparer;
    private bool _hasLast;
    private T _last = default!;

    public EdgeTrigger(Action<T> action, IEqualityComparer<T>? comparer = null)
    {
        _action = action;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    // Dispatch only if value differs from the last dispatched value. Returns true
    // if the underlying action was actually invoked.
    public bool Dispatch(T value)
    {
        if (_hasLast && _comparer.Equals(_last, value))
            return false;

        _last = value;
        _hasLast = true;
        _action(value);
        return true;
    }

    // Forget the last value so the next Dispatch always fires. Use when the
    // external state may have changed underneath us (for example we left a duty).
    public void Reset()
    {
        _hasLast = false;
        _last = default!;
    }
}

// Comparer that treats two Vector3 destinations as equal when within a radius,
// so sub-tolerance jitter does not re-issue a move command.
public sealed class Vector3Proximity : IEqualityComparer<System.Numerics.Vector3>
{
    private readonly float _epsilonSq;
    public Vector3Proximity(float epsilon) => _epsilonSq = epsilon * epsilon;

    public bool Equals(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
        => System.Numerics.Vector3.DistanceSquared(a, b) <= _epsilonSq;

    public int GetHashCode(System.Numerics.Vector3 v) => 0; // force Equals path
}
