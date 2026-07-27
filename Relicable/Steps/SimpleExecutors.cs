using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Model;
using Relicable.Steps.Interaction;

namespace Relicable.Steps;

// Smaller executors grouped for brevity. Each still implements ITaskExecutor and
// is registered individually in the controller's executor map.

// Direct navigation to an explicit coordinate (used by static stages where no
// flag is dropped, for example walking from an aetheryte to Jalzahn).
//
// Completion requires ARRIVAL (within StopDistance + slack), not merely "vnav is
// idle": a move issued while the mesh was still building (the common case right
// after the auto-prepended teleport) is swallowed by vnavmesh, and completing on
// idle then skipped the walk entirely. MoveCloseTo is edge-triggered and
// self-throttled, so re-issuing it every tick is cheap and recovers the swallowed
// request; if vnav stays idle short of the target past a grace, the step fails so
// the controller re-plans instead of silently pretending it arrived.
public sealed class MoveToExecutor : ITaskExecutor
{
    private const float ArrivalSlack = 5.0f;   // tolerance beyond StopDistance
    private const long IdleShortGraceMs = 15_000; // idle-but-not-arrived budget

    private long _idleSinceTicks;

    public StepType Handles => StepType.MoveTo;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _idleSinceTicks = 0;
        if (step.Position is { } p)
            ctx.Navmesh.MoveCloseTo(p, Flight.Allowed(ctx), step.StopDistance);
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (step.Position is not { } p)
            return ExecutorStatus.Complete; // nothing to walk to
        if (!ctx.Navmesh.IsReady())
        {
            _idleSinceTicks = 0;
            return ExecutorStatus.InProgress;
        }

        var me = Plugin.ObjectTable.LocalPlayer?.Position;
        if (me is { } m && Vector3.Distance(m, p) <= step.StopDistance + ArrivalSlack)
            return ExecutorStatus.Complete; // arrived (regardless of vnav state)

        Combat.Mount.EnsureMounted(ctx, me is { } mm ? Vector3.Distance(mm, p) : 0f);

        // Keep the request alive; the edge trigger makes this a no-op while running
        // and re-issues (at most once a second) a request that was swallowed.
        ctx.Navmesh.MoveCloseTo(p, Flight.Allowed(ctx), step.StopDistance);

        if (ctx.Navmesh.PathfindInProgress() || ctx.Navmesh.IsRunning())
        {
            _idleSinceTicks = 0;
            return ExecutorStatus.InProgress;
        }

        // Idle and not arrived: allow the re-issue a grace window to take, then fail
        // honestly (unreachable point / vnav gave up short) so the controller re-plans.
        if (_idleSinceTicks == 0)
            _idleSinceTicks = Environment.TickCount64;
        if (Environment.TickCount64 - _idleSinceTicks > IdleShortGraceMs)
        {
            Diagnostics.DebugLog.Warn(
                $"MoveTo: vnav is idle {(me is { } d ? $"{Vector3.Distance(d, p):0.0}y" : "an unknown distance")} " +
                $"short of {p:0.0} (stop {step.StopDistance:0.0}); failing so the run re-plans.");
            return ExecutorStatus.Failed;
        }
        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) => ctx.Navmesh.Stop();
}

// City aethernet hop via Lifestream. Lifestream raises its busy flag a moment
// after the request, so completing on the first not-busy tick could finish the
// step before the hop even starts; require either an observed busy edge or a
// short startup grace first (same pattern as EnterDutyExecutor.StartupGraceMs).
public sealed class AethernetTravelExecutor : ITaskExecutor
{
    private const long StartupGraceMs = 2500;

    private long _startTicks;
    private bool _sawBusy;

    public StepType Handles => StepType.AethernetTravel;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _startTicks = Environment.TickCount64;
        _sawBusy = false;
        ctx.Lifestream.AethernetTeleport(step.AethernetShardId);
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (ctx.Lifestream.IsBusy())
        {
            _sawBusy = true;
            return ExecutorStatus.InProgress;
        }
        return (_sawBusy || Environment.TickCount64 - _startTicks > StartupGraceMs)
            ? ExecutorStatus.Complete
            : ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) { }
}

// InteractNpc, StartLeve, TurnInLeve, and UpgradeRelic live in
// InteractionExecutors.cs (they share the NpcInteractor phase machine).

// Trade items to an NPC (Sphere Scroll, materia, light upgrades). Approaches and
// interacts with NpcDataId; TextAdvance carries the exchange/confirm prompts.
// Completes when the required quantity of the item has left the inventory.
public sealed class TurnInItemsExecutor : ITaskExecutor
{
    public StepType Handles => StepType.TurnInItems;

    private readonly NpcInteractor _npc = new();
    private int _baseline;

    public void Start(StepData step, ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
        _npc.Reset();
        _baseline = GameState.InventoryCount(step.ItemId);
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        var need = step.Quantity > 0 ? step.Quantity : 1;
        if (_baseline - GameState.InventoryCount(step.ItemId) >= need)
            return ExecutorStatus.Complete;

        var phase = _npc.Tick(step.NpcDataId, step.Position, ctx);
        return phase == InteractionPhase.Failed ? ExecutorStatus.Failed : ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}

// Use a consumable (for example a relic upgrade material) on the player. Uses
// ActionManager; completes when one of the item has been consumed, or after a
// short grace period if the item has no consumption side effect.
public sealed unsafe class UseItemExecutor : ITaskExecutor
{
    public StepType Handles => StepType.UseItem;

    private const long GraceMs = 5000;
    private int _baseline;
    private long _start;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _baseline = GameState.InventoryCount(step.ItemId);
        _start = Environment.TickCount64;
        var am = ActionManager.Instance();
        if (am != null)
            am->UseAction(ActionType.Item, step.ItemId, extraParam: 0xFFFF);
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (GameState.InventoryCount(step.ItemId) < _baseline)
            return ExecutorStatus.Complete;
        return Environment.TickCount64 - _start > GraceMs
            ? ExecutorStatus.Complete
            : ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) { }
}

// Wait for a named game condition (Nexus light gauge full, a timed window, etc.).
public sealed class WaitForConditionExecutor : ITaskExecutor
{
    public StepType Handles => StepType.WaitForCondition;

    public void Start(StepData step, ExecutionContext ctx)
        => ctx.StepStartTicks = Environment.TickCount64;

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (Environment.TickCount64 - ctx.StepStartTicks > step.TimeoutSeconds * 1000L)
            return ExecutorStatus.Failed; // timed out; controller can re-plan

        var satisfied = step.ConditionKey switch
        {
            "LightGaugeFull" => GameState.IsLightGaugeFull(),
            _ => false,
        };
        return satisfied ? ExecutorStatus.Complete : ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) { }
}
