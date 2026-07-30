using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types; // IGameObject (the FATE start NPC)
using Relicable.Diagnostics;
using Relicable.Model;
using CSFateContext = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateContext;
using CSTargetSystem = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Relicable.Steps;

// Used by the Atma and Nexus stages. Locates a FATE (a specific one for book
// FATEs, or the nearest active one for Atma's "any FATE in zone"), navigates into
// its ring, engages FATE enemies via the combat backend, and completes when that
// FATE ends.
//
// Verified against current FFXIVClientStructs (FateContext.State/Progress/Location/
// Radius) and read through Dalamud's IFateTable.
//
// Completion is per-FATE: the step finishes when the target FATE leaves Running.
// The stage objective is confirmed separately (Atma by ItemCount, book FATEs by
// RelicNote.IsFateComplete), so this executor running once per FATE and the
// controller re-selecting the objective forms the "keep doing FATEs" loop.
public sealed class ParticipateFateExecutor : ITaskExecutor
{
    public StepType Handles => StepType.ParticipateFate;

    // Distance (yalms) at which we stop closing on a FATE mob and hand off to the combat backend --
    // melee range, so a melee job (and RSR, which does not move the character itself) ends up right
    // next to the mob. DisengageRange is the looser hold-while-fighting band: once engaged, a mob
    // drifting out to here does NOT re-trigger the approach, so a mob wobbling around EngageRange
    // cannot thrash the backend off/on every tick (the same hysteresis the kill grind uses).
    private const float FateEngageRange = 5f;
    private const float FateDisengageRange = 10f;
    // Interact range for a "speak to begin" FATE's start NPC (NPC-initiated boss FATEs).
    private const float FateInteractRange = 4f;

    // ---- Approach stall guard ----
    // The FATE approach had NO progress check of any kind: an unreachable goal -- a mob hovering
    // over water or stood on an off-mesh ledge, a ring whose centre vnav cannot path to -- looped
    // here forever with the rotation disabled and nothing to break the tie. (The kill grind has had
    // this guard since it hit the same wall; see KillTargetExecutor's _lockBestDist / StallTimeoutMs.)
    //
    // Deliberately SLOWER than the kill grind's 12s: FATE mobs roam far more than note mobs, and a
    // boss kiting around its own ring can legitimately keep us from closing for a while. Only a
    // genuinely stuck approach should trip 20s of no progress at all.
    private const float ApproachProgress = 3f;      // this much closer counts as real progress
    private const long ApproachStallMs = 20000;
    private const long ApproachStuckMs = 25000;     // how long a blacklisted mob / FATE is skipped
    // The landing branch returns BEFORE the distance guard above, so it needs its own watchdog: a
    // mob hovering over water/a shaft can never be descended to. See LandWithWatchdog.
    private const long FateLandTimeoutMs = 6000;
    private long _landingSince;

    // What the tracker is currently measuring, so a NEW goal restarts it rather than inheriting the
    // previous one's clock. Keyed on identity (which FATE, which mob) NOT position: the goal is a
    // live mob that moves every tick, so a position key would reset constantly and never trip.
    private ushort _approachFate;
    private ulong _approachMob;
    private float _approachBest;
    private long _approachProgressAt;

    // A FATE mob we could not close on, skipped until _stuckMobUntil so the approach picks another
    // (or falls back to the ring centre). Cheap and self-healing -- the blacklist simply expires.
    private ulong _stuckMobId;
    private long _stuckMobUntil;
    // A whole FATE whose RING we could not reach. Only used by the Atma "any active FATE" mode,
    // where there is no Rotate to fall back on (see the travel branch), so the next-nearest active
    // FATE is picked instead.
    private ushort _stuckFateId;
    private long _stuckFateUntil;

    private bool _wasInside;
    // Any ring stood in this step (target OR prerequisite). Separate from _wasInside, which is
    // target-only because it drives completion; see the stall escapes in Update.
    private bool _participated;
    private bool _engaging;
    private long _syncThrottle;
    private long _waitLog;
    private long _startTick;
    private ulong _markedId;
    private long _startInteractThrottle;
    private long _stateLog; // throttles the engage-decision heartbeat
    private ulong _engageLoggedId; // dedup for the per-target engage log
    // CombatAssist.DefendSelf's per-caller latch: the id we last armed the backend for, so the
    // mode is re-sent only when the aggressor changes and never per tick.
    private ulong _defendArmedId;
    private System.Numerics.Vector3? _flaggedFor;
    // Prerequisite-chain FATEs (StepData.PrerequisiteFateId): a few book FATEs (e.g. Breaching North
    // Tidegate) do not spawn until a PREDECESSOR overworld FATE (Gauging North Tidegate) is cleared.
    // _workingPrereq = we are currently driving the PREREQ, not the target, so completion must NOT
    // fire (only the target credits the book slot). _prereqDone = the prereq has been cleared this
    // step, so we now stage at / wait for the TARGET instead of the prereq.
    private bool _workingPrereq;
    private bool _prereqDone;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _wasInside = false;
        _participated = false;
        _engaging = false;
        _syncThrottle = 0;
        _waitLog = 0;
        _markedId = 0;
        _startInteractThrottle = 0;
        _stateLog = 0;
        _engageLoggedId = 0;
        _defendArmedId = 0; // executors are singletons; a stale latch would suppress the re-arm
        // Approach stall guard: all per-step, and all MUST reset -- a blacklist carried over from a
        // previous FATE would skip a mob (or a whole FATE) this one still needs.
        ResetApproachTracker();
        _stuckMobId = 0;
        _stuckMobUntil = 0;
        _stuckFateId = 0;
        _stuckFateUntil = 0;
        _landingSince = 0;
        _flaggedFor = null;
        _workingPrereq = false;
        _prereqDone = false;
        // NPC-initiated ("speak to begin") boss FATEs show a Talk / Yes-No on the start interaction;
        // let TextAdvance carry it. Enabled as a global "keep it on" like the leve accept flow; it only
        // affects dialogue, so it is inert during the FATE fight.
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
        // Rotation clock: this step begins AFTER any teleport to the FATE's zone, so
        // the elapsed time here is the in-zone wait for the FATE to spawn.
        _startTick = System.Environment.TickCount64;
        ctx.Rotation.ResyncNextDispatch();
        ctx.BossModReborn.Resync();
        // Let RSR drive the rotation and pick FATE enemies (it auto-detects the
        // active FATE); set its hostile-target type for solo FATE work.
        ctx.Rotation.ConfigureForFate();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Resolve which FATE to physically work THIS tick. Normally the target. But a prerequisite
        // chain FATE (StepData.PrerequisiteFateId, set for a few book FATEs) does not appear in the
        // FATE table at all until its PREDECESSOR overworld FATE is cleared -- so when the target is
        // absent and its prereq is up, we go do the prereq using the SAME engage/DriveFateStart path.
        // Only the TARGET credits the book slot, so _workingPrereq gates completion below: finishing
        // the prereq never completes the step. Every other FATE has PrerequisiteFateId == 0 and skips
        // this entirely, so the normal FATE flow is unchanged.
        // Atma mode only: skip a FATE whose ring we just failed to reach (see the approach stall
        // guard), so the next-nearest active FATE is taken instead of re-picking the unreachable one
        // every tick. A specific book FATE is never skipped -- it is the objective, and its escape
        // hatch is Rotate.
        var avoidFate = System.Environment.TickCount64 < _stuckFateUntil ? _stuckFateId : (ushort)0;
        var target = step.FateId != 0 ? Fates.ById((ushort)step.FateId) : Fates.NearestActive(avoidFate);
        var fate = target;
        _workingPrereq = false;
        if (target == null && step.PrerequisiteFateId != 0 && !_prereqDone)
        {
            var prereq = Fates.ById((ushort)step.PrerequisiteFateId);
            if (prereq != null)
            {
                fate = prereq;
                _workingPrereq = true;
                // Keep the target's spawn-wait window from counting down while we are busy on the
                // prereq, so the target gets a fresh window once the prereq is done (below).
                _startTick = System.Environment.TickCount64;
            }
            else if (Data.BraveBookPositions.SpawnerNpcForFate(step.PrerequisiteFateId) is { } spawnerNpc)
            {
                // The prereq FATE is NPC-SPAWNED (e.g. 610 "The Enemy of My Enemy", spawned by talking to
                // the standing BNpc "Mianne Thousandmalm"): it is NOT in the FATE table until she is
                // engaged, so Fates.ById returned null AND DriveFateStart can never fire (no FATE object
                // to read a MotivationNpc from). The stage-and-wait below would then idle at the spot
                // forever. Go talk to the NPC to spawn it; once it registers, Fates.ById picks it up next
                // tick and the normal prereq flow above takes over.
                var meNow = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                return DriveFateSpawn(spawnerNpc, step.PrerequisiteFateId, ctx, meNow);
            }
        }

        if (fate == null)
        {
            // The target FATE is gone. If we had been inside the TARGET (never the prereq -- _wasInside
            // is only set when working the target), it ended -> done.
            if (_wasInside)
            {
                ctx.Rotation.Disable();
                DebugLog.Verbose("FATE ended; step complete");
                return ExecutorStatus.Complete;
            }

            // FATE not active yet. Travel to its location and wait for it to spawn.
            // FATE coordinates are not in the sheets, so the location comes from the authored
            // per-zone FATE spawn table (Data/BraveBookPositions) via the step's Position;
            // without one we can only idle in place.
            //
            // Self-defense FIRST: this stage-and-wait is the longest idle in the whole run (Atma's
            // "any active FATE" mode waits indefinitely), and it used to sit here with the rotation
            // disabled no matter what was hitting us. Nothing else here could see the attacker --
            // NearestHostileInFate matches only mobs carrying the FATE's id, so an ordinary
            // overworld hostile that wandered onto us was invisible at any distance. Freeze the
            // spawn-wait clock while defending so an add fight cannot burn the rotate window and
            // make us skip a FATE that was about to spawn.
            if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
            {
                _startTick = System.Environment.TickCount64;
                return ExecutorStatus.InProgress;
            }
            ctx.Rotation.Disable();

            // The flag / FlagToPoint only resolve once the zone navmesh is built; wait for it before
            // deciding there is no location. Checked BEFORE the rotate window below so we never rotate
            // off a FATE while the zone (and its FATE table) is still loading -- otherwise a FATE that
            // is actually up could read as "not active" and be skipped, especially on the short
            // first-pass window.
            if (!ctx.Navmesh.IsReady())
            {
                WaitLog("zone navmesh still loading; waiting before travelling to the FATE");
                return ExecutorStatus.InProgress;
            }

            // Rotation: a specific book FATE (FateId != 0) that has not spawned within the wait window
            // is abandoned so the controller can try another objective -- the next incomplete book
            // FATE, in order -- rather than idling here forever. The window is set per attempt by the
            // controller (ctx.FateWaitSeconds): a short glance on the first pass through a book's
            // FATEs, then the full Config.FateRotateSeconds on later passes. FateId 0 is the Atma "any
            // active FATE in zone" mode, where there is nothing to rotate to (only THIS zone drops
            // this zone's atma), so it never rotates. A window of 0 or less disables rotation (wait
            // indefinitely).
            var rotateSeconds = ctx.FateWaitSeconds > 0 ? ctx.FateWaitSeconds : ctx.Config.FateRotateSeconds;
            if (step.FateId != 0 && rotateSeconds > 0
                && System.Environment.TickCount64 - _startTick >= rotateSeconds * 1000L)
            {
                ctx.Navmesh.Stop();
                DebugLog.Info($"FATE {step.FateId} not active after {rotateSeconds}s; rotating to the next objective");
                return ExecutorStatus.Rotate;
            }

            // Destination: the authored staging coordinate snapped to the navmesh. A FATE always
            // stages at its OWN authored spot and waits there for it to spawn -- never at whatever
            // FATE happens to be active nearby. Chasing the nearest active FATE is blind to WHICH
            // fate we want: adjacent North Shroud boss FATEs like Rude Awakening (632) and Air
            // Supply (633) sit only ~3 map units apart, so it stranded the character in the wrong
            // ring waiting for a fate that is elsewhere (the reported "getting me stuck in a
            // separate fate").
            //
            // Drop a FRESH flag at the authored spot whenever it changes (like the kill grind), so
            // the flag is a visible marker for THIS FATE and never one left by a prior objective. The
            // flag is NOT used as the destination: a stale or player-moved flag (FlagToPoint) could
            // send us to a random place -- the destination is the authored coordinate, snapped to a
            // LANDABLE floor (high-Y probe; landable-only so we never fly out of bounds to a lake
            // bottom / void). Null means no landable floor near the spot, so we wait rather than head
            // out of bounds.
            // Stage at the PREREQUISITE's spawn while we are still waiting for it (the target FATE
            // cannot appear until the prereq is cleared); once the prereq is done, stage at the target
            // as usual. Every non-gated FATE has PrerequisiteFateId == 0 and so always uses step.Position.
            var stagePos = step.PrerequisiteFateId != 0 && !_prereqDone
                ? Data.BraveBookPositions.FateWorld(step.PrerequisiteFateId)
                : step.Position;

            if (stagePos is { } spawn && _flaggedFor != spawn)
            {
                if (MapFlag.Set(spawn))
                    _flaggedFor = spawn;
            }

            var dest = stagePos is { } fp
                ? ctx.Navmesh.LandableFloorForMapPoint(fp)
                : (Vector3?)null;

            if (dest is { } t)
            {
                var pos = Plugin.ObjectTable.LocalPlayer?.Position ?? t;
                var d = Vector3.Distance(pos, t);
                if (d > 5f)
                {
                    // Mount, then FLY the haul out to the staging spot (Flight.ShouldFly: airborne
                    // already, or mounted with a long enough leg in a flyable zone). Passing the bare
                    // Flight.Allowed here used to hand a 3D path to a still-UN-mounted character on the
                    // first ticks -- and on a 6-30y hop, where EnsureMounted never mounts at all, that
                    // path can never be followed and vnav stalls short of the spot.
                    Combat.Mount.EnsureMounted(ctx, d);
                    ctx.Navmesh.MoveCloseTo(t, Flight.ShouldFly(ctx, d), 3f);
                }
                else
                {
                    // Arrived at the staging spot: LAND + dismount and wait for the FATE to spawn. Use
                    // LandAndDismount, not a bare EnsureDismounted: after flying here the character can be
                    // hovering above the floor, where a plain dismount leaves it stuck airborne (the
                    // "brought somewhere it can't dismount" case). LandAndDismount routes down to a
                    // landable floor point first.
                    ctx.Navmesh.Stop();
                    Combat.Mount.LandAndDismount(ctx, t);
                    WaitLog($"At location; waiting for FATE to spawn ({ctx.CurrentObjective?.DisplayName})");
                }
            }
            else
            {
                WaitLog("FATE not active and no location set; add a coordinate or place a map flag at the FATE");
            }
            return ExecutorStatus.InProgress;
        }

        WaitLog($"FATE active ({fate.FateId}); progress {fate.Progress}");
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var radius = fate.Radius > 1f ? fate.Radius : 20f;

        // Complete the moment the FATE finishes, checked FIRST so a done FATE never sends us chasing a
        // last mob across the ring. The stage objective (Atma item count, or the book FATE slot) is
        // verified separately by the controller, so re-selecting simply waits for the next FATE.
        if (fate.Progress >= 100 || fate.State is FateState.Ended or FateState.Failed)
        {
            ctx.Navmesh.Stop();
            ctx.Rotation.Disable();
            if (_workingPrereq)
            {
                // The PREREQUISITE finished; the target FATE should now spawn. Do NOT complete the step
                // (the prereq does not credit the book slot). Mark the prereq done, reset the engage
                // state, and give the target a FRESH spawn-wait window (else the elapsed prereq fight
                // would trip an immediate Rotate the moment we start waiting for the target).
                _prereqDone = true;
                _engaging = false;
                _markedId = 0;
                _engageLoggedId = 0; // re-arm the once-per-engage log for the TARGET FATE (RSR-owns path uses 1 as its sentinel)
                _flaggedFor = null;
                _startTick = System.Environment.TickCount64;
                DebugLog.Info($"FATE prereq {fate.FateId} finished; now waiting for the target {step.FateId}");
                return ExecutorStatus.InProgress;
            }
            // A specific book FATE (FateId != 0) that was ALREADY finished when we arrived -- we never
            // entered its ring, so _wasInside is false -- was missed (cleared by other players) and earned
            // NO book credit. Returning Complete there is a false "success": Complete does not stamp
            // _fateCheckedTick, so the controller (which orders book FATEs by that stamp) re-picks this SAME
            // just-finished FATE immediately, and it will not respawn for a long while, so the run churns in
            // the zone completing a done FATE over and over without ever crediting the slot (the reported
            // "completes a FATE but the slot does not progress / it does not move on"). Rotate instead: the
            // controller stamps _fateCheckedTick and round-robins to the NEXT incomplete book FATE, coming
            // back to this one later once it may have respawned. A FATE we actually fought in (_wasInside)
            // still Completes normally, and the Atma "any active FATE" mode (FateId 0) is unchanged -- there
            // completing simply re-picks the next nearest active FATE.
            if (step.FateId != 0 && !_wasInside)
            {
                DebugLog.Info($"FATE {fate.FateId} was already finished on arrival (progress {fate.Progress}); " +
                    "never participated so no credit -- rotating to the next incomplete book FATE.");
                return ExecutorStatus.Rotate;
            }
            DebugLog.Verbose($"FATE {fate.FateId} finished (progress {fate.Progress})");
            return ExecutorStatus.Complete;
        }

        // NPC-INITIATED FATE: a "speak to begin" boss FATE (e.g. Schism, The Taste of Fear) stays in
        // Preparing until a player interacts with its start NPC (FateContext.MotivationNpc). Until it is
        // Running there is NO attackable FATE mob, so the sync + engage below would idle forever -- the
        // reported "didn't talk to the person to start the fate", and the combat backend never toggles
        // on / attacks. Drive the start interaction first; the engage path resumes once it is Running.
        if (fate.State != FateState.Running)
            return DriveFateStart(fate, ctx, me);

        // Level-sync the MOMENT we are inside the ring -- before closing on a mob. This is
        // load-bearing, not cosmetic: an over-levelled, UNSYNCED player cannot damage FATE enemies,
        // and the RSR backend's own FATE filter (IgnoreNonFateInFate, on by default) DROPS any mob
        // whose FateId != the player's SYNCED fate id (which is 0 while unsynced), so it hard-targets
        // the boss but never swings -- the reported "did not level sync and not attacking" on a
        // single-boss FATE like "What Gored Before". The old code only synced AFTER reaching the mob's
        // melee range while grounded, so a boss that roamed to the ring edge (melee just outside the
        // old sync spot), or any slow approach, left us unsynced and unable to attack. See TrySyncToFate.
        TrySyncToFate(fate.FateId);

        // Go to an actual FATE ENEMY, not the ring centre. A melee job -- and RSR, which does not move
        // the character on its own -- must be right next to a mob to fight it, so parking at the centre
        // left the character too far to engage a mob on the far side of the ring (the reported "dismounts
        // but it's too far from the enemy to engage"). Mobs also stand on walkable ground, so closing on
        // one avoids a bad dismount at the ring centre (the "can't dismount" case). While no mob is loaded
        // yet (just spawned / between waves) fall back to the FATE centre so we still move INTO the ring;
        // fateBound targeting only finds mobs once we are inside it, which naturally gives a two-stage
        // approach (to the centre, then to a mob) with no oscillation.
        // Route THROUGH the ring interior until we are physically inside it, then close on a mob.
        // The combat handoff below is gated on being in-ring AND level-synced, because RSR reads
        // PlayerFateId == 0 -- and its IgnoreNonFateInFate filter then DROPS every FATE mob -- until the
        // game's CurrentFate is set and we are synced under the FATE's max level (verified in RSR source:
        // ObjectHelper.IsAttackable's IgnoreNonFateInFate arm + DataCenter.PlayerFateId, which returns 0
        // unless CurrentFate != null AND PlayerSyncedLevel <= CurrentFate->MaxLevel), and BossMod Reborn tags an
        // unsynced / out-of-ring FATE mob Invincible. Chasing the mob's OWN position let an edge-hugging or
        // roaming boss be meleed from just OUTSIDE the ring, where GameState.CurrentFateId() == 0,
        // TrySyncToFate never fires, and the rotation is handed a mob it cannot see -- the reported "rarely
        // attacks at FATEs" (it only worked on the runs where the mob happened to sit well inside the ring).
        // Heading to the centre first guarantees ring entry (hence level-sync), and closing on the mob FROM
        // the centre keeps us inside the ring while we approach.
        var inRing = GameState.CurrentFateId() == fate.FateId;
        // Once we have physically entered the TARGET ring, latch it for the FATE-ended completion branch
        // (a table despawn while we were inside means the FATE is over). Never latched for the prereq.
        if (inRing && !_workingPrereq)
            _wasInside = true;
        // Any ring we have physically stood in this step, the TARGET's or a PREREQUISITE's. _wasInside
        // is target-only because it drives completion, so it cannot gate the stall escapes below:
        // rotating off a prereq we fought in, or blacklisting the Atma FATE we are standing in,
        // forfeits that work just as surely.
        if (inRing)
            _participated = true;
        // Skip a mob we already failed to close on (blacklist expires on its own), so the approach
        // moves to another FATE mob or falls back to the ring centre.
        var avoidMob = System.Environment.TickCount64 < _stuckMobUntil ? _stuckMobId : 0ul;
        var enemy = ctx.Targeting.NearestHostileInFate(fate.FateId, avoidMob);
        // The mob is the GOAL only once we are INSIDE the ring -- outside it we deliberately route to
        // the centre first (see the block above). FATE membership is by the mob's own FateId, NOT ring
        // geometry, so `enemy` is non-null from well outside the ring: keying the stall tracker, the
        // give-up blacklist or the landing watchdog on it out there would blame a mob we are not
        // navigating to, re-key the clock on every nearest-mob swap, and leave the ring escapes below
        // unreachable whenever any FATE mob is loaded. goalMob is what we are actually approaching, so
        // a null goalMob genuinely means "the goal is the ring centre".
        var goalMob = inRing ? enemy : null;
        var goal = goalMob?.Position ?? fate.Position;
        var dist = Vector3.Distance(me, goal);
        // Distances here are centre-to-centre, but a big FATE boss's collision hull parks us several
        // yalms outside its centre, so a fixed band can never be satisfied: the approach hugs the hull
        // and the stall guard below then trips on a mob we are already standing on top of. Game melee
        // range is measured to the hitbox EDGE, so add it. Combat.EngageBand does that AND holds a
        // ranged job at its own standoff instead of marching it into melee.
        //
        // Standing off is safe HERE specifically because the approach has already routed us through
        // the ring centre (goalMob is null until inRing): closing less than the whole way leaves us
        // between the centre and the mob, i.e. still inside the ring and still level-synced. Backing
        // OUT would not be safe, and EngageBand never does that.
        var engageBand = _engaging ? Combat.EngageBand.Disengage(goalMob) : Combat.EngageBand.Engage(goalMob);
        // The ring centre is a place, not a mob: no hitbox, no standoff -- go to it.
        if (goalMob == null)
            engageBand = _engaging ? FateDisengageRange : FateEngageRange;

        // Engage-decision heartbeat (every 5s), the FATE analogue of the leve/beastmen heartbeats:
        // when the run "gets to the FATE and does not attack", this line shows EXACTLY why. The
        // load-bearing fields are the sync pair -- an over-levelled UNSYNCED player cannot damage
        // FATE mobs at all, so inRing/synced is the first thing to check on an intermittent
        // "sometimes attacks" report -- plus whether a FATE mob was even found (mob) and how far
        // (dist), and whether we are grounded (the backend cannot act while mounted/airborne).
        if (System.Environment.TickCount64 - _stateLog > 5000)
        {
            _stateLog = System.Environment.TickCount64;
            DebugLog.Info($"FATE step [target {step.FateId}] engage state: fate={fate.FateId} state={fate.State} " +
                $"progress={fate.Progress} inRing={GameState.CurrentFateId() == fate.FateId} " +
                $"curFate={GameState.CurrentFateId()} synced={GameState.IsSyncedToCurrentFate()} " +
                $"mobFound={enemy != null} mobDist={dist:0.0} engageBand={engageBand:0.0} " +
                $"grounded={Combat.Mount.IsGrounded()} engaging={_engaging} " +
                // Travel mode, so a "why is it walking?" report is answerable from the log alone:
                // flyOk is the zone/config gate (Flight.Allowed) and fly is what vnav was handed.
                $"flyOk={Flight.Allowed(ctx)} fly={Flight.ShouldFly(ctx, dist)}");
        }

        // Airborne and near the goal: LAND + dismount on the walkable ground by it before fighting
        // (RSR / BossMod Reborn cannot act while mounted or airborne). Decide on HORIZONTAL distance, NOT the
        // 3D dist: while airborne the 3D distance stays large because of altitude, which deferred the
        // descent and kept re-issuing a fly-toward-mob move that hovered short of the mob (the same
        // "tries to land but keeps flying up" trap KillTargetExecutor fixed). LandAndDismount routes
        // down to a landable floor point near the mob, so we never try to dismount over bad terrain.
        var horiz = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(goal.X, goal.Z));
        // Grounded, OR the goal drifted back out of the landing band -- either way we are not landing,
        // so the watchdog clock must not keep running as wall time. Clearing it only on "grounded"
        // leaked: a roaming boss leaving the band left us airborne with the clock ticking (as do the
        // early returns above this line), and the next REAL descent then found it already expired and
        // gave up on its very first tick, force-dismounting and blacklisting a reachable mob.
        var landing = !Combat.Mount.IsGrounded() && horiz <= engageBand + 6f;
        if (!landing)
            _landingSince = 0;
        if (landing)
        {
            ctx.Navmesh.Stop();
            ctx.Rotation.Disable();
            LandWithWatchdog(ctx, goal, goalMob);
            return ExecutorStatus.InProgress;
        }

        // Too far to fight: close the gap OURSELVES with the rotation off (vnav owns movement, so it
        // does not fight the backend -- the same handoff the kill grind uses). The fly flag comes from
        // Flight.ShouldFly, which keeps the SHORT final approach on foot (a grounded, un-mounted
        // character cannot follow a 3D path and vnav stalls ~10y short of the mob -- the "inRing +
        // synced, then hovers and never engages" report) while actually GETTING US AIRBORNE for the
        // long haul across the zone to the FATE. The old rule here was ConditionFlag.InFlight alone,
        // which could never become true: mounting sets Mounted, not InFlight, and only a FLYING path
        // makes vnav press jump to take off -- so the character rode the whole way on the ground (the
        // reported "not flying, walking on the ground"). Stop at the tight EngageRange even when the
        // looser hysteresis band let us get here.
        if (dist > engageBand)
        {
            _engaging = false;

            // Stall guard. Without one, an approach that can never finish -- a mob hovering over
            // water or on an off-mesh ledge, a ring centre vnav cannot path to -- sat here forever
            // re-issuing the same move with the rotation OFF. Give up on the goal after
            // ApproachStallMs of no progress and take the cheapest escape that still makes headway.
            //
            // A navmesh that is still BUILDING is not a stall: vnav cannot move us at all until it is
            // ready and an uncached ARR zone takes far longer than ApproachStallMs, so freeze the
            // clock rather than counting build time against a FATE that is up and reachable. (The
            // spawn-wait path gates on IsReady for exactly this reason.)
            if (!ctx.Navmesh.IsReady())
                _approachProgressAt = System.Environment.TickCount64;
            else if (StalledApproaching(fate.FateId, goalMob?.GameObjectId ?? 0, dist))
            {
                ctx.Navmesh.Stop();
                ResetApproachTracker();

                // The goal was a specific mob AND there is another one to try: blacklist it and let
                // the next tick pick the other. Only then -- the blacklist also hides the mob from the
                // engage site below (EngageNearestHostileInFate takes the same avoidMob), so on a
                // single-boss FATE this would leave us standing next to a live, hittable boss doing
                // nothing for ApproachStuckMs. With no alternative, fall through to the arms below.
                if (goalMob != null
                    && ctx.Targeting.NearestHostileInFate(fate.FateId, goalMob.GameObjectId) != null)
                {
                    _stuckMobId = goalMob.GameObjectId;
                    _stuckMobUntil = System.Environment.TickCount64 + ApproachStuckMs;
                    DebugLog.Info($"FATE {fate.FateId}: no approach progress on a mob for " +
                        $"{ApproachStallMs / 1000}s at {dist:0}y; trying a different one.");
                    return ExecutorStatus.InProgress;
                }

                // No alternative target. Once we have actually stood in a ring this step
                // (_participated, latched on entry to the TARGET's or a PREREQUISITE's ring), neither
                // escape below is acceptable: rotating walks away from a FATE we fought in, and
                // blacklisting a FATE we are standing IN makes NearestActive return null next tick,
                // which the fate == null branch above reads as "FATE ended" and COMPLETES -- a false
                // completion on a still-running FATE. Retry instead; that still terminates, because
                // every FATE ends on its own timer and the FATE-ended branch then completes normally.
                if (_participated)
                {
                    WaitLog($"FATE {fate.FateId}: cannot reach the goal ({dist:0}y) but we already " +
                        "participated; retrying until it ends rather than forfeiting the credit.");
                    Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId);
                    return ExecutorStatus.InProgress;
                }

                // Never entered the ring. A specific book FATE rotates -- the controller stamps it and
                // moves to the next incomplete objective, coming back later (the same non-failing
                // escape the spawn-wait window uses).
                if (step.FateId != 0)
                {
                    DebugLog.Warn($"FATE {fate.FateId}: could not path to the ring within " +
                        $"{ApproachStallMs / 1000}s (stuck at {dist:0}y); rotating to another objective.");
                    return ExecutorStatus.Rotate;
                }

                // Atma's "any active FATE" mode has nothing to rotate TO, so blacklist this FATE and
                // let NearestActive pick the next nearest one. Safe here only because !_participated.
                _stuckFateId = fate.FateId;
                _stuckFateUntil = System.Environment.TickCount64 + ApproachStuckMs;
                DebugLog.Warn($"FATE {fate.FateId}: could not path to the ring within " +
                    $"{ApproachStallMs / 1000}s (stuck at {dist:0}y); trying the next nearest FATE.");
                return ExecutorStatus.InProgress;
            }

            ctx.Rotation.Disable();
            Combat.Mount.EnsureMounted(ctx, dist);
            // Stop distance matches the band we just failed: hitbox- and role-aware for a mob,
            // the plain ring distance when the goal is the centre.
            var stopAt = goalMob != null ? Combat.EngageBand.Stop(goalMob) : FateEngageRange - 1f;
            ctx.Navmesh.MoveCloseTo(goal, Flight.ShouldFly(ctx, dist), stopAt);
            return ExecutorStatus.InProgress;
        }

        // Arrived: the approach succeeded, so drop its progress tracker (the next one starts clean).
        ResetApproachTracker();

        // In range. Stop and get fully grounded before fighting.
        ctx.Navmesh.Stop();
        if (!Combat.Mount.IsGrounded())
        {
            ctx.Rotation.Disable();
            LandWithWatchdog(ctx, goal, goalMob);
            return ExecutorStatus.InProgress;
        }

        // Do NOT hand off to the rotation until we are BOTH physically inside the ring AND level-synced.
        // Until both hold, the backend has nothing it will act on -- RSR's PlayerFateId is 0 so
        // IgnoreNonFateInFate drops every FATE mob (verified: ObjectHelper.cs / DataCenter.cs), and BossMod Reborn
        // tags the mob Invincible -- so an early handoff just hard-targets a mob nothing swings at (the
        // "rarely attacks at FATEs" report). TrySyncToFate (issued at the top of Update while in the ring)
        // keeps sending the idempotent "/levelsync on"; re-issue it here and hold with the rotation OFF
        // until sync registers. Because the goal above routes through the ring centre until inRing, we are
        // guaranteed to actually enter the ring and sync -- this WAITS for sync, it cannot idle outside the
        // ring forever. (_wasInside is latched on ring entry above, so a FATE that ends during this wait
        // still completes via the FATE-ended branch.)
        if (!inRing || !GameState.IsSyncedToCurrentFate())
        {
            ctx.Rotation.Disable();
            TrySyncToFate(fate.FateId);
            WaitLog($"FATE {fate.FateId}: in position; waiting for level-sync before engaging " +
                $"(inRing={inRing}, synced={GameState.IsSyncedToCurrentFate()})");
            return ExecutorStatus.InProgress;
        }
        _engaging = true;

        // (Level-sync is handled up-front by TrySyncToFate the moment we entered the ring, so by the
        // time we are standing among the mobs the backend already sees a damageable, in-sync FATE mob.)

        // Now standing among the mobs, hand off to the combat backend. Two modes:
        //
        // (A) The backend OWNS FATE targeting (RSR): it auto-detects the active FATE and, in Auto
        //     mode with the FATE settings (ConfigureForFate: HostileType AllTargetsWhenSolo,
        //     IgnoreNonFateInFate, TargetFatePriority, and a FATE-scoped TargetFreely), auto-selects
        //     and attacks FATE mobs on its own. Relicable must NOT hard-target or Attack1-mark a mob
        //     here -- that fights RSR's own FATE target selection (making it thrash between our pick
        //     and its pick). We only keep it configured + in Auto; the approach logic above already
        //     positioned us among the mobs (moving toward the nearest FATE hostile each tick).
        //
        // (B) The backend only rotates on OUR hard target (BossMod Reborn / none): hard-target the nearest
        //     FATE hostile ourselves and enable the rotation. Membership is by the mob's own FateId
        //     (not ring geometry), so we still acquire a mob while landed at the ring edge, and the
        //     Attack1 mark hands BossMod Reborn a clear priority target.
        if (ctx.Rotation.OwnsFateTargeting)
        {
            // Re-apply the FATE config (re-arms the FATE-scoped TargetFreely override that Disable
            // clears while approaching) and keep RSR in Auto -- both edge/dedup-guarded, so per-tick
            // calls are cheap. No SetTarget, no /enemysign: RSR picks and pulls the FATE mobs.
            ctx.Rotation.ConfigureForFate();
            ctx.Rotation.EnableAuto();
            Combat.CombatAssist.Engage(ctx); // chocobo + BossMod Reborn avoidance
            // No FATE mob loaded right now (between waves): relax the engage latch, exactly as the
            // backend-targets path does in its no-mob branch, so the NEXT mob is approached to tight
            // melee range rather than held at the looser hysteresis band. RSR stays in Auto (a
            // harmless idle) so it opens the instant a mob streams into range.
            if (enemy == null)
                _engaging = false;
            if (_engageLoggedId != 1)
            {
                _engageLoggedId = 1; // once-per-engage marker (RSR owns targeting -> no per-mob id)
                DebugLog.Info($"FATE {fate.FateId}: RSR owns targeting (synced={GameState.IsSyncedToCurrentFate()}, " +
                    $"inRing={GameState.CurrentFateId() == fate.FateId}); Auto mode on, letting RSR pick FATE mobs.");
            }
        }
        else if (ctx.Targeting.EngageNearestHostileInFate(fate.FateId, avoidMob))
        {
            // Attack1-mark the FATE mob (via the game chat box, like the kill grind's /enemysign) so
            // the backend prioritises attacking THIS mob rather than idling next to it -- the marker
            // is the difference between "hard-targets but never swings" and actually opening on it.
            MarkTarget();
            // Log the engage once per distinct target (reuse the mark dedup, set by MarkTarget), with
            // the sync state -- the decisive line for "hard-targets but never swings": synced=false
            // here means the backend is being told to fight a mob the player cannot damage.
            var tid = Plugin.TargetManager.Target?.GameObjectId ?? 0;
            if (tid != 0 && tid != _engageLoggedId)
            {
                _engageLoggedId = tid;
                DebugLog.Info($"FATE {fate.FateId}: engaging a FATE mob (synced={GameState.IsSyncedToCurrentFate()}, " +
                    $"inRing={GameState.CurrentFateId() == fate.FateId}); enabling the rotation.");
            }
            ctx.Rotation.EnableAuto();
            Combat.CombatAssist.Engage(ctx); // chocobo + BossMod Reborn avoidance
        }
        else
        {
            // No mob in the ring right now (a brief gap between waves): drop out of the engage band so
            // we do not sit level-synced at melee while the next wave streams in elsewhere.
            _engaging = false;
            // ...but "no FATE mob" is not "no enemy". A non-FATE hostile that aggroed while we stood
            // in the ring carries FateId 0, so NearestHostileInFate never returns it and this branch
            // disabled the rotation while it kept hitting us. Fight it before idling.
            if (!Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
                ctx.Rotation.Disable();
        }

        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        // Halt movement so a running path does not bleed into the next step (e.g. an
        // AetheryteTeleport, whose cast is cancelled by movement).
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        Combat.CombatAssist.Disengage(ctx);
        // Remove the VISIBLE-marker flag we dropped on the FATE (it is never our
        // destination -- see the Update flag comment). Leaving it set hands a stray flag
        // to the next step; the treasure-map loop in particular reads "a flag exists" as
        // "a treasure to run" and would chase this one forever. The next objective drops
        // its own fresh flag (Start resets _flaggedFor), so nothing is lost.
        MapFlag.Clear();
    }

    // Land + dismount near the goal, with a WATCHDOG. The other half of the unreachable-goal problem:
    // a mob hovering over water or a shaft has no floor beneath it, so the descent never completes
    // and this branch looped here forever with the rotation disabled -- and because it returns before
    // the travel branch, the distance-based stall guard above could never see it either. After
    // FateLandTimeoutMs, force a bare dismount and blacklist the mob so the next tick approaches a
    // different one (or the ring centre). Mirrors KillTargetExecutor.LandWithWatchdog.
    private void LandWithWatchdog(ExecutionContext ctx, Vector3 goal, Dalamud.Game.ClientState.Objects.Types.IGameObject? enemy)
    {
        var now = System.Environment.TickCount64;
        if (_landingSince == 0)
            _landingSince = now;

        if (now - _landingSince > FateLandTimeoutMs)
        {
            DebugLog.Warn($"FATE: could not land near the goal within {FateLandTimeoutMs / 1000}s " +
                "(no floor beneath it?); dismounting and trying another target.");
            Combat.Mount.EnsureDismounted();
            _landingSince = 0;
            ctx.Navmesh.Stop();
            ResetApproachTracker();
            if (enemy != null)
            {
                _stuckMobId = enemy.GameObjectId;
                _stuckMobUntil = now + ApproachStuckMs;
            }
            return;
        }

        Combat.Mount.LandAndDismount(ctx, goal);
    }

    // Approach progress tracking. Returns true when we have gone ApproachStallMs without getting
    // materially closer to the SAME goal -- i.e. the goal is very likely unreachable.
    //
    // Keyed on goal IDENTITY (which FATE, which mob; mobId 0 = the ring centre), never on position:
    // the goal is usually a live mob that moves every tick, so a position key would restart the
    // clock constantly and the guard could never trip. Switching goals -- a different mob, the mob
    // dying, falling back to the centre -- legitimately restarts it, because that is a genuinely
    // new approach with its own chance of succeeding.
    private bool StalledApproaching(ushort fateId, ulong mobId, float dist)
    {
        var now = System.Environment.TickCount64;

        if (fateId != _approachFate || mobId != _approachMob)
        {
            _approachFate = fateId;
            _approachMob = mobId;
            _approachBest = dist;
            _approachProgressAt = now;
            return false;
        }

        if (dist < _approachBest - ApproachProgress)
        {
            _approachBest = dist;
            _approachProgressAt = now;
            return false;
        }

        return now - _approachProgressAt > ApproachStallMs;
    }

    private void ResetApproachTracker()
    {
        _approachFate = 0;
        _approachMob = 0;
        _approachBest = float.MaxValue;
        _approachProgressAt = 0;
    }

    // Throttled status log (every 10s) so the wait state is visible without spam.
    private void WaitLog(string message)
    {
        if (System.Environment.TickCount64 - _waitLog < 10000)
            return;
        _waitLog = System.Environment.TickCount64;
        DebugLog.Info(message);
    }

    // Drive the interaction that STARTS an NPC-initiated FATE: travel to the start NPC and interact
    // with it (TextAdvance carries the Talk / Yes-No) until the FATE flips to Running, at which point
    // the caller resumes the normal sync + engage path. When the FATE has no start NPC (it begins on
    // its own) or the NPC has not streamed in yet, move to the ring and wait for it to start.
    private ExecutorStatus DriveFateStart(IFate fate, ExecutionContext ctx, Vector3 me)
    {
        ctx.Rotation.Disable(); // interacting, not fighting
        var npc = FateStartNpc(fate);

        // No (loaded) start NPC: head to the ring so it streams in, and wait for the FATE to go Running.
        if (npc == null)
        {
            var to = ctx.Navmesh.LandableFloorForMapPoint(fate.Position) ?? fate.Position;
            var dc = Vector3.Distance(me, to);
            if (dc > 5f)
            {
                Combat.Mount.EnsureMounted(ctx, dc);
                ctx.Navmesh.MoveCloseTo(to, Flight.ShouldFly(ctx, dc), 3f);
            }
            else
            {
                ctx.Navmesh.Stop();
                Combat.Mount.LandAndDismount(ctx, to);
            }
            WaitLog($"FATE {fate.FateId} preparing; waiting for its start NPC to load");
            return ExecutorStatus.InProgress;
        }

        var dist = Vector3.Distance(me, npc.Position);
        var horiz = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(npc.Position.X, npc.Position.Z));

        // Airborne near the NPC: land + dismount before interacting (interaction no-ops while mounted).
        // Decide on HORIZONTAL distance, NOT the 3D dist: flying in, the 3D distance stays large because of
        // altitude, which deferred the descent and left the character hovering above the NPC unable to
        // land -- the reported "can't land" at an NPC-start FATE (e.g. Schism's Storm Private), and the
        // same altitude trap the mob approach hit. LandAndDismount routes down to a landable floor by it.
        if (!Combat.Mount.IsGrounded() && horiz <= FateInteractRange + 6f)
        {
            ctx.Navmesh.Stop();
            Combat.Mount.LandAndDismount(ctx, npc.Position);
            return ExecutorStatus.InProgress;
        }

        // Too far: close in, flying only when we can actually follow a flight path (Flight.ShouldFly:
        // airborne already, or mounted on a long enough leg). A grounded, un-mounted character told to
        // fly a 3D path cannot follow it and stalls short of the NPC, so it never reaches interact range
        // and never talks to start the FATE ("doesn't talk to start") -- but the previous InFlight-only
        // gate went too far the other way and rode the ENTIRE haul on the ground, since mounting alone
        // never sets InFlight (only a flying path makes vnav jump to take off).
        if (dist > FateInteractRange)
        {
            Combat.Mount.EnsureMounted(ctx, dist);
            ctx.Navmesh.MoveCloseTo(npc.Position, Flight.ShouldFly(ctx, dist), FateInteractRange - 1f);
            return ExecutorStatus.InProgress;
        }

        // In range: stop, ground, then interact (throttled). Confirm a Yes-No if TextAdvance did not.
        ctx.Navmesh.Stop();
        if (!Combat.Mount.IsGrounded())
        {
            Combat.Mount.LandAndDismount(ctx, npc.Position);
            return ExecutorStatus.InProgress;
        }
        if (Interaction.DialogueMenu.ConfirmYes())
            return ExecutorStatus.InProgress;
        if (System.Environment.TickCount64 - _startInteractThrottle > 2000)
        {
            _startInteractThrottle = System.Environment.TickCount64;
            InteractObject(npc);
            DebugLog.Info($"FATE {fate.FateId}: interacting with start NPC '{npc.Name.TextValue}' to begin it");
        }
        WaitLog($"FATE {fate.FateId}: starting via its NPC");
        return ExecutorStatus.InProgress;
    }

    // Drive the interaction that SPAWNS an NPC-spawned prerequisite FATE (e.g. FATE 610 "The Enemy of My
    // Enemy", spawned by talking to the standing BNpc "Mianne Thousandmalm"). Distinct from an
    // NPC-INITIATED FATE (which DriveFateStart handles): that FATE sits in the table in Preparing with a
    // readable MotivationNpc; THIS one is not in the table at all until the NPC is engaged, so there is no
    // FATE object -- we must find the NPC by NAME in the object table (a BNpc; its object BaseId is not
    // its BNpcName id) and interact to spawn the FATE. Travel to the FATE's authored staging coord (where
    // the NPC stands), find the NPC by name, and interact + accept the Yes/No, which spawns the FATE; the
    // caller's normal prereq flow then takes over next tick. When the NPC is absent (it appears only when
    // the FATE is on its rotation), hold at the staging spot, and rotate to another objective if it does
    // not show within the wait window (the same rotation the stage-and-wait path uses), so a leve on
    // cooldown never hangs the run.
    private ExecutorStatus DriveFateSpawn(string npcName, uint prereqFateId, ExecutionContext ctx, Vector3 me)
    {
        ctx.Rotation.Disable(); // interacting, not fighting

        // Wait for the zone navmesh before travelling (same guard as the stage-and-wait path).
        if (!ctx.Navmesh.IsReady())
        {
            WaitLog("zone navmesh still loading; waiting before travelling to the FATE-spawn NPC");
            return ExecutorStatus.InProgress;
        }

        var staging = Data.BraveBookPositions.FateWorld(prereqFateId) is { } sp
            ? ctx.Navmesh.LandableFloorForMapPoint(sp)
            : (Vector3?)null;

        // Drop a visible marker at the staging spot (like the stage-and-wait path), refreshed on change.
        if (staging is { } marker && _flaggedFor != marker)
        {
            if (MapFlag.Set(marker))
                _flaggedFor = marker;
        }

        var npc = ctx.Targeting.FindNamed(npcName);

        // NPC not loaded (it spawns only when the FATE is on its rotation): travel to / hold at the
        // staging spot and wait, but rotate away if it never appears within the wait window so a FATE on
        // cooldown does not hang the run (mirrors the stage-and-wait rotate at the top of Update).
        if (npc == null)
        {
            var rotateSeconds = ctx.FateWaitSeconds > 0 ? ctx.FateWaitSeconds : ctx.Config.FateRotateSeconds;
            if (rotateSeconds > 0 && System.Environment.TickCount64 - _startTick >= rotateSeconds * 1000L)
            {
                ctx.Navmesh.Stop();
                DebugLog.Info($"FATE-spawn NPC '{npcName}' not present after {rotateSeconds}s; rotating to the next objective");
                return ExecutorStatus.Rotate;
            }
            if (staging is { } to)
            {
                var d = Vector3.Distance(me, to);
                if (d > 5f)
                {
                    Combat.Mount.EnsureMounted(ctx, d);
                    ctx.Navmesh.MoveCloseTo(to, Flight.ShouldFly(ctx, d), 3f);
                }
                else
                {
                    ctx.Navmesh.Stop();
                    Combat.Mount.LandAndDismount(ctx, to);
                    WaitLog($"At the FATE-spawn spot; waiting for '{npcName}' to appear (FATE {prereqFateId})");
                }
            }
            else
            {
                WaitLog($"No staging position for FATE-spawn NPC '{npcName}' (FATE {prereqFateId})");
            }
            return ExecutorStatus.InProgress;
        }

        // NPC loaded: approach and interact (mirrors DriveFateStart's approach + interact). Land on
        // HORIZONTAL distance (altitude must not defer the descent), and take the fly flag from
        // Flight.ShouldFly so a short grounded approach WALKS to the NPC instead of stalling on a 3D
        // flight path it cannot follow -- the same "can't land / never reaches the NPC" trap as above --
        // while a long haul mounts and actually flies instead of riding the whole way.
        var dist = Vector3.Distance(me, npc.Position);
        var horiz = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(npc.Position.X, npc.Position.Z));
        if (!Combat.Mount.IsGrounded() && horiz <= FateInteractRange + 6f)
        {
            ctx.Navmesh.Stop();
            Combat.Mount.LandAndDismount(ctx, npc.Position);
            return ExecutorStatus.InProgress;
        }
        if (dist > FateInteractRange)
        {
            Combat.Mount.EnsureMounted(ctx, dist);
            ctx.Navmesh.MoveCloseTo(npc.Position, Flight.ShouldFly(ctx, dist), FateInteractRange - 1f);
            return ExecutorStatus.InProgress;
        }
        ctx.Navmesh.Stop();
        if (!Combat.Mount.IsGrounded())
        {
            Combat.Mount.LandAndDismount(ctx, npc.Position);
            return ExecutorStatus.InProgress;
        }
        // Accept her proposal (a Yes/No) if TextAdvance did not carry it; else fire the talk. Accepting
        // spawns the FATE, which the normal prereq flow then engages next tick.
        if (Interaction.DialogueMenu.ConfirmYes())
            return ExecutorStatus.InProgress;
        if (System.Environment.TickCount64 - _startInteractThrottle > 2000)
        {
            _startInteractThrottle = System.Environment.TickCount64;
            InteractObject(npc);
            DebugLog.Info($"FATE-spawn: interacting with '{npc.Name.TextValue}' to spawn prereq FATE {prereqFateId}");
        }
        WaitLog($"Spawning prereq FATE {prereqFateId} via '{npcName}'");
        return ExecutorStatus.InProgress;
    }

    // The live, TARGETABLE object you interact with to begin an NPC-initiated FATE, or null when the
    // FATE starts on its own (no MotivationNpc) or the NPC has not loaded yet. FateContext.MotivationNpc
    // is the initiator's EntityId; 0 and the 0xE0000000 "none" sentinel both mean "no start NPC". Read
    // straight off the FateContext (Dalamud's IFate does not surface it).
    private static unsafe IGameObject? FateStartNpc(IFate fate)
    {
        if (fate.Address == nint.Zero)
            return null;
        var id = ((CSFateContext*)fate.Address)->MotivationNpc;
        if (id == 0 || id == 0xE0000000)
            return null;
        var obj = Plugin.ObjectTable.SearchByEntityId(id);
        return obj is { IsTargetable: true } ? obj : null;
    }

    // Fire the game's object interaction (the "talk"/RMB) on a world object -- the same verified call
    // the leve/NPC flows use: TargetSystem.InteractWithObject(GameObject*, checkLineOfSight).
    private static unsafe void InteractObject(IGameObject obj)
    {
        var ts = CSTargetSystem.Instance();
        if (ts == null || obj.Address == nint.Zero)
            return;
        Plugin.TargetManager.Target = obj;
        ts->InteractWithObject((CSGameObject*)obj.Address, false);
    }

    // Level-sync to the FATE we are physically standing in. Gated on GetCurrentFateId (ring
    // membership) rather than on reaching a mob, so we sync as soon as we are in the ring.
    //
    // "/levelsync ON", not the bare toggle: a toggle flips sync OFF again if our synced-state read is
    // momentarily stale (e.g. read the same tick the fate is (re)joined), which then leaves the
    // backend unable to attack; "on" is idempotent -- the game ignores a redundant on -- and matches
    // the community FATE-sync tools. Sent through the game chat box (ECommons.Chat) because Dalamud's
    // ICommandManager.ProcessCommand silently drops native commands like /levelsync. Throttled so we
    // do not hammer it while the sync registers; guarded by IsSyncedToCurrentFate so it stops once on.
    private void TrySyncToFate(ushort fateId)
    {
        // The level-sync option only exists while inside a FATE ring; issuing it elsewhere just errors
        // in the log. GetCurrentFateId is the fate we are physically in (0 when outside any ring).
        if (fateId == 0 || GameState.CurrentFateId() != fateId)
            return;
        if (GameState.IsSyncedToCurrentFate())
            return;
        if (System.Environment.TickCount64 - _syncThrottle <= 3000)
            return;
        _syncThrottle = System.Environment.TickCount64;
        try { ECommons.Automation.Chat.ExecuteCommand("/levelsync on"); }
        catch (System.Exception ex) { DebugLog.Warn($"FATE: /levelsync failed: {ex.Message}"); }
    }

    // Place the Attack1 head sign on the current hard target, once per distinct target. Sent through
    // the GAME chat box (ECommons.Chat), NOT ctx.Commands.Run -- Dalamud's ProcessCommand only
    // dispatches Dalamud-registered commands and silently drops native ones like /enemysign (the same
    // trap documented in KillTargetExecutor). Deduplicated by GameObjectId so a mob we are already on
    // is not re-marked every tick.
    private void MarkTarget()
    {
        var target = Plugin.TargetManager.Target;
        if (target == null || target.GameObjectId == _markedId)
            return;
        _markedId = target.GameObjectId;
        try { ECommons.Automation.Chat.ExecuteCommand("/enemysign attack1 <t>"); }
        catch (System.Exception ex) { DebugLog.Warn($"FATE: /enemysign failed: {ex.Message}"); }
    }
}

// FATE lookup over Dalamud's IFateTable.
internal static class Fates
{
    public static IFate? ById(ushort fateId)
    {
        foreach (var f in Plugin.FateTable)
            if (f.FateId == fateId)
                return f;
        return null;
    }

    // avoidId: a FATE the caller judged unreachable (it could not path to the ring at all), skipped
    // for a cooldown so the Atma "any active FATE" mode moves to the next nearest one instead of
    // pathing at an unreachable ring forever.
    public static IFate? NearestActive(ushort avoidId = 0)
    {
        IFate? best = null;
        var bestDist = float.MaxValue;
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        foreach (var f in Plugin.FateTable)
        {
            if (avoidId != 0 && f.FateId == avoidId)
                continue;
            // Running FATEs, PLUS not-yet-started NPC-initiated ones: a "speak to begin" boss FATE has a
            // MotivationNpc and sits in Preparing until interacted with, so including it here lets the
            // Atma "any FATE in zone" mode walk over and START it (DriveFateStart) instead of treating
            // it as "no active FATE" and skipping it forever.
            var eligible = f.State == FateState.Running
                || (f.State != FateState.Ended && f.State != FateState.Failed && HasStartNpc(f));
            if (!eligible)
                continue;
            var d = Vector3.DistanceSquared(me, f.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = f;
            }
        }
        return best;
    }

    // True when the FATE is NPC-initiated -- it carries a MotivationNpc (start NPC) id. Read straight
    // off the FateContext; the id is set even before the NPC object streams in. 0 / 0xE0000000 = none.
    private static unsafe bool HasStartNpc(IFate fate)
    {
        if (fate.Address == nint.Zero)
            return false;
        var id = ((CSFateContext*)fate.Address)->MotivationNpc;
        return id != 0 && id != 0xE0000000;
    }
}
