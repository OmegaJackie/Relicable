using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;

namespace Relicable.Steps;

// Find a world OBJECT by name (and/or DataId) near an authored position, walk onto it, and
// interact. Written for the "A Relic Reborn" part-1 coffer: the quest's only action at the
// beastman stronghold is to OPEN the Treasure Coffer, and until that happens the game's
// quest sequence never advances -- so the previous bare WalkTo in the Bard path (1125 seq 1)
// flew to Natalan, arrived, reported Complete, and the run then idled there forever.
//
// Why this is not InteractNpcExecutor / NpcInteractor:
//   * NpcInteractor.Find matches BaseId only (NpcInteractor.cs:254-272) -- no name path.
//     The authored target here is a NAME; the DataId is offline-derived and UNVERIFIED.
//   * NpcInteractor's arrival gate is a 3D InteractRange of 4y (NpcInteractor.cs:30, :157).
//     A coffer's object origin sits ABOVE the floor, so the 3D distance need never fall to
//     interact reach even while standing on it -- the exact "a hair too far to open" failure
//     this codebase already paid for once (TreasureMapExecutor.cs:304-308). We gate on
//     HORIZONTAL distance instead.
//   * NpcInteractor completes when the event flag clears (:211-212). A coffer may open only
//     a SelectYesno, which TextAdvance does not carry (TreasureMapExecutor.cs:291-302).
//
// What IS reused, by copying the proven discipline (not the class):
//   * mount/land: sticky land-and-dismount decided on HORIZONTAL distance, and a grounded
//     gate before firing (NpcInteractor.cs:34, :37, :138-144, :193-200). Load-bearing:
//     InteractWithObject is a hard no-op while mounted or airborne, and the step this
//     replaces arrives MOUNTED AND AIRBORNE (MoveToExecutor never lands).
//   * walk-onto: horizontal gate + best-distance stall bail-out + no-fly final approach
//     (TreasureMapExecutor.cs:303-322), so we never loop just short of the object.
//   * self-defense: a fight phase gated on actually being InCombat, NEVER on nearby mobs
//     (TreasureMapExecutor.cs:326-370). Load-bearing here: the strongholds are beastman
//     nests, a proximity gate would stall the open forever, and no other quest-path step
//     ever enables a rotation -- without this the character just stands there and dies.
public sealed class InteractObjectExecutor : ITaskExecutor
{
    public StepType Handles => StepType.InteractObject;

    private enum Phase { Locating, Approaching, Interacting, InEvent, Fight, Done, Failed }

    private const float SearchRadius = 100f;      // the authored anchor lands us well inside this
    private const float ArriveHorizontal = 1.5f;  // cf. TreasureMapExecutor.CofferArrive = 1.0f (:38)
    private const float ApproachStop = 0.5f;      // walk right ONTO it (TreasureMapExecutor.cs:39)
    // vnavmesh idle for this long, with no re-path pending, means it has stopped: either we are
    // there or it got as close as it can. This is the ONLY stall signal -- see the approach block.
    private const long NavIdleMs = 1500;
    // How far out the "nav gave up, interact anyway" bail-out may still fire. The bail exists
    // because an object's own footprint can block the exact stop point, leaving us a yard or two
    // short forever -- but a coffer's interact reach is SHORT (the treasure-map loop measured a
    // failed open from ~2.5y), so firing from beyond this is pointless and only hides a real
    // "cannot route there" behind a silent timeout.
    private const float BailInteractRange = 3.0f;
    private const float LandHorizontal = 8.0f;    // NpcInteractor.cs:34
    private const float FlyMinDistance = 30.0f;   // NpcInteractor.cs:37
    private const long InteractCooldownMs = 600;  // NpcInteractor.cs:39
    private const long DialogCooldownMs = 400;    // TreasureMapExecutor's menu throttle
    private const long OverallTimeoutMs = 120_000;
    // Combat pauses the timeout, but only for so long: an unwinnable fight must eventually
    // fail the step rather than hang InProgress forever (which no failure counter can catch).
    private const long MaxCombatGraceMs = 180_000;
    private const long SpentGraceMs = 2000;       // object gone/spent after our interact: settle, then finish
    private const long DiagMs = 5000;

    private Phase _phase;
    private long _startTicks;
    private long _lastTick;
    private long _combatGraceUsed;
    private long _lastInteract;
    private long _lastDialog;
    private long _lastDiag;
    private long _spentSince;
    private bool _landing;         // sticky landing commitment (NpcInteractor.cs:45)
    private bool _fired;           // we have issued at least one InteractWithObject
    private bool _everTargetable;  // the object read targetable at some point (see the spent check)
    private bool _warnedUntargetable; // the "in range but nothing targetable" warning has been said once
    private long _navIdleSince;    // when vnavmesh last went idle (0 = it is moving/pathing)
    private Vector3? _resolvedAnchor; // a map-derived (Y=0) anchor snapped to a landable floor, cached

    public void Start(StepData step, ExecutionContext ctx)
    {
        // Reused singleton (one step runs at a time): reset EVERYTHING.
        _phase = Phase.Locating;
        _startTicks = Environment.TickCount64;
        _lastTick = Environment.TickCount64;
        _combatGraceUsed = 0;
        _lastInteract = 0;
        _lastDialog = 0;
        _lastDiag = 0;
        _spentSince = 0;
        _landing = false;
        _fired = false;
        _everTargetable = false;
        _warnedUntargetable = false;
        _navIdleSince = 0;
        _resolvedAnchor = null;
        // Force the next rotation-mode dispatch to actually re-send. The combat backend is
        // edge-triggered on its last-sent state, so a backend left "off" by the previous step
        // would otherwise edge-SUPPRESS this step's self-defense EnableAuto and never engage the
        // Ixal that jump us at the coffer. KillTargetExecutor.Start does the same; this executor
        // (and TreasureMapExecutor) were missing it.
        ctx.Rotation.ResyncNextDispatch();
        ctx.BossModReborn.Resync();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable(); // carries the quest event; the Yes/No is confirmed by hand below
        DebugLog.Info($"InteractObject: start. name '{step.TargetName}', dataId {step.TargetDataId}, anchor {step.Position}");
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_phase == Phase.Done)
            return ExecutorStatus.Complete;
        if (_phase == Phase.Failed)
            return ExecutorStatus.Failed;

        var now = Environment.TickCount64;
        var sinceLastTick = now - _lastTick;
        _lastTick = now;

        if (string.IsNullOrEmpty(step.TargetName) && step.TargetDataId == 0)
        {
            // Safe-fail, never throw: this is an authoring error, not a runtime condition.
            DebugLog.Warn("InteractObject: step has neither TargetName nor TargetDataId; nothing to find");
            return Fail(ctx);
        }

        // Navmesh build time must not count against the budget (NpcInteractor.cs:71-76): a
        // freshly teleported-to zone can take a while to build on first visit.
        if (_phase is Phase.Locating or Phase.Approaching && !ctx.Navmesh.IsReady())
        {
            _startTicks = now;
            return ExecutorStatus.InProgress;
        }

        // Being jumped on the way in is not a reason to fail, so combat pauses the timeout --
        // but only up to MaxCombatGraceMs, so a fight we cannot win still expires instead of
        // hanging InProgress forever (a hang is invisible; MaxConsecutiveFailures only counts
        // ExecutorStatus.Failed).
        if (Plugin.Condition[ConditionFlag.InCombat] && _combatGraceUsed < MaxCombatGraceMs)
        {
            _combatGraceUsed += sinceLastTick;
            _startTicks += sinceLastTick;
        }

        if (now - _startTicks > OverallTimeoutMs)
        {
            DebugLog.Warn($"InteractObject: timed out in phase {_phase} looking for '{step.TargetName}'/{step.TargetDataId} " +
                          $"near {step.Position} (fired: {_fired}, ever targetable: {_everTargetable}). " +
                          "Check the object name and DataId against the live zone.");
            return Fail(ctx);
        }

        var obj = WorldObject.FindNearest(step.TargetName, step.TargetDataId, SearchRadius, out var targetable);
        _everTargetable |= targetable;

        // 1) The object's event, but ONLY once we have actually fired at it. Gating on _fired
        //    is what makes the event our completion EVIDENCE rather than a coincidence: any
        //    unrelated Talk/cutscene/Yes-No during the long flight in would otherwise latch
        //    this phase and complete the step without ever touching the coffer -- re-creating
        //    the very stall this executor exists to fix. NpcInteractor has the same guard by
        //    construction: its identical check lives inside case Interacting (:178-187).
        //    Checked BEFORE the combat gate so an aggro landing mid-cutscene cannot bail us
        //    out of the event and break it.
        if (_fired && (EventConditions.InEvent || DialogueMenu.AnyOpen()))
        {
            _phase = Phase.InEvent;
            ctx.Navmesh.Stop();
            // TextAdvance does not carry the coffer's Yes/No (TreasureMapExecutor.cs:291-302).
            // Only ever fired post-_fired, so a stray prompt elsewhere never gets a blind Yes.
            if (DialogueMenu.IsOpen("SelectYesno") && now - _lastDialog >= DialogCooldownMs)
            {
                _lastDialog = now;
                DialogueMenu.ConfirmYes();
                DebugLog.Info("InteractObject: confirming the object's prompt");
            }
            return ExecutorStatus.InProgress;
        }
        if (_phase == Phase.InEvent)
        {
            // The event we opened has closed. "Done" here means the interaction happened and
            // its event finished -- NOT that the quest advanced; the controller's sequence
            // watch remains the authority on that.
            DebugLog.Info("InteractObject: the object's event completed");
            return Done(ctx);
        }

        // 2) Self-defense, BEFORE the not-loaded branch so it also covers the approach, when
        //    the object is still outside SearchRadius. Gated on actually being InCombat, never
        //    on nearby mobs: a proximity gate would stall the open forever in a beastman nest
        //    (the load-bearing note at TreasureMapExecutor.cs:326-327).
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            if (_phase != Phase.Fight)
            {
                DebugLog.Info("InteractObject: in combat; clearing before continuing");
                _phase = Phase.Fight;
                ctx.Navmesh.Stop();
            }
            // EnableAuto, not Manual: these are aggressors already engaged on us. Manual is
            // only needed to PULL a neutral mob.
            if (ctx.Targeting.EngageNearestHostile(fateBound: false))
            {
                Combat.Mount.EnsureDismounted(); // cannot fight mounted
                ctx.Rotation.EnableAuto();
                Combat.CombatAssist.Engage(ctx);
            }
            else
            {
                ctx.Rotation.Disable();
            }
            return ExecutorStatus.InProgress;
        }
        if (_phase == Phase.Fight)
        {
            ctx.Rotation.Disable();
            DebugLog.Info("InteractObject: combat cleared; resuming the approach");
            _phase = Phase.Approaching;
            _navIdleSince = 0; // the fight halted vnav; do not count that as its best effort
        }

        // 3) The object is gone, or is spent (it was targetable and no longer is), AFTER we
        //    fired at it. Both mean the interaction landed and the object consumed itself --
        //    some quest objects despawn, some just go inert, and neither necessarily leaves an
        //    event flag behind for us to observe. Without this, a successful open would keep
        //    re-firing every 600ms until the timeout and then report Failed, which would count
        //    toward the controller's consecutive-failure Stop().
        //    _everTargetable guards the inert case: if the object NEVER read targetable, its
        //    targetability is not a signal we can trust, so we do not infer "spent" from it.
        var spent = _fired && (obj == null || (_everTargetable && !targetable));
        if (spent)
        {
            if (_spentSince == 0)
                _spentSince = now;
            if (now - _spentSince > SpentGraceMs)
            {
                DebugLog.Info(obj == null
                    ? "InteractObject: the object is gone after our interact; treating the step as done"
                    : "InteractObject: the object went untargetable after our interact; treating the step as done");
                return Done(ctx);
            }
            return ExecutorStatus.InProgress;
        }
        _spentSince = 0;

        // 4) Not loaded yet: travel to the authored anchor so it streams in.
        if (obj == null)
        {
            _phase = Phase.Locating;
            TravelToAnchor(step, ctx);
            return ExecutorStatus.InProgress;
        }

        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position;
        var horiz = Vector2.Distance(new(me.X, me.Z), new(obj.Position.X, obj.Position.Z));

        // 5) Land + dismount, decided on HORIZONTAL distance and sticky until grounded
        //    (NpcInteractor.cs:134-144). While airborne the 3D distance stays large (altitude)
        //    and we would fly on toward the object forever, fighting the descent.
        if (!Combat.Mount.IsGrounded() && (_landing || horiz <= LandHorizontal))
        {
            _landing = true;
            _phase = Phase.Approaching;
            Combat.Mount.LandAndDismount(ctx, obj.Position);
            return ExecutorStatus.InProgress;
        }
        _landing = false;

        // 6) Walk fully ONTO it. HORIZONTAL gate, because the object's origin sits above the
        //    floor and a 3D gate can never close (TreasureMapExecutor.cs:304-308).
        //
        //    Arrival is judged by vnavmesh's OWN movement state, NEVER by the straight-line
        //    distance shrinking. An object behind geometry -- an Ixal hut, a fenced compound --
        //    makes vnav path AROUND it, so the horizontal distance legitimately GROWS, at full
        //    running speed, while we are making perfect progress toward the door. A
        //    "distance stopped shrinking" heuristic (which is what TreasureMapExecutor can
        //    afford, since its coffer is dug in place on open ground) reads that detour as a
        //    stall: in Natalan it declared "stopped closing" while the character was running
        //    4.5y/s and fired the interact from 19.9y, where it cannot possibly land. So while
        //    vnav is running or re-pathing we are NOT stuck, full stop.
        var navBusy = ctx.Navmesh.IsRunning() || ctx.Navmesh.PathfindInProgress();
        if (navBusy)
            _navIdleSince = 0;
        else if (_navIdleSince == 0)
            _navIdleSince = now;

        // vnav has gone quiet: this is its best effort. Interact anyway -- but only from a
        // plausible reach (BailInteractRange). Going idle far out means "cannot route there",
        // not "a hair too far"; firing from there would just burn the timeout while looking
        // like it tried. Beyond the bail range we keep re-issuing the move instead (MoveCloseTo
        // re-paths once movement has stopped short), and the timeout fails honestly.
        var navGaveUp = _navIdleSince != 0 && now - _navIdleSince > NavIdleMs;
        var bailing = navGaveUp && horiz > ArriveHorizontal && horiz <= BailInteractRange;

        if (horiz > ArriveHorizontal && !bailing)
        {
            _phase = Phase.Approaching;
            if (horiz > FlyMinDistance)
                Combat.Mount.EnsureMounted(ctx, horiz);
            // Do NOT pick the fly flag from the distance: a threshold pick flips it every tick
            // and makes vnav hover in place (NpcInteractor.cs:164-166). Keep flying only if we
            // already are; the land block above brings us down.
            ctx.Navmesh.MoveCloseTo(obj.Position, Plugin.Condition[ConditionFlag.InFlight], ApproachStop);
            if (now - _lastDiag > DiagMs)
            {
                _lastDiag = now;
                // dY is logged explicitly: if this step ever fails silently, a large |dY| is the
                // smoking gun that we landed a tier below the object (the strongholds are
                // multi-tier, and Mount.LandAndDismount deliberately descends to the floor BELOW
                // the target -- the same geometry that broke KillTarget). navBusy distinguishes
                // "walking a detour" (fine) from "vnav cannot route there" (the real failure).
                DebugLog.Info($"InteractObject: approaching '{obj.Name.TextValue}' ({obj.BaseId}), " +
                              $"{horiz:0.0}y horizontal, dY {obj.Position.Y - me.Y:0.0}, " +
                              $"targetable {targetable}, navBusy {navBusy}");
            }
            return ExecutorStatus.InProgress;
        }

        // 7) Interact.
        ctx.Navmesh.Stop();
        _phase = Phase.Interacting;
        if (!Combat.Mount.IsGrounded())
        {
            Combat.Mount.EnsureDismounted(); // a residual mount would make the fire below a silent no-op
            return ExecutorStatus.InProgress;
        }
        if (now - _lastInteract >= InteractCooldownMs)
        {
            _lastInteract = now;
            _fired = true;
            if (bailing)
                DebugLog.Info($"InteractObject: vnav stopped {horiz:0.0}y short (its best effort); interacting anyway");
            DebugLog.Info($"InteractObject: interacting with '{obj.Name.TextValue}' ({obj.BaseId}), targetable {targetable}");
            // Standing ON it, grounded, and it still reads untargetable: InteractWithObject is a
            // no-op against such an object, so this fire cannot land. Say why once, because the
            // usual cause is specific and otherwise invisible -- most beastman strongholds hold
            // TWO jobs' identically-named "Treasure Coffer"s, and only the one belonging to the
            // quest step you are on is targetable. The finder now prefers a targetable match
            // (WorldObject.FindNearest), so reaching here means none is loaded: either this job's
            // coffer has not streamed in yet, or the quest is not actually at that step.
            if (!targetable && !_everTargetable && !_warnedUntargetable)
            {
                _warnedUntargetable = true;
                DebugLog.Warn($"InteractObject: '{obj.Name.TextValue}' ({obj.BaseId}) is not targetable from " +
                              $"{horiz:0.0}y, and no targetable match is loaded nearby. If this stronghold holds " +
                              "another job's coffer too, that is probably the one we found -- check that the relic " +
                              "quest is really at the recover-the-broken-weapon step.");
            }
            WorldObject.Interact(obj);
        }
        return ExecutorStatus.InProgress;
    }

    // Not loaded yet: travel to the authored anchor so the object streams in. Mirrors
    // NpcInteractor.cs:102-121. The anchor only has to get us within SearchRadius -- the
    // finder then returns the object's LIVE position, which is what we actually approach, so
    // an anchor that is somewhat off still works.
    private void TravelToAnchor(StepData step, ExecutionContext ctx)
    {
        if (step.Position is not { } ap)
            return; // nothing to travel to; the timeout is the backstop
        // A map-derived anchor (MapCoords.MapToWorld with no authored height) carries Y = 0, meaning
        // "resolve the floor from XZ". Navigating to the raw Y = 0 point sends the character underground
        // or fails to route (NavmeshIpc: a raw Y = 0 snap "rejects every floor above sea level"), so snap
        // it to a landable floor first -- the same resolution KillTargetExecutor applies to these very
        // stronghold coords. A real-Y anchor (the hand-authored Bard coffer, Y=287) has Y != 0 and is used
        // as-is. Cached: the snap is stable for a fixed anchor, so the probe runs once (and retries while
        // the freshly-teleported zone's mesh is still building and the resolve returns null).
        if (ap.Y == 0f)
        {
            _resolvedAnchor ??= ctx.Navmesh.LandableFloorForMapPoint(ap) ?? ctx.Navmesh.FloorForMapPoint(ap);
            if (_resolvedAnchor is { } snapped)
                ap = snapped;
        }
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? ap;
        var d = Vector3.Distance(me, ap);
        Combat.Mount.EnsureMounted(ctx, d);
        ctx.Navmesh.MoveCloseTo(ap, Flight.Allowed(ctx), step.StopDistance);
        if (Environment.TickCount64 - _lastDiag > DiagMs)
        {
            _lastDiag = Environment.TickCount64;
            DebugLog.Info($"InteractObject: '{step.TargetName}'/{step.TargetDataId} not loaded; travelling to the anchor ({d:0.0}y)");
        }
    }

    private ExecutorStatus Done(ExecutionContext ctx)
    {
        _phase = Phase.Done;
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        return ExecutorStatus.Complete;
    }

    private ExecutorStatus Fail(ExecutionContext ctx)
    {
        _phase = Phase.Failed;
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        return ExecutorStatus.Failed;
    }

    public void Stop(ExecutionContext ctx)
    {
        // Stop runs on every completion or abort. Halt the mesh so a stale destination cannot
        // survive into the next objective, and drop the rotation the fight phase may have
        // enabled.
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        // Under the RSR backend the fight phase may have handed BossMod Reborn a separate avoidance
        // preset; release it (no-op under the BossMod Reborn backend). Matches KillTargetExecutor.
        Combat.CombatAssist.Disengage(ctx);
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
