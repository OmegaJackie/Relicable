using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.External.Ipc;

namespace Relicable.External;

// Hardened wrapper over the vnavmesh IPC surface (confirmed from IPCProvider.cs).
//
// Per-frame cost: status gates (IsReady, PathfindInProgress, IsRunning) are read
// through Cached so polling them every tick collapses to one call per TTL window.
//
// Command re-firing: MoveTo/MoveCloseTo are edge-triggered on destination, and a
// move is only re-issued when the destination changes OR movement has stopped
// short. Calling them every tick with the same destination is a no-op.
//
// Readiness: every gate is guarded by ICallGateSubscriber.HasFunction (Funcs) or
// HasAction (the void Path.Stop), so a
// missing or unloaded vnavmesh never throws; calls become no-ops/fallbacks.
public sealed class NavmeshIpc
{
    private readonly ICallGateSubscriber<bool> _isReady;
    private readonly ICallGateSubscriber<Vector3?> _flagToPoint;
    private readonly ICallGateSubscriber<Vector3, bool, bool> _moveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _moveCloseTo;
    private readonly ICallGateSubscriber<bool> _pathfindInProgress;
    private readonly ICallGateSubscriber<object> _stop;
    private readonly ICallGateSubscriber<bool> _isRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> _nearestPoint;
    private readonly ICallGateSubscriber<float> _buildProgress;
    private readonly ICallGateSubscriber<bool> _reload;

    private readonly Cached<bool> _isReadyCache;
    private readonly Cached<bool> _pathfindCache;
    private readonly Cached<bool> _runningCache;

    // Edge-trigger keyed on the last move request. We re-issue only on change.
    private (Vector3 dest, bool fly, float range)? _lastMove;
    // When the destination is unchanged but vnav has stopped, re-issue at most this
    // often (ms) so a stopped-short move still recovers without per-tick re-path stutter.
    private const long RetryMs = 1000;
    private long _lastIssueTicks;

    public NavmeshIpc(IDalamudPluginInterface pi)
    {
        _isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _flagToPoint = pi.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
        _moveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        _moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _isRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        _nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        _buildProgress = pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        _reload = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.Reload");

        _isReadyCache = new Cached<bool>(() => Safe(_isReady, false), 100);
        _pathfindCache = new Cached<bool>(() => Safe(_pathfindInProgress, false), 15);
        _runningCache = new Cached<bool>(() => Safe(_isRunning, false), 15);
    }

    // True when the core gates are registered (vnavmesh loaded). Cheap; used by
    // the controller to decide whether navigation is available before starting.
    public bool Available => _moveCloseTo.HasFunction && _isReady.HasFunction;

    public bool IsReady() => _isReadyCache.Value;
    public bool PathfindInProgress() => _pathfindCache.Value;
    public bool IsRunning() => _runningCache.Value;

    // Navmesh build progress for the current zone: negative when no build is running,
    // otherwise a fraction in [0, 1] while the mesh is being built. The UI uses this
    // to show a "building X%" indicator so the player knows why navigation is waiting.
    public float BuildProgress()
    {
        if (!_buildProgress.HasFunction)
            return -1f;
        try { return _buildProgress.InvokeFunc(); }
        catch { return -1f; }
    }

    // Reload the current zone's navmesh (from cache when available). Cancels in-flight
    // pathfinds and swaps in a fresh mesh, which clears most "stuck" states without a
    // full from-scratch rebuild. Returns false if vnavmesh is unavailable.
    public bool Reload()
    {
        if (!_reload.HasFunction)
            return false;
        try { return _reload.InvokeFunc(); }
        catch { return false; }
    }

    public Vector3? FlagToPoint()
    {
        if (!_flagToPoint.HasFunction)
            return null;
        try { return _flagToPoint.InvokeFunc(); }
        catch { return null; }
    }

    // Re-path only when the destination moves more than this (yalms). Chasing a live
    // target jitters its position slightly every tick; without a deadband we restart
    // pathfinding constantly and vnav rejects the overlapping requests with
    // "Pathfinding task is in progress". 2y is well inside the executors' stop range.
    private const float RepathThreshold = 2f;

    public void MoveCloseTo(Vector3 dest, bool fly, float range)
    {
        // A pathfind is still being computed. Issuing another now is what produces
        // vnav's "Pathfinding task is in progress" error, so wait for it to finish
        // before sending a new request.
        if (PathfindInProgress())
            return;

        var req = (dest, fly, range);
        // Re-issue only when the request meaningfully changed (destination moved past
        // the deadband, or flight/range changed) or movement has stopped short. While
        // still running toward an effectively-unchanged destination, leave it be.
        var changed = _lastMove is not { } l
            || Vector3.DistanceSquared(l.dest, dest) > RepathThreshold * RepathThreshold
            || l.fly != fly
            || Math.Abs(l.range - range) > 0.5f;
        if (!changed)
        {
            // Already following this destination: leave it be.
            if (IsRunning())
                return;
            // Stopped with an unchanged destination (arrived within range, or vnav gave
            // up short). Re-issuing every tick re-paths a hair repeatedly and is what
            // shows up as the back-and-forth stutter; nudge at most once a second so a
            // genuine stopped-short still recovers without the spam.
            if (Environment.TickCount64 - _lastIssueTicks < RetryMs)
                return;
        }

        if (!_moveCloseTo.HasFunction)
            return;
        try
        {
            _moveCloseTo.InvokeFunc(dest, fly, range);
            _lastMove = req;
            _lastIssueTicks = Environment.TickCount64;
            _pathfindCache.Invalidate();
            _runningCache.Invalidate();
            Diagnostics.DebugLog.Verbose($"vnav -> MoveCloseTo {dest:0.0} fly={fly} range={range:0.0}");
        }
        catch { /* unavailable; retry next tick */ }
    }

    public void MoveTo(Vector3 dest, bool fly)
    {
        if (!_moveTo.HasFunction)
            return;
        try { _moveTo.InvokeFunc(dest, fly); }
        catch { /* unavailable */ }
    }

    // Nearest landable floor point on the navmesh near 'near'. Used to find a spot
    // to descend to and dismount when the destination/flag sits over a cliff, water,
    // or other non-landable terrain. allowUnlandable=false restricts to spots the
    // character can actually stand on. Returns null if vnavmesh is unavailable or
    // the mesh has no candidate within the search box.
    public Vector3? PointOnFloor(Vector3 near, bool allowUnlandable, float halfExtentXZ)
    {
        if (!_pointOnFloor.HasFunction)
            return null;
        try { return _pointOnFloor.InvokeFunc(near, allowUnlandable, halfExtentXZ); }
        catch { return null; }
    }

    // Nearest point on the navmesh within the given search box; a looser fallback
    // than PointOnFloor when no strictly-landable floor point is found.
    public Vector3? NearestPoint(Vector3 near, float halfExtentXZ, float halfExtentY)
    {
        if (!_nearestPoint.HasFunction)
            return null;
        try { return _nearestPoint.InvokeFunc(near, halfExtentXZ, halfExtentY); }
        catch { return null; }
    }

    // Sentinel probe height for resolving a MAP-derived coordinate (authored spawn or
    // flag world position) to a real navmesh floor. Such coordinates carry no usable
    // height and arrive with Y = 0.
    //
    // vnavmesh's Query.FindPointOnFloor searches a tall column but keeps only floor
    // polygons at or below the probe's Y, returning the highest one; probing from
    // Y = 0 therefore rejects every floor above sea level (most of the game) and the
    // snap fails. vnavmesh's own MapUtils.FlagToPoint avoids this by probing from
    // Y = 1024, so we match that value for consistency.
    private const float FloorProbeHeight = 1024f;

    // Resolve a map-derived world XZ (Y ignored) to a real, landable navmesh floor
    // point. This is the correct way to consume an authored spawn coordinate or a flag
    // world position, both of which have no meaningful Y. It probes from a high Y so
    // FindPointOnFloor returns the true ground, preferring a reachable floor, then an
    // unreachable one, then the nearest mesh point across the full vertical range.
    //
    // Returns null only when the XZ column contains no navmesh at all. Callers must NOT
    // substitute the raw Y = 0 point in that case: navigating there is exactly what
    // sent the character underground and onto non-landable terrain.
    public Vector3? FloorForMapPoint(Vector3 worldXZ, float halfExtentXZ = 30f)
    {
        var probe = new Vector3(worldXZ.X, FloorProbeHeight, worldXZ.Z);
        return PointOnFloor(probe, false, halfExtentXZ)
            ?? PointOnFloor(probe, true, halfExtentXZ)
            ?? NearestPoint(probe, halfExtentXZ, FloorProbeHeight);
    }

    // Resolve a map-derived world XZ to a LANDABLE navmesh floor ONLY -- the correct resolver for a
    // NAVIGATION destination. Unlike FloorForMapPoint, it never falls back to an unreachable floor or
    // the nearest mesh point: those fallbacks can return an out-of-bounds spot (e.g. a lake bottom far
    // below the shore), and flying to it sends the character out of bounds. When the authored point
    // sits just off the walkable area (over water/void) the landable floor is nearby, so the search is
    // widened before giving up; a null result means the caller must NOT navigate (hold / ask for a
    // flag) rather than head to a guessed point.
    public Vector3? LandableFloorForMapPoint(Vector3 worldXZ)
    {
        var probe = new Vector3(worldXZ.X, FloorProbeHeight, worldXZ.Z);
        foreach (var ext in LandableSearchExtents)
        {
            var hit = PointOnFloor(probe, false, ext);
            if (hit is { } p)
                return p;
        }
        return null;
    }

    // Widening XZ half-extents for the landable-floor search: start tight (the authored point is
    // usually on or beside the spawn), then widen to catch a coordinate that sits over a water/void
    // gap next to the walkable shore. Kept modest so it snaps to the NEARBY shore, not a distant
    // unrelated floor.
    private static readonly float[] LandableSearchExtents = { 30f, 60f, 90f };

    public void Stop()
    {
        _lastMove = null;
        if (!_stop.HasAction)
            return;
        try { _stop.InvokeAction(); }
        catch { /* unavailable */ }
        _runningCache.Invalidate();
        _pathfindCache.Invalidate();
    }

    private static bool Safe(ICallGateSubscriber<bool> gate, bool fallback)
    {
        if (!gate.HasFunction)
            return fallback;
        try { return gate.InvokeFunc(); }
        catch { return fallback; }
    }
}
