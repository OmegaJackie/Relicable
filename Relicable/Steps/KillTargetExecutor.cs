using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Relicable.Model;

namespace Relicable.Steps;

// The core of the kill grind. Acquires the named enemy via the targeting layer,
// enables the combat backend, and reports Complete only when the objective's
// authoritative counter has risen by the required amount. A stray kill cannot
// advance the step, which prevents state-machine desync (DESIGN.md 5.4).
public sealed class KillTargetExecutor : ITaskExecutor
{
    public StepType Handles => StepType.KillTarget;

    // Distance (yalms) at which we stop navigating and hand off to the rotation.
    // Tight enough that melee jobs are in range, loose enough to absorb pathing
    // overshoot and mob movement.
    private const float EngageRange = 4f;

    // Hysteresis: once engaged, keep fighting until the mob is beyond this looser range.
    // A single EngageRange threshold made a mob that wobbles around 4y flip between the
    // engage branch (RSR TargetOnly) and the travel branch (RSR Off) every tick -- the
    // Off/On thrash that stops RSR ever settling into its rotation, so it "engages but
    // never attacks". Only a mob that genuinely moved away re-enters the mount/close branch.
    private const float DisengageRange = 8f;

    // While flying in, start landing once this close HORIZONTALLY to the mob. The 3D
    // distance stays large while airborne (altitude), which otherwise defers the descent
    // and keeps re-issuing a fly-toward-mob move that fights it -- the "tries to land but
    // keeps flying up" bug. Decided on horizontal distance so altitude cannot defer it.
    private const float LandHorizontal = 10f;
    // Only mount/fly to close a gap longer than this; short hops stay on foot so a brief
    // takeoff cannot restart the land/fly oscillation right on top of the mob.
    private const float FlyMinDistance = 30f;

    // True once we have committed to landing + dismounting for the current mob, so an
    // altitude or mob wobble across the range boundary keeps landing instead of flipping
    // back to flying. Reset on Start and once grounded.
    private bool _landing;

    // When the current landing attempt began (0 = not landing / already grounded). A mob over a
    // shaft/void/water with no floor beneath it cannot be descended to, so LandAndDismount would loop
    // forever with the rotation disabled ("flew to the mob, never lands, no attack"). After
    // LandTimeoutMs we force a bare dismount and blacklist the mob so the next acquire moves on.
    private long _landingSince;
    private const long LandTimeoutMs = 5000;

    // True once we are fighting a mob, so a drift past EngageRange (up to DisengageRange)
    // keeps us in the engage branch with RSR enabled instead of toggling it off. Reset on
    // Start and when the target is lost.
    private bool _engaging;

    // The target id we last re-armed RSR (Manual) for. The resync that forces the mode to re-send
    // (to recover from RSR auto-off after a kill) must fire only ONCE per pull, not every tick:
    // re-sending "Manual" every frame thrashes RSR so it never settles and never attacks. Keyed on
    // the engaged target so a new mob re-arms but a mob we are already on does not. Reset on Start.
    private ulong _manualArmedId;

    // Throttle for the /levelsync issued when engaging a FATE-spawned note mob (Configuration.AllowFateNoteKills).
    private long _fateSyncThrottle;

    // The authored spawn position we last dropped the map flag for. Used to refresh the flag once
    // per objective (a fresh flag EVERY time) instead of reusing a stale flag from a prior mob.
    private Vector3? _flaggedFor;

    // Approach commitment. During the long-distance travel branch we keep navigating to this exact
    // mob (by GameObjectId) instead of re-picking the straight-line nearest every tick. In multi-tier
    // spots like U'Ghamaro Mines the nearest note mob swaps as we fly a winding route, so a per-tick
    // pick flip-flops the destination between two mobs and the character shuttles back and forth
    // (the "constantly going back and forth" symptom). Cleared when the mob dies/despawns, is lost,
    // or stalls (below). Reset on Start.
    private ulong _travelLockId;

    // Stall guard for the lock: the closest we have gotten to the locked mob and when we last made
    // progress toward it. If we cannot get closer for StallTimeoutMs -- e.g. the mob sits on
    // unreachable geometry -- we give up the lock and blacklist that mob for StuckCooldownMs so the
    // next acquire picks a different target (or falls through to the outward search) instead of
    // tunnelling an unreachable one forever, the only failure the commitment could otherwise add.
    private float _lockBestDist;
    private long _lockProgressAt;
    private ulong _stuckId;
    private long _stuckUntil;

    // How much closer (yalms) counts as real approach progress, and how long without it before we
    // treat the locked mob as unreachable. Generous so a normal winding flight -- which closes far
    // more than this in seconds -- never trips it; only a genuinely stuck approach does.
    private const float StallProgress = 3f;
    private const long StallTimeoutMs = 12000;
    private const long StuckCooldownMs = 20000;

    // ---- Multi-name grind (StepData.TargetNames; the base relic's three-beastman hunt) ----
    //
    // The step wants several enemy TYPES at once and takes whichever is nearest, so it must also
    // stop taking a type once that type is finished -- the quest caps each at eight and silently
    // ignores further kills, so tunnelling a capped type standing right next to you would grind
    // forever. Two independent signals retire a type, because neither alone is sufficient:
    //
    //   * the local per-name tally reaching the cap. Exact while the step runs, but it is zeroed by
    //     Start (a re-plan, a death recovery), so on its own it would re-offer a finished type;
    //   * NoCreditRetireAfter kills of that type that produced no rise in the quest's own counter
    //     total. Survives a re-plan (it re-derives from the game), and needs no mob->nibble mapping,
    //     of which only White Mage's is known.
    //
    // The second is deliberately given a GRACE window and a repeat requirement: a credit can land a
    // frame or two after the mob dies, and a previous per-nibble cap detector that judged on the
    // death frame declared types capped after ~2 kills and ended hunts early (v1.4.126.0). Waiting
    // for the counter to stay flat across CreditGraceMs, twice, cannot be fooled by that timing.
    //
    // If every type ends up retired while the step is still incomplete, the retirements are thrown
    // away and all types are re-offered -- so a wrong retirement costs a few kills, never a deadlock.
    private const long CreditGraceMs = 5000;
    private const int NoCreditRetireAfter = 2;

    private readonly System.Collections.Generic.Dictionary<string, int> _killsByName = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Generic.Dictionary<string, int> _noCreditByName = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Generic.HashSet<string> _retired = new(System.StringComparer.OrdinalIgnoreCase);
    // Scratch for the per-tick wanted set. Reused (not reallocated) because it is rebuilt every tick.
    private readonly System.Collections.Generic.List<string> _wanted = new();

    // The name of the mob currently latched in _engagedId, so its death can be attributed to a type.
    private string _engagedName = string.Empty;

    // The outstanding "did that kill actually credit?" check: the type killed, the counter total
    // from BEFORE the kill, and when it was observed. Empty name = nothing pending.
    private string _pendingName = string.Empty;
    private int _pendingBaseTotal;
    private long _pendingAt;

    // The quest counter total as of this tick and as of the previous one. The previous tick's value
    // is what a credit check compares against: it is guaranteed to pre-date the kill, whereas the
    // value read on the death tick may already include the credit.
    private int _qTotal = -1;
    private int _qTotalLastTick = -1;

    public void Start(StepData step, ExecutionContext ctx)
    {
        // RSR may have toggled itself off after the previous fight; force the next
        // mode change to actually re-send rather than being suppressed as a no-op.
        ctx.Rotation.ResyncNextDispatch();
        ctx.BossModReborn.Resync();
        // Allow a fresh Attack1 mark on the next acquired target. RSR's hostile-target
        // type is intentionally left untouched; the kill is driven by Manual mode.
        _markedId = 0;
        // Reset the local kill tally. This is per-step state and MUST reset (the executor is a
        // singleton shared by all three beastmen steps) -- which is exactly why the local tally
        // could never be the authority for the hunt: Start() runs again on every death recovery
        // and re-plan, silently rewinding the count to 0. The quest's own counters (below) are
        // what survive that, and they are now the authority.
        _engagedId = 0;
        _engagedName = string.Empty;
        _localKills = 0;
        // Quest-counter state: only the "last logged total" marker (the completion authority is a
        // stateless absolute read of the game counters, so nothing else needs resetting).
        _qLoggedTotal = -1;
        // Multi-name state. All of it is per-step and MUST reset: the executor is a singleton, so a
        // retirement carried over from a previous hunt would refuse a type this one still needs.
        _killsByName.Clear();
        _noCreditByName.Clear();
        _retired.Clear();
        _wanted.Clear();
        _pendingName = string.Empty;
        _pendingBaseTotal = 0;
        _pendingAt = 0;
        _qTotal = -1;
        _qTotalLastTick = -1;
        _landing = false;
        _landingSince = 0;
        _engaging = false;
        _manualArmedId = 0;
        _flaggedFor = null;
        _travelLockId = 0;
        _lockBestDist = float.MaxValue;
        _lockProgressAt = 0;
        _stuckId = 0;
        _stuckUntil = 0;
        ResetSearch();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Completion: an authoritative game counter wherever one exists, and only a local kill
        // tally as a last resort.
        //   * MonsterSlot / ItemCount  -> the RelicNote slot or the inventory count (unchanged).
        //   * UseQuestKillCounter      -> the relic quest's OWN QuestWork nibbles. Set only by
        //     BaseRelicHuntGenerator for the beastmen hunt. The old comment here claimed "the
        //     base-relic quest has no readable per-mob counter" -- that is FALSE, and it is what
        //     forced the fragile local tally. Sapphire's JobWhm001.cpp proves the quest keeps a
        //     0..8 counter per beastman type in its work bytes (see BeastmanCounters).
        //   * otherwise                -> the local tally (FATE/leve kills).
        var kind = ctx.CurrentObjective?.Completion.Kind;
        var authoritative = kind is CompletionKind.MonsterSlot or CompletionKind.ItemCount;

        // Count a local kill when the mob we were fighting died or despawned. Used by the
        // non-authoritative paths (FATE/leve) and as the quest-counter path's fallback when the
        // quest counters cannot be read.
        if (!authoritative && _engagedId != 0 && !EngagedAlive())
        {
            _localKills++;
            NoteKill(_engagedName);
            _engagedId = 0;
            _engagedName = string.Empty;
            Log($"kill observed ({_localKills} local)");
        }

        // The quest-counter path for the beastmen hunt.
        if (!authoritative && step.UseQuestKillCounter
            && QuestCounterComplete(step, ctx) is { } questDone)
        {
            if (questDone)
            {
                ctx.Rotation.Disable();
                return ExecutorStatus.Complete;
            }
            // The quest counter is readable and says this step's target is NOT yet reached. It is
            // the authority: ignore the local tally so a mis-count cannot end the step early.
            _localKills = 0;
        }

        // Settle any outstanding "did that kill credit?" check against the counter total just read,
        // then roll the total forward so the NEXT kill compares against a pre-kill value.
        ResolveCreditWatch();
        _qTotalLastTick = _qTotal;

        // Authoritative kinds (RelicNote monster slot / item count): compare the ABSOLUTE game
        // counter to the required total, exactly as the controller's IsObjectiveComplete does. The
        // earlier baseline-DELTA check (counter - baseline >= count) wrongly required `count` kills
        // ON TOP OF whatever progress the slot already had at Start; the game caps the slot at its
        // required total, so a slot even partway done (a prior run, a manual kill) could never reach
        // the delta and the step ran forever -- "got the required kills but it never advances to the
        // next target". The same cap exists on the beastmen nibbles (Sapphire skips the increment at
        // 8), which is why the quest-counter path above is likewise absolute, never a delta.
        var complete = authoritative
            ? ReadObjectiveCounter(ctx) >= step.Count
            : _localKills >= step.Count;
        if (complete)
        {
            ctx.Rotation.Disable();
            return ExecutorStatus.Complete;
        }

        // Ensure we have a valid target; reacquire if the current one is gone.
        // For relic-note monster slots, ask the game whether a candidate counts
        // (RelicNote.IsMonsterNoteTarget) instead of matching by name; fall back
        // to name/dataId for non-note kills (FATEs, leves).
        //
        // Commitment: prefer the mob we are already approaching (_travelLockId) and skip one we just
        // judged unreachable (_stuckId, for a cooldown), so the acquire is sticky rather than a fresh
        // straight-line-nearest pick every tick -- see the field docs and TryAcquireKillTarget.
        var useNote = ctx.CurrentObjective?.Completion.Kind == CompletionKind.MonsterSlot;
        var avoidId = System.Environment.TickCount64 < _stuckUntil ? _stuckId : 0ul;
        // Allow FATE-spawned note mobs only for the note grind and only when the user opts in; a
        // by-name/dataId kill (FATE/leve) keeps its own fate gating (step.FateBound) and is unaffected.
        var allowFateNote = useNote && ctx.Config.AllowFateNoteKills;
        var haveTarget = ctx.Targeting.TryAcquireKillTarget(
            useNote, step.TargetName, step.TargetDataId, step.FateBound, allowFateNote,
            _travelLockId, avoidId,
            out var mobPos, out var mobDist, out var acquiredId, out var targetFateId,
            WantedNames(step));

        // Carry the commitment forward. TryAcquireKillTarget returned the locked mob while it stayed
        // valid, so acquiredId only changes when the old lock died/despawned (or was blacklisted) and
        // a new mob was picked; on a new lock, reset the stall tracker. Read acquiredId here -- BEFORE
        // the engage branch may retarget to an add -- so the lock tracks the relic/note mob, not the add.
        if (haveTarget)
        {
            if (acquiredId != _travelLockId)
            {
                _travelLockId = acquiredId;
                _lockBestDist = float.MaxValue;
                _lockProgressAt = System.Environment.TickCount64;
            }
            // A valid target loaded; cancel any outward search in progress. The Attack1 mark is
            // applied later, in the engage branch, on the PRIORITY target (an aggroed add before the
            // relic mob) -- marking here would fight that (this tick's target is the relic mob, which
            // the engage branch may retarget off), churning the /enemysign command every frame.
            ResetSearch();
        }
        else
        {
            _travelLockId = 0;
        }

        // Self-defense: if something is attacking us while we are still travelling or
        // approaching (not yet in melee range of our intended target), stop and let
        // RSR fight back instead of running while taking hits. The travel branches
        // below keep RSR disabled, which is why the character would otherwise neither
        // retaliate nor target the attacker. Ground first (RSR cannot act while
        // mounted or airborne), then target the threat and run RSR in Manual mode.
        // Travel resumes automatically once combat ends.
        if (Plugin.Condition[ConditionFlag.InCombat] && !(haveTarget && mobDist <= EngageRange))
        {
            // Combat pauses the approach; keep the stall clock fresh so a long add fight does not
            // false-declare the (still far) relic mob unreachable the moment combat ends.
            _lockProgressAt = System.Environment.TickCount64;
            if (!Combat.Mount.IsGrounded())
            {
                ctx.Navmesh.Stop();
                ctx.Rotation.Disable();
                var landNear = haveTarget ? mobPos : Plugin.ObjectTable.LocalPlayer?.Position ?? mobPos;
                Combat.Mount.LandAndDismount(ctx, landNear);
                return ExecutorStatus.InProgress;
            }
            // In combat with the relic mob out of melee range. If a separate ADD aggroed onto us
            // (something targeting us OTHER than the relic mob), fight it first so the backend hits
            // what is actually attacking us -- and CLOSE on it when it is out of melee range. A RANGED
            // add (caster / archer mob) will not walk to us, and under a rotation-only backend (BossMod Reborn
            // with the AI off) nothing else moves us, so a melee job could never reach it and it would
            // never die -- the "won't auto-move to the aggroed target, ranged enemy won't die" report.
            // The rotation stays on while closing so a ranged job still fires en route.
            var defenderId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
            var relicId = haveTarget ? (Plugin.TargetManager.Target?.GameObjectId ?? 0) : 0;
            if (ctx.Targeting.EngageAggressor(defenderId, relicId))
            {
                MarkTarget(ctx);
                ctx.Rotation.EnableManual();
                Combat.CombatAssist.Engage(ctx);
                var self = Plugin.ObjectTable.LocalPlayer?.Position ?? mobPos;
                var add = Plugin.TargetManager.Target;
                if (add != null && Vector3.Distance(self, add.Position) > EngageRange)
                {
                    Log("add aggroed at range; closing to reach it while fighting");
                    ctx.Navmesh.MoveCloseTo(add.Position, false, EngageRange - 1f);
                }
                else
                {
                    Log("under attack; marking + fighting the aggressor before resuming the relic mob");
                    ctx.Navmesh.Stop();
                }
                return ExecutorStatus.InProgress;
            }
            // No separate add is attacking us: the relic mob ITSELF (e.g. a RANGED one we just pulled)
            // is what keeps us in combat, from out of melee range. Do NOT stop here -- fall through to
            // the main engage below, which closes the gap to the relic mob and fights it. Previously
            // this branch stopped and a ranged relic mob at distance was never reached, so it never died.
        }

        if (haveTarget)
        {
            // Airborne approach: descend, land, and dismount BEFORE closing the final gap.
            // Decide on HORIZONTAL distance -- while airborne the 3D distance stays large
            // (altitude), which used to keep us in the "closing in" branch below, re-issuing a
            // fly-toward-mob move that overrode LandAndDismount's descent every tick, so the
            // character "tried to land but kept flying up". Sticky (_landing) until grounded so
            // a mob/altitude wobble across the boundary cannot flip back to flying.
            var self = Plugin.ObjectTable.LocalPlayer?.Position ?? mobPos;
            var horizontal = System.Numerics.Vector2.Distance(new(self.X, self.Z), new(mobPos.X, mobPos.Z));
            // Grounded -> not (or no longer) landing, so clear the landing watchdog clock.
            if (Combat.Mount.IsGrounded())
                _landingSince = 0;
            if (Plugin.Condition[ConditionFlag.InFlight] && (_landing || horizontal <= LandHorizontal))
            {
                _landing = true;
                Log($"mob near ({horizontal:F1}y horizontal); landing before engage");
                if (LandWithWatchdog(ctx, mobPos))
                    _landing = false; // watchdog gave up on this mob; a different one is acquired next tick
                return ExecutorStatus.InProgress;
            }
            _landing = false;

            // A valid mob exists, but "found" is not "reachable": EngageMonsterNoteTarget
            // returns the nearest match at ANY distance. RSR will not move us to a mob
            // that is out of range, so we must close the gap ourselves before enabling
            // the rotation. Otherwise RSR holds at caster range (or drifts) and never
            // casts. Only stop and fight once the mob is within EngageRange.
            // Hysteresis: once committed to a mob, keep fighting it while it stays within the
            // looser DisengageRange, so a mob wobbling around EngageRange does not flip between
            // this engage branch and the travel branch every tick and thrash RSR Off/On.
            var engageBand = _engaging ? DisengageRange : EngageRange;
            if (mobDist <= engageBand)
            {
                // The mob is reachable. Before casting, the character must be fully
                // grounded: RSR cannot act while mounted or airborne, which is the
                // "in range but not casting / not dismounting" case. If still mounted
                // or flying, land at a navmesh floor point and dismount, and hold off
                // on the rotation until grounded.
                if (!Combat.Mount.IsGrounded())
                {
                    Log($"mob in range ({mobDist:F1}y); grounding before engage");
                    LandWithWatchdog(ctx, mobPos);
                    return ExecutorStatus.InProgress;
                }

                _engaging = true;

                // FATE-bound note mob: level-sync to its FATE so the backend will actually engage it
                // (RSR drops a mob whose FateId != our synced fate, so Manual hard-targets it but never
                // casts). Gated on the target being a FATE mob AND us now standing in that ring; a normal
                // overworld note kill has targetFateId == 0 and is untouched. Idempotent /levelsync on,
                // throttled; leaving the ring for the next mob auto-unsyncs, so nothing lingers.
                if (targetFateId != 0)
                    SyncToFateTarget(targetFateId);

                // Stand and fight when in melee. If the mob has only drifted a little past melee
                // (still inside the band), chase it ON FOOT but KEEP RSR enabled -- do NOT disable
                // it for a small correction, which is exactly what produced the Off/On thrash.
                if (mobDist > EngageRange)
                {
                    Log($"mob drifted to {mobDist:F1}y; closing on foot while engaged");
                    ctx.Navmesh.MoveCloseTo(mobPos, false, EngageRange - 1f);
                }
                else if (!HasLineOfSight(self, mobPos))
                {
                    // In melee range, but terrain blocks the line of sight to the mob (we landed under a
                    // ledge / on a lower tier -- LandAndDismount descends to the floor BELOW the mob). The
                    // rotation backend then will NOT attack: RSR drops a no-line-of-sight target from its
                    // hostile list, so the mob is hard-targeted and Attack1-marked but nothing casts -- the
                    // "flew to the mob but it does not attack" report. Close right up to the mob's hitbox
                    // (~1y) to clear the block instead of freezing; the rotation is still ENABLED below (no
                    // Disable here, so no Off/On thrash). Once line of sight is clear the next tick stops
                    // and fights normally.
                    Log($"mob in range ({mobDist:F1}y) but no line of sight; closing in to clear it");
                    ctx.Navmesh.MoveCloseTo(mobPos, false, 1f);
                }
                else
                {
                    Log($"mob in range ({mobDist:F1}y); engaging");
                    ctx.Navmesh.Stop();
                }

                // The relic mob is the current hard target (set at the top of Update). Capture it
                // before any add retarget: it is what base-relic counting tracks, and it is the
                // object we exclude when scanning for adds (it is itself "targeting us" once pulled).
                var relicId = Plugin.TargetManager.Target?.GameObjectId ?? 0;

                // Add defense: RSR is in Manual and only acts on the target we set, so a non-targeted
                // enemy that aggroes onto us is otherwise ignored while we tunnel the (often neutral)
                // relic mob and take free hits. If something else is attacking us, retarget to it so
                // RSR fights back; once it dies the relic mob is re-acquired at the top of the next
                // Update. Base-relic counting stays latched to the relic mob below.
                var meId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
                if (ctx.Targeting.EngageAggressor(meId, relicId))
                    Log("add aggroed while engaging; marking + fighting it before the relic mob");

                // Attack1-mark the PRIORITY hard target (the add we just switched to, or the relic
                // mob if none aggroed) and drive RSR in MANUAL mode. Manual runs the rotation
                // continuously against the hard target and will INITIATE on a neutral, un-aggroed
                // relic mob; TargetOnly does not (it only layers a rotation onto combat we are
                // already in, so it "targets but never attacks" a neutral mob). The Attack1 sign
                // marks that same target as RSR's attack priority. Marking follows the priority, so
                // aggroed adds are engaged first, then the relic mob (re-acquired at the next Update).
                MarkTarget(ctx);

                // Re-arm RSR ONCE per pull. RSR may have auto-off'd after the previous kill
                // (AutoOffAfterCombat) while our mode is edge-triggered, so a resync forces the mode
                // to re-send. Doing it every out-of-combat tick re-sent the mode continuously, which
                // thrashed RSR so it never settled into its rotation and never attacked. Key the
                // resync on the (final) engaged target so a fresh mob re-arms but the one we are
                // already pulling does not.
                var engageId = Plugin.TargetManager.Target?.GameObjectId ?? 0;
                // Re-arm the backend ONCE per distinct engaged mob (keyed on the target id, so a mob we
                // are already pulling never re-sends -- that is what avoids the RSR mode-thrash). No longer
                // gated on !InCombat: a genuinely NEW mob/add engaged while still in combat (a clustered
                // spawn, or the backend self-cleared across a one-frame combat flicker) must still force a
                // fresh dispatch, or an edge-suppressed EnableManual would leave the rotation off.
                if (engageId != _manualArmedId)
                {
                    ctx.Rotation.ResyncNextDispatch();
                    _manualArmedId = engageId;
                }

                ctx.Rotation.EnableManual();
                Combat.CombatAssist.Engage(ctx); // chocobo + BossMod Reborn avoidance
                // Latch the mob we are ACTUALLY fighting, so its death is observed.
                //
                // This used to latch `relicId` once under an `_engagedId == 0` guard and never
                // re-point. When EngageAggressor (above) retargeted to an aggroed add, the latched
                // mob was the one we had STOPPED fighting: it never died, EngagedAlive() stayed
                // true, and the kill count froze at 0 while same-type mobs died continuously --
                // "not recognizing the kill count, stuck on the first set of enemies". In a
                // beastman stronghold the adds ARE our target type, so this fired constantly.
                //
                // Latch by NAME: we follow a retarget onto another mob of the SAME type, but never
                // credit a different species (which would corrupt the cap detection above). The
                // guard the old comment protected -- a target auto-switch on death skipping an
                // uncounted kill -- is preserved by ORDERING, not by the `== 0` check: the death
                // check at the top of Update runs before this, so within one Update the latched
                // mob's death is counted and cleared before a new mob is latched.
                if (!authoritative)
                {
                    var cur = Plugin.TargetManager.Target;
                    // Matched against the step's FULL authored name list, not the narrowed wanted
                    // set: a type retired while we were mid-fight is still a mob we are killing, and
                    // failing to latch it would lose the kill observation entirely.
                    if (cur != null && cur.GameObjectId != _engagedId && IsStepTargetName(step, cur.Name.TextValue))
                    {
                        _engagedId = cur.GameObjectId;
                        _engagedName = cur.Name.TextValue;
                    }
                    else if (_engagedId == 0)
                    {
                        _engagedId = relicId; // nothing named matched (FATE/leve kills have no name gate)
                        _engagedName = string.Empty;
                    }
                }
            }
            else
            {
                // Mob is genuinely far (beyond the hysteresis band): close the gap with RSR off so
                // it does not fight movement. Fly ONLY when already airborne (keep flying; the
                // sticky-land above descends when close). Do NOT pick the fly mode from the mob
                // distance: a mob sitting near a fly/walk threshold flipped the fly flag every tick,
                // so vnav never committed to a path and the character jittered / hovered in place. On
                // foot we close on the ground, mounting for a long haul for speed. mobPos has a real
                // height, so flight routes ok.
                _engaging = false;

                // Stall guard: if we cannot get materially closer to the committed mob for
                // StallTimeoutMs it is likely stranded on unreachable geometry. Give up the lock and
                // blacklist it briefly so the next acquire picks a different mob (or the outward search
                // repositions us), rather than tunnelling an unreachable target forever -- the failure
                // mode the commitment could otherwise introduce.
                if (mobDist < _lockBestDist - StallProgress)
                {
                    _lockBestDist = mobDist;
                    _lockProgressAt = System.Environment.TickCount64;
                }
                else if (_travelLockId != 0 && System.Environment.TickCount64 - _lockProgressAt > StallTimeoutMs)
                {
                    Log($"no approach progress on the locked mob ({mobDist:F0}y) for {StallTimeoutMs / 1000}s; trying another");
                    _stuckId = _travelLockId;
                    _stuckUntil = System.Environment.TickCount64 + StuckCooldownMs;
                    _travelLockId = 0;
                    ctx.Navmesh.Stop();
                    return ExecutorStatus.InProgress;
                }

                Log($"mob located at {mobDist:F1}y; closing in");
                ctx.Rotation.Disable();
                if (mobDist > FlyMinDistance)
                    Combat.Mount.EnsureMounted(ctx, mobDist);
                ctx.Navmesh.MoveCloseTo(mobPos, Plugin.Condition[ConditionFlag.InFlight], EngageRange - 1f);
            }
            return ExecutorStatus.InProgress;
        }

        // No valid target this tick: drop the engaged latch so a freshly acquired mob starts
        // from the tight EngageRange again rather than inheriting the looser band.
        _engaging = false;

        // No valid mob loaded: the zone teleport leaves us at the aetheryte, not the
        // spawn, so travel to the spawn area.
        //
        // The map flag and vnavmesh.FlagToPoint only resolve once this zone's navmesh
        // is built (FlagToPoint returns null while the mesh query is unavailable).
        // Wait for it rather than declaring "no location" -- this is why a freshly
        // placed flag appeared to be ignored right after teleporting.
        ctx.Rotation.Disable();
        if (!ctx.Navmesh.IsReady())
        {
            Log("zone navmesh still loading; waiting before travelling to the spawn");
            return ExecutorStatus.InProgress;
        }

        // Auto-flag: if nothing is flagged yet but we have an authored coordinate,
        // drop the in-game flag there (click-to-flag, as the in-game book does) so it
        // is visible to the player and FlagToPoint has something to resolve.
        //
        // Drop a FRESH flag on the current mob's authored coordinate whenever the objective
        // changes, so a flag is placed EVERY time and it is never a stale flag left by a previous
        // objective (the "doesn't place a flag every time / goes to a random place" symptom).
        // Drops a fresh flag per Trials-of-the-Braves entry.
        if (step.Position is { } spawn && _flaggedFor != spawn)
        {
            if (MapFlag.Set(spawn))
                _flaggedFor = spawn;
        }

        // Destination: the authored coordinate snapped to a LANDABLE floor. The authored
        // coordinate is deterministic per mob and the resolver is
        // landable-only (probes from a high Y, widens to the nearest shore), so it never lands on
        // an out-of-bounds under-water floor. The shared map flag is only the VISIBLE marker, NOT
        // the destination, so a stale or player-moved flag can no longer send us to a random place
        // -- which is what happened when FlagToPoint's result was used and the flag was only
        // (re)dropped when none already existed. The authored point is verified correct against the
        // in-game book coordinates; it is a single representative point, so the search rings below
        // still cover a mob that spawns offset from it.
        var dest = step.Position is { } p ? ctx.Navmesh.LandableFloorForMapPoint(p) : (Vector3?)null;

        if (dest is { } t)
        {
            var me = Plugin.ObjectTable.LocalPlayer?.Position ?? t;

            // An ACTIVE outward search owns movement. The search deliberately walks AWAY
            // from the anchor, so the d > 5f travel gate below must not recapture the
            // character the moment it leaves the anchor: that called ResetSearch, dragged
            // it back, re-dwelled 3s, and re-issued ring point 1 -- an endless anchor <->
            // first-ring oscillation (two alternating vnav destinations ~130ms apart in
            // the log, search stuck at "1/32"). Only a materially moved destination (a
            // new authored anchor for a different mob) cancels a running search; a target load
            // still ends it via ResetSearch in the haveTarget branch above.
            if (_searchAnchor is { } sa)
            {
                if (Vector3.Distance(sa, t) <= SearchRedirectSlack)
                {
                    SearchOutward(ctx, sa, me);
                    return ExecutorStatus.InProgress;
                }
                ResetSearch(); // the destination itself moved: follow it afresh
            }

            var d = Vector3.Distance(me, t);
            if (d > 5f)
            {
                // Still travelling to the anchor; not searching yet.
                Log($"no mob loaded; travelling to the spawn ({d:F0}y)");
                Combat.Mount.EnsureMounted(ctx, d);
                // Honor AllowFlight: the destination now resolves to a real floor point
                // (FloorForMapPoint), so flight no longer routes toward an out-of-bounds Y=0.
                ctx.Navmesh.MoveCloseTo(t, Flight.Allowed(ctx), 3f);
            }
            else
            {
                // Arrived at the authored anchor but nothing valid streamed in. The
                // authored coordinates are single representative points (verified against
                // the in-game book), and several mobs -- multi-level spots like U'Ghamaro
                // Mines, or roamers -- spawn away from that point. Rather than sit on it,
                // search expanding rings around the anchor; the per-tick target scan ends
                // the search the instant a valid mob loads (ResetSearch in the haveTarget
                // branch above).
                SearchOutward(ctx, t, me);
            }
        }
        else
        {
            Log("no mob loaded and no authored position; add a coordinate for this target");
        }
        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        // Halt movement too: on the 3/3 kill the controller calls Stop and advances to
        // the next step (often an AetheryteTeleport). A vnavmesh path still running would
        // keep the character moving, and movement cancels the teleport cast -- the
        // "finishing the kills breaks teleport" symptom. Rotation off + navmesh stopped
        // leaves a clean, stationary handoff.
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        Combat.CombatAssist.Disengage(ctx);
        // Remove the VISIBLE-marker flag we dropped on the mob (it is never our
        // destination -- see the Update flag comment). Leaving it set hands a stray flag
        // to the next step; the treasure-map loop in particular reads "a flag exists" as
        // "a treasure to run" and would chase this one forever. The next objective drops
        // its own fresh flag (Start resets _flaggedFor), so nothing is lost.
        MapFlag.Clear();
    }

    // --- Self-healing search around a possibly-off authored anchor ---------------
    // The authored spawn coordinates are single representative points, so a mob whose
    // real spawn is offset from that point never streams into the object table if we
    // just sit on it. When we arrive but no valid target has loaded, walk an expanding
    // ring of compass points around the anchor so the mob's real spawn enters load
    // range. TryAcquireKillTarget (top of Update) ends the search the moment a target
    // appears -- it calls ResetSearch via the haveTarget branch.
    private const long AnchorDwellMs = 3000; // let mobs stream in before wandering off
    private const long PointDwellMs = 1500;  // brief pause at each reached ring point
    // A destination that moves less than this is the SAME anchor (flag re-resolve
    // jitter); more means the player redirected (new flag), which cancels the search.
    private const float SearchRedirectSlack = 15f;
    private Vector3? _searchAnchor;
    private int _searchIdx;
    private long _searchDwellSince;
    private long _pointDwellSince;

    // Compass points on rings of growing radius (yalms). 8 points x 4 rings = 32 probes
    // out to 180y, which covers a stronghold's spread and multi-level offsets.
    private static readonly (float Dx, float Dz)[] SearchOffsets = BuildSearchOffsets();

    private static (float, float)[] BuildSearchOffsets()
    {
        var list = new System.Collections.Generic.List<(float, float)>();
        foreach (var radius in new[] { 45f, 90f, 135f, 180f })
            for (var k = 0; k < 8; k++)
            {
                var ang = k * System.MathF.PI / 4f;
                list.Add((radius * System.MathF.Cos(ang), radius * System.MathF.Sin(ang)));
            }
        return list.ToArray();
    }

    private void ResetSearch()
    {
        _searchAnchor = null;
        _searchIdx = 0;
        _searchDwellSince = 0;
        _pointDwellSince = 0;
    }

    private void SearchOutward(ExecutionContext ctx, Vector3 anchor, Vector3 me)
    {
        _searchAnchor ??= anchor;
        if (_searchDwellSince == 0)
            _searchDwellSince = System.Environment.TickCount64;

        // Dwell briefly at the anchor first: the mob may just need a moment to load.
        if (_searchIdx == 0 && System.Environment.TickCount64 - _searchDwellSince < AnchorDwellMs)
        {
            ctx.Navmesh.Stop();
            return;
        }

        if (_searchIdx >= SearchOffsets.Length)
        {
            // Exhausted the pattern: the coordinate is likely wrong for this mob. Hold so
            // the player can redirect with a map flag rather than wandering forever.
            ctx.Navmesh.Stop();
            Log("searched around the authored point but no valid target loaded; its coordinate may be off (place a map flag to redirect)");
            return;
        }

        var (dx, dz) = SearchOffsets[_searchIdx];
        var raw = new Vector3(_searchAnchor.Value.X + dx, 0f, _searchAnchor.Value.Z + dz);
        // Snap to a real LANDABLE floor (high-Y probe) so we never path to Y=0 or to an
        // out-of-bounds fallback point; skip unreachable ring points by advancing when the snap
        // yields nothing.
        var spot = ctx.Navmesh.LandableFloorForMapPoint(raw);
        if (spot is not { } s)
        {
            _searchIdx++;
            return;
        }
        if (Vector3.Distance(me, s) <= 5f)
        {
            // Reached this ring point: hold briefly so nearby mobs can stream in (the
            // per-tick scan at the top of Update ends the search the instant one does),
            // then advance to the next point.
            if (_pointDwellSince == 0)
            {
                _pointDwellSince = System.Environment.TickCount64;
                ctx.Navmesh.Stop();
                return;
            }
            if (System.Environment.TickCount64 - _pointDwellSince < PointDwellMs)
                return;
            _pointDwellSince = 0;
            _searchIdx++;
            return;
        }
        Combat.Mount.EnsureMounted(ctx, Vector3.Distance(me, s));
        ctx.Navmesh.MoveCloseTo(s, Flight.Allowed(ctx), 3f);
        Log($"no target at anchor; searching outward ({_searchIdx + 1}/{SearchOffsets.Length})");
    }

    // Drive land-and-dismount for the current mob, with a WATCHDOG: if the character cannot get grounded
    // within LandTimeoutMs -- the mob sits over a shaft/void/water with no floor beneath it, so the
    // descent never completes -- force a bare dismount and blacklist the mob so the next acquire picks a
    // different one, instead of looping here forever with the rotation disabled. Returns true when it gave
    // up (blacklisted); the caller returns InProgress either way.
    private bool LandWithWatchdog(ExecutionContext ctx, Vector3 mobPos)
    {
        var now = System.Environment.TickCount64;
        if (_landingSince == 0)
            _landingSince = now;

        if (now - _landingSince > LandTimeoutMs)
        {
            Log($"could not land near the mob within {LandTimeoutMs / 1000}s (no floor beneath it?); dismounting and trying another");
            Combat.Mount.EnsureDismounted();
            _stuckId = _travelLockId;
            _stuckUntil = now + StuckCooldownMs;
            _travelLockId = 0;
            _landingSince = 0;
            ctx.Navmesh.Stop();
            return true;
        }

        ctx.Rotation.Disable();
        Combat.Mount.LandAndDismount(ctx, mobPos);
        return false;
    }

    // Whether world geometry does NOT block the straight line from the player's eye to the mob's eye
    // (both +2y, matching RSR). RSR's Manual mode only casts on a hard target that passes this exact
    // line-of-sight raycast -- it drops a blocked target from its hostile list -- so a mob we landed
    // under a ledge from is targeted + marked yet never attacked. Same call RSR uses
    // (BGCollisionModule.RaycastMaterialFilter). Fails OPEN (returns true) if the module is unavailable,
    // preserving the pre-change "just stop at range" behaviour.
    private static bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var origin = new Vector3(from.X, from.Y + 2f, from.Z);
        var target = new Vector3(to.X, to.Y + 2f, to.Z);
        var delta = target - origin;
        var dist = delta.Length();
        if (dist < 0.1f)
            return true;
        try
        {
            // RaycastMaterialFilter returns true when the ray HITS BG geometry within maxDistance, i.e.
            // the eye-to-eye line is blocked; line of sight is the negation.
            return !BGCollisionModule.RaycastMaterialFilter(origin, delta / dist, out _, dist);
        }
        catch
        {
            return true;
        }
    }

    private long _log;
    private void Log(string message)
    {
        if (System.Environment.TickCount64 - _log < 10000)
            return;
        _log = System.Environment.TickCount64;
        Diagnostics.DebugLog.Info($"KillTarget: {message}");
    }

    // Places the Attack1 head sign on the current target (the priority mob the engage branch
    // just selected), once per distinct target, via the game's /enemysign command.
    // MarkingController exposes no API for head signs, so the chat command is used.
    // Local kill counter (see Update). _engagedId is the GameObjectId of the mob currently
    // being fought; when it dies or despawns the kill is counted.
    private ulong _engagedId;
    private int _localKills;

    // ---- Quest-counter (beastmen hunt) state; reset in Start ----
    // The last CUMULATIVE beastman total we logged (-1 = nothing logged yet), so each change is
    // recorded exactly once rather than every tick.
    private int _qLoggedTotal = -1;

    // Completion for a beastmen-hunt KillTarget step, read from the relic quest's own work
    // counters. Returns null when the counters cannot be read at all (quest not accepted, wrong
    // sequence, id unresolved), which hands the step back to the local tally.
    //
    // THE AUTHORITY IS THE CUMULATIVE TOTAL of the three beastman counters (0..24), compared
    // ABSOLUTELY to this step's cumulative target (8 / 16 / 24). This needs NO mob->nibble
    // mapping (only White Mage's is known, and it is not positional), and it is race-free: the
    // sum is conserved, so it does not matter which type a kill credits, whether an AoE cleave
    // credited a different type, or whether the credit lands a frame after the mob's HP hit 0.
    //
    // This REPLACES a per-nibble "we killed our mob and nothing credited, so it must be capped"
    // detector (v1.4.126.0), which was timing-fragile: a credit landing one frame after the death
    // frame was silently absorbed and the type was declared capped after only ~2 kills, ending the
    // hunt early and stopping the engine (with the rotation left off -- the "BMR no longer
    // toggles" report). The cumulative total has no such frame dependency.
    private bool? QuestCounterComplete(StepData step, ExecutionContext ctx)
    {
        if (step.QuestCounterTarget <= 0)
            return null; // misconfigured (flag set without a target); fall back to the local tally

        var qid = BaseRelic.BeastmanCounters.QuestIdFor(ctx.CurrentObjective);
        if (qid == 0)
            return null;

        // The whole quest is finished (QuestSequence reads 0 for a completed quest, so without
        // this an already-done hunt would fall through and farm pointless kills).
        if (GameState.IsQuestComplete(qid))
        {
            Log("beastmen: the relic quest is already complete; nothing to hunt");
            return true;
        }

        var seq = GameState.QuestSequence(qid);
        var huntSeq = BaseRelic.BeastmanCounters.HuntSequence;
        // The quest has moved PAST the hunt: the 24th kill already credited and the part is done.
        // Cannot false-complete -- only the game advances the sequence.
        if (seq > huntSeq)
        {
            Log($"beastmen: quest sequence {seq} is past the hunt ({huntSeq}); part complete");
            return true;
        }
        // Not on the hunt step (or not accepted): the nibbles are meaningless here -- Sapphire
        // shows Variables[1] is reused as dungeon scratch flags at other sequences -- so refuse to
        // read them and let the local tally carry the step.
        if (seq != huntSeq)
            return null;

        var vars = GameState.QuestWorkVariables(qid);
        var total = BaseRelic.BeastmanCounters.Total(vars);
        if (total < 0)
            return null; // unreadable; fall back
        // Publish it for the per-type credit watch (see ResolveCreditWatch).
        _qTotal = total;

        // Log every change in the total exactly once -- the running record of the layout for the
        // nine jobs whose mob->nibble assignment is still inference. Deliberately NOT through the
        // 10s-throttled Log() helper, which would drop most of it.
        if (total != _qLoggedTotal)
        {
            _qLoggedTotal = total;
            var hunting = step.TargetNames.Count > 0
                ? string.Join(", ", _wanted.Count > 0 ? _wanted : step.TargetNames)
                : step.TargetName ?? string.Empty;
            Diagnostics.DebugLog.Info(
                $"beastmen: quest {qid} (masked {qid & 0xFFFF}) seq {seq}, step target {step.QuestCounterTarget} " +
                $"still hunting '{hunting}'. {BaseRelic.BeastmanCounters.Dump(vars)}");
        }

        return total >= step.QuestCounterTarget;
    }

    // ---- Multi-name grind helpers (see the field docs above) ----

    // The enemy names this step will accept RIGHT NOW: the authored list minus the types already
    // finished. Null for an ordinary single-target step, which leaves the acquire exactly as it was.
    private System.Collections.Generic.IReadOnlyCollection<string>? WantedNames(StepData step)
    {
        if (step.TargetNames.Count == 0)
            return null;

        _wanted.Clear();
        foreach (var n in step.TargetNames)
        {
            if (_retired.Contains(n))
                continue;
            if (_killsByName.GetValueOrDefault(n) >= BaseRelic.BeastmanCounters.PerMobTarget)
                continue;
            _wanted.Add(n);
        }

        if (_wanted.Count == 0)
        {
            // Every type looks finished, yet the step has not completed -- so at least one
            // retirement was wrong (a re-plan zeroed the tallies mid-hunt, or a credit was missed).
            // Re-offer everything rather than standing in the middle of the stronghold with nothing
            // eligible: a wrong retirement then costs a few kills instead of hanging the run.
            Diagnostics.DebugLog.Warn("Kill step: every enemy type looked finished but the step is not " +
                                      "complete; re-offering all of them.");
            _retired.Clear();
            _killsByName.Clear();
            _noCreditByName.Clear();
            _wanted.AddRange(step.TargetNames);
        }
        return _wanted;
    }

    // Is this the name of something this step kills at all (any authored type, retired or not)?
    private static bool IsStepTargetName(StepData step, string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (step.TargetNames.Count > 0)
        {
            foreach (var n in step.TargetNames)
                if (string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        return !string.IsNullOrEmpty(step.TargetName)
               && string.Equals(name, step.TargetName, System.StringComparison.OrdinalIgnoreCase);
    }

    // Record a kill against its type and open a credit watch on it. The watch's baseline is the
    // PREVIOUS tick's counter total, which is guaranteed to pre-date this kill.
    private void NoteKill(string name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        _killsByName[name] = _killsByName.GetValueOrDefault(name) + 1;
        if (_qTotalLastTick < 0)
            return; // no readable counter (not the beastmen hunt) -> nothing to watch
        _pendingName = name;
        _pendingBaseTotal = _qTotalLastTick;
        _pendingAt = System.Environment.TickCount64;
    }

    // Settle the outstanding credit watch: the counter rising proves the type still counts; the
    // counter staying flat for the whole grace window is one strike against it, and
    // NoCreditRetireAfter strikes retire it.
    private void ResolveCreditWatch()
    {
        if (_pendingName.Length == 0 || _qTotal < 0)
            return;

        if (_qTotal > _pendingBaseTotal)
        {
            _noCreditByName.Remove(_pendingName); // it credited; any earlier strike was noise
            _pendingName = string.Empty;
            return;
        }

        if (System.Environment.TickCount64 - _pendingAt < CreditGraceMs)
            return; // still inside the window where a late credit can land

        var name = _pendingName;
        _pendingName = string.Empty;
        var strikes = _noCreditByName.GetValueOrDefault(name) + 1;
        _noCreditByName[name] = strikes;
        if (strikes < NoCreditRetireAfter)
            return;

        _retired.Add(name);
        Diagnostics.DebugLog.Info($"beastmen: '{name}' no longer credits after {strikes} kills " +
                                  $"(its {BaseRelic.BeastmanCounters.PerMobTarget} are done); hunting the other types only.");
    }

    // True while the last-engaged base-relic mob is still alive. A despawned (gone) or
    // 0-HP object reads as dead, which is what advances the local kill counter. Iterates
    // the object table (rather than SearchById) to avoid a version-sensitive accessor.
    private bool EngagedAlive()
    {
        if (_engagedId == 0)
            return false;
        foreach (var o in Plugin.ObjectTable)
            if (o.GameObjectId == _engagedId)
                return o is IBattleChara { CurrentHp: > 0 };
        return false; // not in the table -> despawned -> counts as dead
    }

    // Deduplicated by GameObjectId, not object-table address: the table recycles
    // addresses, so a new mob spawning at a recycled address would never be marked.
    private ulong _markedId;
    private void MarkTarget(ExecutionContext ctx)
    {
        var target = Plugin.TargetManager.Target;
        if (target == null || target.GameObjectId == _markedId)
            return;
        _markedId = target.GameObjectId;
        // Place the Attack 1 head-sign on the current hard target so RSR prioritises attacking it.
        // Sent through the GAME chat box (ECommons.Chat), NOT ctx.Commands.Run: the latter routes to
        // Dalamud's ICommandManager.ProcessCommand, which only dispatches Dalamud-REGISTERED commands
        // and silently drops a native game command like /enemysign (returns false, does nothing) --
        // so no marker was ever placed and RSR never engaged the neutral relic mob. (Same trap fixed
        // for /beckon in LeveRunner; /marking, tried earlier, is not even a real command.) attack1 =
        // the Attack 1 sign; <t> is the current hard target, resolved by the game.
        try { ECommons.Automation.Chat.ExecuteCommand("/enemysign attack1 <t>"); }
        catch (System.Exception ex) { Diagnostics.DebugLog.Warn($"KillTarget: /enemysign failed: {ex.Message}"); }
    }

    // Level-sync to the FATE a just-engaged note mob belongs to, so the backend will actually attack it
    // (RSR drops a FATE mob whose FateId != the player's synced fate). Only fires once we are physically
    // standing in that ring (GetCurrentFateId), so it is a no-op while still approaching; idempotent
    // "/levelsync on" (a bare toggle can flip sync back off on a stale read), throttled, and stops once
    // synced. Sent through the game chat box (ECommons.Chat) because Dalamud's ProcessCommand drops the
    // native /levelsync -- the same trap as /enemysign above. Mirrors ParticipateFateExecutor.TrySyncToFate.
    private void SyncToFateTarget(ushort fateId)
    {
        if (fateId == 0 || GameState.CurrentFateId() != fateId)
            return;
        if (GameState.IsSyncedToCurrentFate())
            return;
        if (System.Environment.TickCount64 - _fateSyncThrottle <= 3000)
            return;
        _fateSyncThrottle = System.Environment.TickCount64;
        try { ECommons.Automation.Chat.ExecuteCommand("/levelsync on"); }
        catch (System.Exception ex) { Diagnostics.DebugLog.Warn($"KillTarget: /levelsync failed: {ex.Message}"); }
    }

    // Reads the authoritative counter for the current objective's completion
    // condition. For a monster slot this is RelicNote.GetMonsterProgress (kills,
    // 0..3); for Atma it is the inventory count of the Atma item.
    private static int ReadObjectiveCounter(ExecutionContext ctx)
    {
        var c = ctx.CurrentObjective?.Completion;
        if (c == null)
            return 0;

        return c.Kind switch
        {
            CompletionKind.MonsterSlot => GameState.MonsterProgress(c.Slot),
            CompletionKind.ItemCount => GameState.InventoryCount(c.ItemId),
            _ => 0,
        };
    }
}
