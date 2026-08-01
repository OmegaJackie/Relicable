using System;
using System.Numerics;
using Relicable.Model;

namespace Relicable.Steps;

// Converts the in-game map flag to a navmesh point and travels there via vnavmesh,
// mounting for long hauls and flying when AllowFlight is enabled. Completes on
// ARRIVAL within stopDistance (+ slack), never merely on "vnav is idle": a move
// issued before the mesh was ready is swallowed, and idle-means-done then skipped
// the travel entirely (same fix as MoveToExecutor). MoveCloseTo is edge-triggered,
// so keeping the request alive every tick is cheap and recovers a swallowed issue.
public sealed class MoveToFlagExecutor : ITaskExecutor
{
    private const float ArrivalSlack = 5.0f;
    private const long IdleShortGraceMs = 15_000;

    public StepType Handles => StepType.MoveToFlag;

    private Vector3? _dest;
    private long _idleSinceTicks;
    // CombatAssist.DefendSelf's per-caller latch: the id we last armed the backend for, so the mode
    // is re-sent only when the attacker changes rather than every tick.
    private ulong _defendArmedId;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _idleSinceTicks = 0;
        _defendArmedId = 0;
        _dest = ctx.Navmesh.FlagToPoint();
        Issue(step, ctx);
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // This is the longest open-world leg in the run -- aetheryte to flag, routinely hundreds of
        // yalms through populated zones -- and it used to read no combat state at all. Two things
        // went wrong when something aggroed on the way: nothing ever fought back, and Mount.
        // EnsureMounted refuses to mount in combat, so the rest of the route was walked ON FOOT
        // with the mob in tow.
        //
        // DefendSelf issues its own Navmesh.Stop(); the per-tick Issue() below re-paths from
        // wherever the fight ended, so nothing else has to be undone. Clearing the idle clock is
        // load-bearing: it is wall-clock, so a fight longer than IdleShortGraceMs would otherwise
        // fail the step the moment we hand back, for a stall that never happened.
        if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
        {
            _idleSinceTicks = 0;
            return ExecutorStatus.InProgress;
        }

        if (!ctx.Navmesh.IsReady())
        {
            _idleSinceTicks = 0;
            return ExecutorStatus.InProgress; // mesh still loading
        }

        // Resolve lazily if the flag was not ready at Start.
        if (_dest is null)
        {
            _dest = ctx.Navmesh.FlagToPoint();
            Issue(step, ctx);
            return ExecutorStatus.InProgress;
        }

        var d = _dest.Value;
        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        if (me is { } m && Vector3.Distance(m, d) <= step.StopDistance + ArrivalSlack)
            return ExecutorStatus.Complete; // arrived

        // Mount for the long haul (vnavmesh does not mount on its own).
        Combat.Mount.EnsureMounted(ctx, me is { } mm ? Vector3.Distance(mm, d) : 0f);

        // Keep the request alive (no-op while running; re-issues a swallowed move).
        Issue(step, ctx);

        if (ctx.Navmesh.PathfindInProgress() || ctx.Navmesh.IsRunning())
        {
            _idleSinceTicks = 0;
            return ExecutorStatus.InProgress;
        }

        if (_idleSinceTicks == 0)
            _idleSinceTicks = Environment.TickCount64;
        if (Environment.TickCount64 - _idleSinceTicks > IdleShortGraceMs)
        {
            Diagnostics.DebugLog.Warn(
                $"MoveToFlag: vnav is idle short of the flag point {d:0.0} " +
                $"(stop {step.StopDistance:0.0}); failing so the run re-plans.");
            return ExecutorStatus.Failed;
        }
        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) => ctx.Navmesh.Stop();

    private void Issue(StepData step, ExecutionContext ctx)
    {
        if (_dest is { } d)
            ctx.Navmesh.MoveCloseTo(d, Flight.Allowed(ctx), step.StopDistance);
    }
}
