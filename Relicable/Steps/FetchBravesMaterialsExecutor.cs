using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;

namespace Relicable.Steps;

// Braves stage: pull the material-quest items you already own OUT of your retainers, without being
// asked to go do it yourself.
//
// The Braves quests want a Bombard Core, Sacred Spring Water, a 100k-gil vendor item and two HQ
// crafted items per quest (the "Perfect ..." pieces). Those are bought, crafted or desynthed rather
// than farmed, so they habitually sit on a retainer -- and the run would stop at the report step with
// "gather the vendor/crafted items", which is wrong when the items exist and are simply parked.
//
// Flow: build the wanted set from the Braves planner (outstanding AND seen on a retainer by the bell
// scan) -> if no summoning bell is in reach, teleport to Revenant's Toll (where three of the four
// quest NPCs stand anyway, so this is on the way) -> walk onto the bell and interact -> hand the set
// to the shared RetainerFetchRunner, which cycles every retainer and retrieves the stacks -> close out.
//
// Wants Configuration.AutoWithdrawFromRetainers. With it off the runner only REPORTS what to drag,
// which is not something an unattended run can act on, so the controller does not select this at all.
//
// Bounded and safe-fail throughout: it can only ever move items retainer -> bags, it never buys or
// discards, and every phase has a deadline so a bell it cannot reach fails with guidance instead of
// hanging.
public sealed class FetchBravesMaterialsExecutor : ITaskExecutor
{
    public StepType Handles => StepType.FetchBravesMaterials;

    // The bell hub to fall back on: Revenant's Toll, Mor Dhona. Papana, Guiding Star and Brangwine
    // are all here, so a fetch trip doubles as the trip to the next report.
    private const uint BellTerritory = 156;
    private const string BellName = "Summoning Bell";

    private const float SearchRadius = 100f;   // bells sit within this of any aetheryte plaza
    private const float ArriveHorizontal = 2.0f;
    private const float LandHorizontal = 8.0f;
    private const float FlyMinDistance = 30.0f;
    private const long InteractCooldownMs = 600;
    private const long ApproachTimeoutMs = 180_000;
    private const long OpenBellTimeoutMs = 30_000;

    private enum Phase { Done, Nothing, WaitExit, Teleport, Approach, Opening, Fetching }

    private readonly AetheryteTeleportExecutor _teleport = new();
    private RetainerFetchRunner? _fetch;

    private Phase _phase;
    private StepData? _teleStep;
    private readonly Dictionary<uint, int> _want = new();
    private long _phaseStart;
    private long _lastInteract;
    private bool _landing;
    private bool _teleported;

    // What this trip is for: every Braves material that is still short AND that the last bell scan saw
    // on a retainer. Values are the TOTAL to hold (what RetainerFetchRunner expects), so what is
    // already in the bags is subtracted live and the run ends the moment it is stocked.
    public static Dictionary<uint, int> Wanted(ExecutionContext ctx)
    {
        var want = new Dictionary<uint, int>();
        var planner = ctx.BravesPlanner;
        if (planner == null || !ctx.Config.AutoWithdrawFromRetainers)
            return want;
        foreach (var line in planner.ComputePlan().Lines)
            if (line.Fetchable)
                want[line.ItemId] = want.GetValueOrDefault(line.ItemId) + line.Material.Quantity;
        return want;
    }

    public void Start(StepData step, ExecutionContext ctx)
    {
        _teleStep = null;
        _lastInteract = 0;
        _landing = false;
        _teleported = false;
        _phaseStart = Environment.TickCount64;
        _want.Clear();
        foreach (var kv in Wanted(ctx))
            _want[kv.Key] = kv.Value;

        if (_want.Count == 0)
        {
            // Nothing outstanding is on a retainer. Complete rather than fail: there is genuinely
            // nothing to do, and the controller moves on to the dungeon or report work.
            _phase = Phase.Nothing;
            return;
        }

        _fetch = new RetainerFetchRunner(ctx.Config, "Braves auto-fetch");
        DebugLog.Info($"Braves auto-fetch: {_want.Count} material(s) to pull from retainers.");
        _phase = BoundByDuty() ? Phase.WaitExit : NextAfterDuty(ctx);
    }

    private Phase NextAfterDuty(ExecutionContext ctx)
    {
        // Already standing at a bell (a report at Revenant's Toll just finished, or the player parked
        // at an inn): skip the trip entirely.
        if (WorldObject.FindNearest(BellName, 0, SearchRadius, out _) != null)
            return Begin(Phase.Approach);

        var aeth = Locations.AetheryteForTerritory(BellTerritory);
        if (aeth == 0)
            return Begin(Phase.Approach); // no aetheryte resolved; try to find a bell where we stand
        _teleStep = new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aeth };
        _teleport.Start(_teleStep, ctx);
        _teleported = true;
        return Begin(Phase.Teleport);
    }

    private Phase Begin(Phase phase)
    {
        _phaseStart = Environment.TickCount64;
        return phase;
    }

    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_phase is Phase.Done or Phase.Nothing)
            return ExecutorStatus.Complete;

        var now = Environment.TickCount64;

        switch (_phase)
        {
            case Phase.WaitExit:
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                _phase = NextAfterDuty(ctx);
                return ExecutorStatus.InProgress;

            case Phase.Teleport:
                var t = _teleport.Update(_teleStep!, ctx);
                if (t == ExecutorStatus.Failed)
                {
                    DebugLog.Warn("Braves auto-fetch: could not teleport to Revenant's Toll. Pull the " +
                                  "quest materials from your retainers yourself (see /relic braves), then /relic start.");
                    return ExecutorStatus.Failed;
                }
                if (t == ExecutorStatus.Complete)
                {
                    _teleport.Stop(ctx);
                    _phase = Begin(Phase.Approach);
                }
                return ExecutorStatus.InProgress;

            case Phase.Approach:
                return Approach(ctx, now);

            case Phase.Opening:
                // The bell was fired at; wait for its retainer list. Once it is up the fetch runner
                // owns the UI from here.
                if (DialogueMenu.IsOpen("RetainerList"))
                {
                    _fetch!.Start(_want, "the Braves quest materials", BravesData.GameName,
                        () => RetainerStocks(ctx));
                    _phase = Begin(Phase.Fetching);
                    return ExecutorStatus.InProgress;
                }
                if (now - _phaseStart > OpenBellTimeoutMs)
                {
                    DebugLog.Warn("Braves auto-fetch: the summoning bell did not open its retainer list. " +
                                  "Pull the quest materials yourself (see /relic braves), then /relic start.");
                    return ExecutorStatus.Failed;
                }
                // Re-fire on the cooldown: the first interact can land while still settling from the walk.
                return Approach(ctx, now);

            case Phase.Fetching:
                _fetch!.Tick();
                if (_fetch.Busy)
                    return ExecutorStatus.InProgress;
                // The runner finished (stocked, every retainer checked, or timed out). Completing
                // either way is right: what it could pull is pulled, and the objective's completion
                // re-reads the live plan, so anything still missing simply is not on a retainer.
                DebugLog.Info($"Braves auto-fetch: {_fetch.Status}");
                _phase = Phase.Done;
                return ExecutorStatus.Complete;

            default:
                return ExecutorStatus.Complete;
        }
    }

    // Walk onto the bell and interact. Mirrors InteractObjectExecutor's discipline, which exists
    // because an object's origin sits above the floor (a 3D range gate never closes) and because
    // InteractWithObject is a hard no-op while mounted or airborne. Not that executor itself: it
    // completes when the object's event ENDS, and a bell's event is exactly what we need to stay in.
    private ExecutorStatus Approach(ExecutionContext ctx, long now)
    {
        if (!ctx.Navmesh.IsReady())
        {
            _phaseStart = now; // navmesh build time is not the step's fault
            return ExecutorStatus.InProgress;
        }
        if (now - _phaseStart > ApproachTimeoutMs)
        {
            DebugLog.Warn("Braves auto-fetch: could not reach a summoning bell. Pull the quest materials " +
                          "from your retainers yourself (see /relic braves), then /relic start.");
            return ExecutorStatus.Failed;
        }

        var bell = WorldObject.FindNearest(BellName, 0, SearchRadius, out _);
        if (bell == null)
        {
            DebugLog.Warn(_teleported
                ? "Braves auto-fetch: no summoning bell found at Revenant's Toll (is the zone still loading?)."
                : "Braves auto-fetch: no summoning bell in reach.");
            return ExecutorStatus.Failed;
        }

        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? bell.Position;
        var horiz = Vector2.Distance(new(me.X, me.Z), new(bell.Position.X, bell.Position.Z));

        // Land on HORIZONTAL distance and stay committed until grounded: while airborne the 3D
        // distance keeps the altitude in it, so we would fly on forever fighting the descent.
        if (!Combat.Mount.IsGrounded() && (_landing || horiz <= LandHorizontal))
        {
            _landing = true;
            Combat.Mount.LandAndDismount(ctx, bell.Position);
            return ExecutorStatus.InProgress;
        }
        _landing = false;

        if (horiz > ArriveHorizontal)
        {
            if (horiz > FlyMinDistance)
                Combat.Mount.EnsureMounted(ctx, horiz);
            // Never pick the fly flag from the distance -- a per-tick threshold flip makes vnav hover.
            ctx.Navmesh.MoveCloseTo(bell.Position, Plugin.Condition[ConditionFlag.InFlight], 1.0f);
            return ExecutorStatus.InProgress;
        }

        ctx.Navmesh.Stop();
        if (!Combat.Mount.IsGrounded())
        {
            Combat.Mount.EnsureDismounted(); // a residual mount makes the interact a silent no-op
            return ExecutorStatus.InProgress;
        }
        if (now - _lastInteract >= InteractCooldownMs)
        {
            _lastInteract = now;
            WorldObject.Interact(bell);
            if (_phase != Phase.Opening)
                _phase = Begin(Phase.Opening);
        }
        return ExecutorStatus.InProgress;
    }

    private static IEnumerable<RetainerFetchRunner.RetainerStock> RetainerStocks(ExecutionContext ctx)
    {
        foreach (var r in ctx.Config.RetainerBravesItems.Retainers.Values)
            yield return new RetainerFetchRunner.RetainerStock(r.RetainerName, r.Items);
    }

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        // Always release the runner: it holds an AutoRetainer suppression for as long as it is busy,
        // and only its Stop restores it.
        _fetch?.Stop("Braves auto-fetch stopped.");
    }
}
