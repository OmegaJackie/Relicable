using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps.Combat;

// The global aggro backstop, ticked by RelicController for EVERY step rather than living inside
// one executor.
//
// CombatAssist.DefendSelf already exists and is correct, but it only defends the loops that
// remembered to call it. The gap it cannot close is structural: each executor has to opt in, in
// every one of its branches, and each new branch is a new place to forget. The travel legs are
// exactly the branches that got missed -- the aetheryte teleport's in-combat wait stands still for
// twenty seconds specifically BECAUSE something is hitting us (Teleport is refused in combat) and
// never once fights back; the flag walk is the longest open-world leg in the run and reads no
// combat state at all. That is the reported "aggroed enemies aren't getting attacked", and it kept
// coming back because the fix was always per-branch.
//
// So this watches from OUTSIDE the executors and needs no cooperation from them.
//
// IT IS A BACKSTOP, NOT A COMBAT LOOP. It deliberately does nothing while anything else is coping,
// which is what keeps it from fighting the executors that DO handle their own combat:
//
//   * It fires only on an UNATTENDED aggro -- an enemy engaged with us that is not our hard target.
//     The instant KillTarget/DefendSelf/the FATE loop targets the attacker, this goes quiet, so on
//     every path that already works it never runs at all.
//   * It waits out a grace window first, shorter when we are rooted (standing still with something
//     on us is already the failure) and longer while we are still moving (a mob that aggroes as we
//     ride past and gets left behind must not be worth stopping for).
//   * It stands down inside a FATE. FATE combat is the one place where being surrounded by enemies
//     engaged with us is NORMAL and where the executor owns targeting, level sync and the low-health
//     bail-out (FateSyncGuard) -- overriding any of that would break credit, not save us. Travelling
//     TO a FATE is not in a FATE, and IS covered.
//   * Separately from that, it never takes a FATE mob we are not synced to at all (the finder
//     excludes them). Ring geometry alone is not enough, because FATE mobs chase well outside the
//     ring: one that follows us out would pass the "not in a FATE" test while still being a mob the
//     backend refuses to cast at unsynced -- so engaging it would buy a ninety-second staring
//     contest instead of a fight.
internal sealed class AggroWatchdog
{
    // How far out an aggroed enemy still counts. Beyond roughly this a mob that pulled has already
    // lost us and will reset on its own; chasing it would be the watchdog creating work.
    private const float Radius = 45f;

    // While we are still MOVING, wait this multiple of the configured window before intervening.
    // Riding past a camp aggroes things constantly and almost all of it drops on its own, so the
    // moving case has to be clearly persistent -- something that has stayed on us across a long
    // stretch of travel is not going to be outrun.
    private const int MovingWindowMultiplier = 3;

    // Distance the character must cover for the tick to count as movement. Well above navmesh
    // jitter and the shuffle a rotation's positioning does, so "standing still" means standing
    // still and not "finished the last step of a path".
    private const float MovedEpsilon = 2.0f;

    // Never own the tick for longer than this. Whatever we are fighting is not dying (an immune
    // object, a backend that is off, a mob we cannot reach), and holding the run hostage is worse
    // than handing it back -- the objective's own timeout is then free to fail it honestly.
    private const long MaxHoldMs = 90_000;

    // ...and after giving up, stay out of the way this long so the run gets a clean attempt at
    // whatever it was doing instead of re-arming on the same unkillable thing every tick.
    private const long GiveUpCooldownMs = 60_000;

    // Throttles for the two user-facing lines, so a long fight reports once rather than per frame.
    private const long PingLogMs = 15_000;
    private const long StalledLogMs = 20_000;

    // Movement sampling (anchor + timestamp, as WatchTravelStall and FateSyncGuard.Rate do).
    private Vector3 _anchor;
    private long _movedAt;

    // When the current unattended aggro was first seen. 0 = nothing pending.
    private long _unattendedSince;

    // The watchdog has taken the run over and is fighting. Kept SEPARATE from _armedId below: the
    // first tick of an intervention may spend itself landing, before anything is targeted, and a
    // single latch would have re-run the decision (and re-logged it) on every frame of the descent.
    private bool _engaged;
    private long _engagedAt;

    // Whether we have already issued the halt for this fight, and whether we are inside the engage
    // band (which is entered on the tight threshold and left on the loose one). See Defend.
    private bool _stopped;
    private bool _inBand;

    // The last tick this actually ran. The controller returns before the watchdog on several paths
    // (death recovery every tick while dead, both CBT guards), and none of them can call Disarm --
    // so without this a fight in progress when the run died would still be "in progress" minutes
    // later, and its age would blow straight past MaxHoldMs the moment it resumed, arming the
    // give-up cooldown and disabling the backstop for a minute on a fight that never happened.
    private long _lastTicked;

    // A gap larger than this means we were not being ticked at all, so no clock we hold measured
    // anything real. Comfortably longer than a slow frame, far shorter than any of our windows.
    private const long MissedTickResetMs = 2000;

    // The mob we last armed the backend for. Same latch DefendSelf takes, and for the same reason:
    // the backend mode is re-sent only when the target changes, never per tick, or RSR never settles
    // into its rotation.
    private ulong _armedId;
    private long _cooldownUntil;
    private long _lastPingLog;

    // "Targeted, in combat, standing still, and it is not losing health" -- the watchdog cannot fix
    // that (it is a line-of-sight, level-sync or backend-configuration problem, and re-sending a
    // mode would only mask it), so it is reported rather than acted on.
    private ulong _stalledTargetId;
    private uint _stalledHp;
    private long _stalledSince;
    private long _lastStalledLog;
    private string _status = string.Empty;

    // Non-empty while the watchdog has something to say: shown in the main window so an
    // intervention (or a fight that is going nowhere) is visible rather than silent.
    public string Status => _status;

    public void Reset()
    {
        _anchor = default;
        _movedAt = 0;
        _unattendedSince = 0;
        _engaged = false;
        _engagedAt = 0;
        _stopped = false;
        _inBand = false;
        _armedId = 0;
        _cooldownUntil = 0;
        _lastPingLog = 0;
        ResetStalled();
        _status = string.Empty;
    }

    // Called every tick while a step is running, BEFORE the step runs. Returns true when the
    // watchdog has taken the tick over (it is fighting), in which case the caller must not run the
    // executor -- exactly the contract DefendSelf has with its callers.
    //
    // inFateStep: the active step is a FATE step. Combined with actually being inside a FATE ring,
    // this is the stand-down condition; see the class comment.
    public bool Tick(ExecutionContext ctx, bool inFateStep)
    {
        var now = Environment.TickCount64;

        // We were not ticked for a while -- the run died and was recovering, or CBT owned the tick,
        // or the option was off. Nothing we are holding measured real time, so start clean rather
        // than resume a fight whose age is now nonsense.
        if (_lastTicked != 0 && now - _lastTicked > MissedTickResetMs)
            Reset();
        _lastTicked = now;

        if (!ctx.Config.AggroWatchdog)
        {
            if (_engaged || _status.Length > 0)
                Reset();
            return false;
        }

        // Not free to act (zoning, cutscene, a dialogue), dead, or in a duty where AutoDuty owns
        // combat: decide nothing and hold no state, so nothing is carried across the transition.
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || me.CurrentHp == 0 || !Interaction.EventConditions.Free || BoundByDuty())
        {
            Disarm();
            return false;
        }

        // Sampled before the FATE stand-down, not after, so the anchor keeps up while a FATE is
        // being fought. Otherwise every FATE would be followed by a stale "we have not moved in
        // four minutes" reading and an over-eager first intervention on the way out of the ring.
        TrackMovement(now, me.Position);

        // Inside a FATE: hands off entirely. Everything in the ring is engaged with us by design,
        // and the FATE executor owns the target, the level sync and the low-health bail-out.
        if (inFateStep && GameState.CurrentFateId() != 0)
        {
            Disarm();
            return false;
        }

        var meId = me.GameObjectId;
        var allyId = Companion.CompanionId();
        // Stay committed to the mob we are already fighting while it is still on us: two attackers
        // circling swap which is nearest every few frames, and a per-tick nearest pick would
        // re-target and re-dispatch the backend on each swap -- the mode thrash that stops the
        // rotation ever settling. It falls back to the nearest the moment ours dies or drops us.
        var aggressor = ctx.Targeting.FindNearestAggroedEnemy(meId, allyId, Radius,
            GameState.SyncedFateId(), _engaged ? _armedId : 0);

        if (aggressor == null)
        {
            // Nothing engaged with us. If we were fighting, the fight is over -- hand the run back.
            if (_engaged)
                DebugLog.Info("Aggro watchdog: nothing is engaged with us any more; handing the step back.");
            Disarm();
            ResetStalled();
            return false;
        }

        // Already fighting: stay in charge until the aggro is gone (the branch above), so closing the
        // last few yalms onto a ranged mob does not read as "we are moving again" and drop us out
        // mid-fight. Bounded by MaxHoldMs.
        if (_engaged)
        {
            if (now - _engagedAt > MaxHoldMs)
            {
                DebugLog.Warn($"Aggro watchdog: still fighting after {MaxHoldMs / 1000}s and getting nowhere; " +
                    "handing the step back so the run can fail or re-plan on its own terms. " +
                    "Check that your combat backend is running and can reach the target.");
                _cooldownUntil = now + GiveUpCooldownMs;
                Disarm();
                return false;
            }
            return Defend(ctx, aggressor, now);
        }

        var target = Plugin.TargetManager.Target;
        var attended = target != null
                       && target.GameObjectId != 0
                       && Targeting.IsAggroedOnUs(target, meId, allyId);

        if (attended)
        {
            // Someone -- the kill loop, the leve fight, DefendSelf -- is on it. Do not intervene;
            // just watch that the fight is actually progressing and say so if it is not.
            _unattendedSince = 0;
            WatchStalledFight(now, target!);
            return false;
        }

        ResetStalled();

        // Cooling down after giving up on something we could not kill: re-measure from scratch when
        // it expires rather than re-firing the instant it does on a clock that kept running.
        if (now < _cooldownUntil)
        {
            _unattendedSince = 0;
            return false;
        }

        // An enemy is engaged with us and we are pointed at something else (or at nothing). Start
        // the grace clock; the window is short when rooted and long while we are still travelling.
        if (_unattendedSince == 0)
            _unattendedSince = now;

        var stillMs = now - _movedAt;
        var window = Math.Max(1, ctx.Config.AggroWatchdogSeconds) * 1000L;
        var standingStill = stillMs >= window;
        var required = standingStill ? window : window * MovingWindowMultiplier;
        if (now - _unattendedSince < required)
            return false;

        DebugLog.Warn($"Aggro watchdog: '{aggressor.Name.TextValue}' has been attacking us for " +
            $"{(now - _unattendedSince) / 1000}s and nothing engaged it" +
            (standingStill ? $" while we stood still for {stillMs / 1000}s" : " while travelling") +
            ". Stopping to fight back.");
        _engaged = true;
        _engagedAt = now;
        return Defend(ctx, aggressor, now);
    }

    // Ground, target, mark, and run the backend on the aggressor -- the same sequence DefendSelf
    // performs, plus closing the gap: an add that plinks us from range never walks into our reach,
    // and under a rotation-only backend nothing else moves us, so a melee job would stand there
    // forever "engaged" with something it cannot hit.
    private bool Defend(ExecutionContext ctx, IGameObject aggressor, long now)
    {
        var here = Plugin.ObjectTable.LocalPlayer?.Position ?? aggressor.Position;

        // Nothing can be cast while mounted or airborne, so get down first.
        if (!Mount.IsGrounded())
        {
            ctx.Navmesh.Stop();
            Mount.LandAndDismount(ctx, here);
            _status = "Something aggroed and nothing engaged it — landing to fight back.";
            return true;
        }

        ctx.Targeting.SetTarget(aggressor);
        if (aggressor.GameObjectId != _armedId)
        {
            _armedId = aggressor.GameObjectId;
            // The backend may have auto-off'd after the last fight while our own dispatch is
            // edge-triggered; force this one to actually re-send, for the NEW target only.
            ctx.Rotation.ResyncNextDispatch();
            // Attack1 marks it as the backend's priority target. Through the game chat box
            // (ECommons.Chat), never ctx.Commands, which silently drops native game commands.
            try { ECommons.Automation.Chat.ExecuteCommand("/enemysign attack1 <t>"); }
            catch (Exception ex) { DebugLog.Warn($"Aggro watchdog: /enemysign failed: {ex.Message}"); }
        }

        // Close the gap when it is out of reach. An add that plinks us from range never walks into
        // our reach, and under a rotation-only backend nothing else moves us, so a melee job would
        // stand there "engaged" with something it cannot hit. The band is the shared role-aware one
        // (ranged jobs fight from where they stand), and the outer Disengage threshold is what stops
        // a mob drifting around the edge from flipping us between chase and hold every tick.
        // TWO bands, not one, exactly as the kill and FATE loops do. Approaching, the threshold is
        // the TIGHT Engage() band; once inside it, the LOOSE Disengage() band. Using the loose one
        // for both halts the walk-in a full hysteresis width (4y) short of reach -- a melee job
        // stops around 7y from an archer that is still shooting it, "engaged" with something it can
        // never hit, until the give-up timer expires. The latch is what lets the two differ.
        var dist = Vector3.Distance(here, aggressor.Position);
        if (dist <= EngageBand.Engage(aggressor))
            _inBand = true;
        else if (dist > EngageBand.Disengage(aggressor))
            _inBand = false;

        if (!_inBand)
        {
            _stopped = false;
            ctx.Navmesh.MoveCloseTo(aggressor.Position, false, EngageBand.Stop(aggressor));
        }
        else if (!_stopped)
        {
            // Stop ONCE. Navmesh.Stop() is not deduplicated -- it drops vnav's cached destination on
            // every call -- so a per-tick stop is the documented "tiny steps" stutter.
            _stopped = true;
            ctx.Navmesh.Stop();
        }

        ctx.Rotation.EnableManual();
        CombatAssist.Engage(ctx);

        if (now - _lastPingLog >= PingLogMs)
        {
            _lastPingLog = now;
            DebugLog.Info($"Aggro watchdog: fighting '{aggressor.Name.TextValue}' at {dist:0}y " +
                $"({(now - _engagedAt) / 1000}s so far).");
        }
        _status = $"Fighting back: {aggressor.Name.TextValue} aggroed and nothing engaged it.";
        return true;
    }

    // Position anchor. Re-anchoring only once the character has actually covered MovedEpsilon means
    // _movedAt is the last time we genuinely went somewhere, not the last frame the position jittered.
    private void TrackMovement(long now, Vector3 pos)
    {
        if (_movedAt == 0 || Vector3.DistanceSquared(_anchor, pos) >= MovedEpsilon * MovedEpsilon)
        {
            _anchor = pos;
            _movedAt = now;
        }
    }

    // The fight IS attended but going nowhere: rooted, in combat, holding the mob that is on us, and
    // its health has not moved for two full windows. That is a line-of-sight block, a missing level
    // sync, or a combat backend that is not running -- none of which the watchdog can fix by
    // re-sending a mode, and all of which it would hide by trying. So it reports and leaves it alone.
    private void WatchStalledFight(long now, IGameObject target)
    {
        if (target is not IBattleChara chara)
        {
            ResetStalled();
            return;
        }

        if (chara.GameObjectId != _stalledTargetId || chara.CurrentHp != _stalledHp)
        {
            // A different mob, or its health moved at all: this fight is doing something. Any change
            // counts, not just a drop -- a mob regenerating is still evidence the world is ticking,
            // and the point of this check is to catch the case where NOTHING happens.
            _stalledTargetId = chara.GameObjectId;
            _stalledHp = chara.CurrentHp;
            _stalledSince = now;
            _status = string.Empty;
            return;
        }

        // Moving, out of combat, or not stalled long enough yet. Clear the line as well as bailing:
        // the status describes THIS tick's judgement, and a warning left standing after the fight
        // moved on reads as a live problem in the main window when there is none.
        if (now - _movedAt < 5_000 || !Plugin.Condition[ConditionFlag.InCombat]
            || now - _stalledSince < 15_000)
        {
            _status = string.Empty;
            return;
        }

        _status = $"{chara.Name.TextValue} is attacking and is not losing health — check your combat backend.";
        if (now - _lastStalledLog < StalledLogMs)
            return;
        _lastStalledLog = now;
        DebugLog.Warn($"Aggro watchdog: '{chara.Name.TextValue}' is engaged with us and targeted, but its " +
            $"health has not moved in {(now - _stalledSince) / 1000}s while we stood still. The backend is " +
            "not landing anything -- check that your combat plugin is loaded and enabled, that the target " +
            "is in line of sight, and (in a FATE) that you are level-synced.");
    }

    // Clears the "this fight is going nowhere" line with the tracker, so it can never outlive the
    // observation that produced it.
    private void ResetStalled()
    {
        _stalledTargetId = 0;
        _stalledHp = 0;
        _stalledSince = 0;
        if (!_engaged)
            _status = string.Empty;
    }

    // Give the tick back without clearing the cooldown (which must outlive a hand-back) or the
    // movement anchor (which is cheap to keep warm and wrong to restart mid-travel).
    private void Disarm()
    {
        _engaged = false;
        _engagedAt = 0;
        _stopped = false;
        _inBand = false;
        _armedId = 0;
        _unattendedSince = 0;
        _status = string.Empty;
    }

    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];
}
