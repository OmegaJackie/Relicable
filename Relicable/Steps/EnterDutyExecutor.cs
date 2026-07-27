using System;
using Dalamud.Game.ClientState.Conditions;
using Relicable.Model;

namespace Relicable.Steps;

// Delegates duty objectives to AutoDuty. Three modes, keyed off the objective's
// completion kind:
//
//   * Animus dungeons / guildhests (default): queue the step's TerritoryType once and
//     wait for AutoDuty to report stopped (its own Duty Support / Trust run).
//   * Nexus Light farm (LightGauge): queue the configured farm duty (default Bowl of
//     Embers (Extreme)) UNSYNCED via DutyMode=Trial + Unsynced, looping; auto-stop the
//     instant Light reaches 2000.
//   * Zeta Mahatma farm (MahatmaGauge): queue the configured farm duty (default Aurum
//     Vale) UNSYNCED with the DutyMode auto-resolved from the content type (dungeon ->
//     Regular, trial -> Trial), looping; auto-stop the instant a Mahatma awakens
//     (completed count increments) so the next Mahatma can be attached at Remon before
//     farming resumes.
//
// Farm duty / loops / unsynced come from Configuration so the duty is selectable; the
// farm objective is just a bare EnterDuty step with the gauge completion.
public sealed class EnterDutyExecutor : ITaskExecutor
{
    // Grace after hand-off before "AutoDuty stopped" is treated as "finished".
    private const long StartupGraceMs = 5000;
    // Grace after AutoDuty stops for a book dungeon's slot credit to land. The credit
    // is applied on/just after the duty completes, and AutoDuty can leave the instance
    // faster than the RelicNote update propagates; without this the slot reads
    // uncredited the instant AutoDuty stops and the clear is wrongly failed. Kept generous
    // because on a fast unsynced clear the propagation + zone-out can run to ~20s.
    private const long CreditGraceMs = 30000;
    // Minimum settle held once a duty objective is first seen complete, before the step is
    // finalized. The objective is detected on the final-boss kill (still inside the instance),
    // so this keeps the run in the duty a beat longer to be sure the game applies clear credit
    // before the controller advances -- guarding against AutoDuty zoning out the instant the
    // boss dies. Distinct from CreditGraceMs (which is the outer wait for a slot that has not
    // credited at all); this fires on the normal, credited path.
    private const long ExitDelayMs = 2000;
    // If a forced leave was issued but we are still inside the instance this long later, the game did
    // not honor it (a boss-death / duty-complete transition can swallow the request). Re-issue it.
    private const long LeaveRetryMs = 4000;
    // Ifrit EX nail bail cap (Light farm). After this many consecutive Ifrit runs abandoned on the
    // nail phase WITHOUT a credited clear in between, stop bailing and let AutoDuty clear through the
    // nails once, so a character that can never burst Ifrit still makes progress instead of churning
    // re-entries up to the loop cap. A credited clear resets the count (see _nailBails).
    private const int MaxNailBails = 5;

    public StepType Handles => StepType.EnterDuty;

    private enum FarmKind { None, Light, Mahatma }

    private bool _handedOff;
    private bool _started;
    private bool _wasBound; // the character was inside the duty at some point this run
    private FarmKind _farm;
    private long _startTicks;
    private long _creditGraceStart;
    private long _exitDelayStart;
    private bool _left; // the forced Leave-Duty has been issued for the credited dungeon (fire once)
    // Hold-for-credit state (DungeonSlot). Kept latched so a note read that flips underneath us, or a
    // leave the game swallows, cannot strand the character: we leave on EITHER credit-settled OR a
    // bounded hold since the boss cleared, and re-issue a leave the game did not honor.
    private bool _credited;      // the slot was SEEN complete at least once (rising edge; never un-set)
    private long _creditSeenAt;  // when the credit was first observed (settle window start)
    private long _clearedAt;     // when AutoDuty first went idle-stopped while we held (boss cleared)
    private long _leftAt;        // when the forced leave was last issued (retry if still bound after)
    private long _holdLogAt;     // throttle for the hold-state heartbeat log
    private int _baselineLight;
    private int _baselineCompleted;
    private int _baselinePoints;
    // A nail bail-out leave was issued this hand-off (Light/Mahatma Ifrit farm). Only used to word a
    // subsequent "no progress" failure accurately (the run was abandoned on the nail phase, not a
    // combat/rotation failure to clear). Reset in Start.
    private bool _leftForNails;
    // Consecutive Ifrit runs abandoned on nails with no credited clear between them (Light farm bound;
    // see MaxNailBails). Reset on a credited-clear edge, and in Start.
    private int _nailBails;
    // Last-seen Nexus Light, so a rise (a clear crediting Light) can reset _nailBails as a per-clear
    // edge without disturbing _baselineLight (which the end-of-run progress check needs). Reset in Start.
    private int _streakLight;
    // Throttle for the one-shot "clearing through the nails" warning once the bail cap is hit.
    private bool _slowClearLogged;

    public void Start(StepData step, ExecutionContext ctx)
    {
        if (!ctx.Config.EnableAutoDuty)
        {
            // The duty hand-off is the whole mechanism here: when this is off, the step has no
            // way to run the duty and Update() fails it. Say so, since a bare "EnterDuty failed"
            // is opaque. The toggle is ON by default; the user must have turned it off.
            Diagnostics.DebugLog.Warn(
                "EnterDuty: 'Run duties via AutoDuty' is OFF in /relic config, so this duty cannot be " +
                "handed to AutoDuty and the step fails. Turn it ON to let AutoDuty run the relic trials " +
                "and dungeons.");
            return;
        }

        _farm = ctx.CurrentObjective?.Completion.Kind switch
        {
            CompletionKind.LightGauge => FarmKind.Light,
            CompletionKind.MahatmaGauge => FarmKind.Mahatma,
            _ => FarmKind.None,
        };
        _startTicks = Environment.TickCount64;
        _started = false;
        _wasBound = false;
        _creditGraceStart = 0;
        _exitDelayStart = 0;
        _left = false;
        _credited = false;
        _creditSeenAt = 0;
        _clearedAt = 0;
        _leftAt = 0;
        _holdLogAt = 0;
        _leftForNails = false;
        _nailBails = 0;
        _streakLight = 0;
        _slowClearLogged = false;

        // Log which AutoDuty IPC functions are present up front. If they are all false the IPC is
        // not connected (AutoDuty not loaded / disabled / version mismatch) and every hand-off below
        // silently no-ops -- the real cause behind both "no setconfig" and "never started the duty".
        ctx.AutoDuty.LogAvailability();

        // Book NM dungeons: the RelicNote slot credit lands a moment AFTER the final-boss kill while
        // still INSIDE the instance, and a too-fast exit permanently loses it (user-confirmed). So
        // for those, HOLD in the instance (AutoExitDuty=false -> AutoDuty clears then stays put) and
        // let Relicable drive the leave once the slot credits (see the DungeonSlot branch in Update).
        // Everything else exits promptly -- gauges are read live off the equipped weapon and trials
        // are quest/item-authoritative, so their credit survives a fast exit. Forcing the flag also
        // undoes a leftover `/relic adset AutoExitDuty false` for the non-hold duties (which would
        // otherwise strand the character -- "not exiting at all").
        var needsHold = ctx.CurrentObjective?.Completion.Kind == CompletionKind.DungeonSlot;
        ctx.AutoDuty.SetAutoExit(!needsHold);

        switch (_farm)
        {
            case FarmKind.Light:
                GameState.TryGetNexusLight(out _baselineLight);
                StartFarm(ctx, ctx.Config.NexusFarmTerritoryType, ctx.Config.NexusFarmLoops,
                    ctx.Config.NexusFarmUnsynced, "Trial",
                    $"Light={_baselineLight}/{GameState.NexusLightMax}");
                break;

            case FarmKind.Mahatma:
                GameState.TryGetMahatma(out _baselineCompleted, out _baselinePoints, out _);
                var territory = ctx.Config.ZetaFarmTerritoryType;
                StartFarm(ctx, territory, ctx.Config.ZetaFarmLoops, ctx.Config.ZetaFarmUnsynced,
                    Data.DutyInfo.DutyModeForTerritory(territory),
                    $"Mahatma={_baselineCompleted}/{GameState.MahatmaCount} ({_baselinePoints}/{GameState.MahatmaPointsMax})");
                break;

            default:
                var loops = step.Loops > 0 ? step.Loops : 1;
                // Old relic trials/dungeons are soloed unsynced: set the matching DutyMode
                // (Trial/Regular) and Unsynced so AutoDuty will queue them for a lone player;
                // synced, an 8-man trial never pops solo and AutoDuty never starts.
                if (step.Unsynced)
                {
                    ctx.AutoDuty.SetDutyMode(Data.DutyInfo.DutyModeForTerritory(step.TerritoryType));
                    ctx.AutoDuty.SetUnsynced(true);
                }
                if (!ctx.AutoDuty.ContentHasPath(step.TerritoryType))
                    Diagnostics.DebugLog.Warn(
                        $"AutoDuty has no navigation path for TerritoryType {step.TerritoryType}; it cannot " +
                        "run this duty, so it will not start. Install AutoDuty's path/support for it.");
                ctx.AutoDuty.Run(step.TerritoryType, loops);
                break;
        }

        _handedOff = true;
    }

    // Shared farm hand-off: optionally set AutoDuty's mode + unsynced, warn if it has
    // no path for the duty, then run it for the loop cap.
    private static void StartFarm(
        ExecutionContext ctx, uint territory, int loops, bool unsynced, string dutyMode, string progress)
    {
        var capped = loops > 0 ? loops : 1;
        if (unsynced)
        {
            // A solo player is only queued into a trial/dungeon/raid when the matching
            // DutyMode is set and Unsynced is on; otherwise AutoDuty refuses the queue.
            ctx.AutoDuty.SetDutyMode(dutyMode);
            ctx.AutoDuty.SetUnsynced(true);
        }
        if (!ctx.AutoDuty.ContentHasPath(territory))
            Diagnostics.DebugLog.Warn(
                $"AutoDuty reports no navigation path for TerritoryType {territory}; the farm may stall. " +
                "Install AutoDuty's path for this duty, or point the farm at one it supports.");
        ctx.AutoDuty.Run(territory, capped);
        Diagnostics.DebugLog.Info(
            $"Farm: AutoDuty territory={territory} loops={capped} mode={dutyMode} unsynced={unsynced}, {progress}");
    }

    // Still inside a duty instance -> AutoDuty has not finished walking the player out yet.
    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    // Gate on the post-clear settle: stamps the moment the objective was first seen complete
    // (call it as soon as a completion branch matches) and reports true once ExitDelayMs has
    // elapsed. Until then the caller keeps the step InProgress, holding the run in/at the duty
    // so clear credit is applied before it finalizes. Stamped once per run; reset in Start.
    private bool ExitDelayElapsed()
    {
        if (_exitDelayStart == 0)
            _exitDelayStart = Environment.TickCount64;
        return Environment.TickCount64 - _exitDelayStart >= ExitDelayMs;
    }

    // Throttled (~2s) heartbeat of the hold-for-credit state while inside a book dungeon, so a stuck
    // exit shows WHICH signal is stuck (credit not landing, AutoDuty not stopped, or the game refusing
    // the leave) instead of looking like a silent hang. Purely diagnostic.
    private void LogHoldState(ExecutionContext ctx, int slot, string note)
    {
        var now = Environment.TickCount64;
        if (now - _holdLogAt < 2000)
            return;
        _holdLogAt = now;
        Diagnostics.DebugLog.Info(
            $"Book dungeon slot {slot} [{note}]: bound={BoundByDuty()}, credited={_credited}, " +
            $"dungeonComplete={GameState.IsDungeonComplete(slot)}, autoDutyStopped={ctx.AutoDuty.IsStopped()}, " +
            $"canLeave={GameState.CanLeaveDuty()}, left={_left}.");
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (!ctx.Config.EnableAutoDuty)
            return ExecutorStatus.Failed; // user disabled duty automation

        if (!_handedOff)
            return ExecutorStatus.InProgress;

        // Track whether we ever entered the instance, so a "did not credit" failure can say
        // whether AutoDuty actually cleared a duty (credit/mechanic/wrong-duty issue) or never
        // entered one at all (queue/path issue).
        if (BoundByDuty())
            _wasBound = true;

        // Ifrit EX nail bail-out (Light/Mahatma farm). Ifrit's Infernal Nails make him invulnerable
        // until they are all destroyed, turning an otherwise few-second unsynced clear into a long
        // detour. Per the farm strategy, leave the moment the nails spawn and let the run re-enter for
        // a fresh burst: forcing a leave changes territory, which -- while AutoDuty is looping -- makes
        // it re-queue the SAME duty (AutoDuty increments its own loop counter and re-enters). Its Stage
        // never reads Stopped during that re-queue, so a single re-entry does not trip the completion
        // logic below. Only fires when a live nail is loaded, so it is inert on a non-Ifrit farm duty.
        //
        // Bounding: the Light farm loops inside AutoDuty, so a run that ALWAYS nails would otherwise
        // churn re-entries up to the loop cap (~65). After MaxNailBails consecutive bails with no
        // credited clear it instead lets AutoDuty CLEAR through the nails once (a credit resets the
        // count, below), so an under-geared character still progresses. The Mahatma farm runs a single
        // loop, so each bail ends its hand-off and AutoDuty stops -- that path is bounded by the
        // controller's own consecutive-failure backoff instead (a nail bail reports no progress).
        if (_farm != FarmKind.None && ctx.Config.AbandonOnIfritNails
            && BoundByDuty() && GameState.IfritNailPresent())
        {
            // The slow-clear fallback is only taken when the nail phase is actually WINNABLE, which
            // means Relicable must be pinning RSR's target order onto the nails. Ifrit is
            // invulnerable while any nail lives, and RSR's default hostile sort is by hitbox radius
            // descending -- i.e. Ifrit, forever. Clearing through without nail targeting is not a
            // slow clear, it is a guaranteed 60-minute duty-timer stall that never credits, and
            // because _nailBails only resets on a CREDITED clear every later run takes the same
            // branch. Keep bailing instead: that at least still re-queues.
            var bailsExhausted = _farm == FarmKind.Light && _nailBails >= MaxNailBails;
            var canWinNailPhase = ctx.Config.PrioritiseIfritNailTargeting;

            if (bailsExhausted && !_slowClearLogged)
            {
                _slowClearLogged = true;
                Diagnostics.DebugLog.Warn(canWinNailPhase
                    ? $"Ifrit EX nails: bailed {_nailBails} runs in a row without a clear; clearing through the " +
                      "nails this time instead of churning re-entries. If this keeps happening the burst cannot " +
                      "beat the nail phase -- use a stronger job/gear, or turn off 'Abandon Ifrit EX on Infernal " +
                      "Nails' in /relic config."
                    : $"Ifrit EX nails: bailed {_nailBails} runs in a row without a clear, and 'Target Ifrit's " +
                      "Infernal Nails first' is turned OFF in /relic config. Relicable will keep bailing rather " +
                      "than enter a nail phase it cannot win -- turn that setting on to allow a slow clear, or " +
                      "use a job/gear that can burst Ifrit past 50% before the nails spawn.");
            }

            if (bailsExhausted && canWinNailPhase)
            {
                // Fall through and let AutoDuty clear the nail phase; a credited clear resets the count.
            }
            else
            {
                _nailBails++;
                _leftForNails = true;
                // LeaveDuty no-ops (returns false) while the game refuses the leave (a mechanic
                // transition), so it self-throttles; log only on the tick the leave is actually issued.
                if (GameState.LeaveDuty())
                    Diagnostics.DebugLog.Info(
                        $"Ifrit EX Infernal Nails detected during the farm (bail {_nailBails}); leaving to re-queue " +
                        "a fresh burst rather than wait out the nail phase.");
                return ExecutorStatus.InProgress; // hold while we zone out and the run re-enters
            }
        }

        // Reset the consecutive nail-bail count on a credited Light clear (a rising edge in the gauge):
        // occasional nails between good clears must not accumulate toward the slow-clear fallback, which
        // is only for a run that can NEVER burst Ifrit. _streakLight tracks the last-seen Light so this
        // is a per-clear edge, independent of _baselineLight (which the end-of-run progress check uses).
        // Only the Light farm needs it -- the Mahatma path is bounded by the controller backoff.
        if (_farm == FarmKind.Light)
        {
            var lightSeen = GameState.NexusLight();
            if (lightSeen > _streakLight)
            {
                _nailBails = 0;
                _slowClearLogged = false;
            }
            _streakLight = lightSeen;
        }

        // Light farm: stop once the gauge fills. If it filled inside a duty, wait for the exit
        // first so AutoDuty walks the player out, instead of being stopped mid-completion (which
        // strands them on the duty-complete screen). A FATE fill is not bound, so it stops at once.
        if (_farm == FarmKind.Light && GameState.IsLightGaugeFull())
        {
            if (!ExitDelayElapsed())
                return ExecutorStatus.InProgress; // settle so the last-boss Light credit lands before we finalize
            if (BoundByDuty())
                return ExecutorStatus.InProgress;
            ctx.AutoDuty.Stop();
            Diagnostics.DebugLog.Info($"Nexus Light full ({GameState.NexusLight()}/{GameState.NexusLightMax}); left the duty, stopping farm.");
            return ExecutorStatus.Complete;
        }

        // Mahatma farm: stop the instant the current Mahatma is full (40/40). A Mahatma does NOT
        // auto-bank on the duty's last boss -- it sits "awakened" at full points (raw 80) until the
        // next is attached at Remon, which is what increments completed (raw -> 500+). So stopping
        // only on a rise in completed never fires from farming; stop on full instead, so the
        // controller goes to Remon to bank it and attach the next.
        if (_farm == FarmKind.Mahatma)
        {
            GameState.TryGetMahatma(out var nowDone, out var nowPts, out _);
            if (nowDone > _baselineCompleted || nowPts >= GameState.MahatmaPointsMax)
            {
                // Full -> bank at Remon next. Do NOT stop AutoDuty while still bound by the instance:
                // the Mahatma fills on the last boss, so stopping here strands the player on the duty-
                // complete screen. Let AutoDuty walk out of the duty first, then stop and complete.
                if (!ExitDelayElapsed())
                    return ExecutorStatus.InProgress; // settle so the last-boss Mahatma credit lands before we finalize
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                ctx.AutoDuty.Stop();
                Diagnostics.DebugLog.Info(
                    $"Mahatma full/awakened (completed={nowDone}/{GameState.MahatmaCount}, points={nowPts}/{GameState.MahatmaPointsMax}); " +
                    "left the duty, stopping farm to bank it and attach the next at Remon.");
                return ExecutorStatus.Complete;
            }
        }

        // Book dungeon: the RelicNote slot credit lands a moment AFTER the final-boss kill, WHILE
        // still inside the instance -- a too-fast exit permanently loses it (user-confirmed). We held
        // AutoDuty in the instance for this (AutoExitDuty=false, set in Start); now poll the slot
        // while BoundByDuty and only leave once it credits.
        if (ctx.CurrentObjective?.Completion is { Kind: CompletionKind.DungeonSlot } dungeon)
        {
            // Latch the slot credit the first time it is seen complete (rising edge). Once earned it
            // stays earned even if a later read of the note would differ -- we never un-credit.
            if (!_credited && GameState.IsDungeonComplete(dungeon.Slot))
            {
                _credited = true;
                _creditSeenAt = Environment.TickCount64;
                Diagnostics.DebugLog.Info($"Book dungeon slot {dungeon.Slot} credited; settling then leaving.");
            }

            if (BoundByDuty())
            {
                // Detect the boss cleared as "AutoDuty idle-stopped while still bound" (with AutoExitDuty
                // off it clears then holds). Being BoundByDuty already proves the duty was entered, so
                // this is NOT gated on _started: that way the bounded fallback still fires (rather than
                // stranding) even if the AutoDuty IPC drops mid-instance so IsStopped() reads true from
                // the first bound tick and no credit lands. While AutoDuty is actively running we reset
                // the stamp, so a transient stop mid-fight cannot start the timeout early.
                if (!ctx.AutoDuty.IsStopped())
                {
                    _started = true;
                    _clearedAt = 0;
                }
                else if (_clearedAt == 0)
                {
                    _clearedAt = Environment.TickCount64;
                    Diagnostics.DebugLog.Info($"Book dungeon: AutoDuty idle while bound; holding for slot {dungeon.Slot} credit, then leaving.");
                }

                // Leave on EITHER the credit having landed and settled, OR a bounded hold since the
                // clear elapsing (credit never observed -> leave anyway rather than strand). We ALWAYS
                // drive the leave ourselves because AutoExitDuty is off; this branch never falls
                // through to the generic run logic, so a stuck signal can no longer hang us in here.
                var settled = _credited && Environment.TickCount64 - _creditSeenAt >= ExitDelayMs;
                var heldLongEnough = _clearedAt != 0 && Environment.TickCount64 - _clearedAt >= CreditGraceMs;

                if (settled || heldLongEnough)
                {
                    if (!_left)
                    {
                        if (GameState.LeaveDuty())
                        {
                            _left = true;
                            _leftAt = Environment.TickCount64;
                            Diagnostics.DebugLog.Info(
                                $"Book dungeon slot {dungeon.Slot}: issued forced leave (credited={_credited}, holdTimeout={heldLongEnough}).");
                        }
                        else
                        {
                            // The game refused the leave (CanLeaveCurrentContent false: a boss-death /
                            // duty-complete transition). Retry every tick; the heartbeat below records
                            // canLeave so a persistent refusal is visible instead of a silent hang.
                            LogHoldState(ctx, dungeon.Slot, "ready to leave, game refused it");
                        }
                    }
                    else if (Environment.TickCount64 - _leftAt >= LeaveRetryMs)
                    {
                        // Issued the leave but still inside -> the game swallowed it; re-issue.
                        _left = false;
                        LogHoldState(ctx, dungeon.Slot, "leave did not take, re-issuing");
                    }
                    return ExecutorStatus.InProgress; // waiting for the zone-out
                }

                // Still holding. Heartbeat only once the exit phase is relevant (credited or cleared),
                // so AutoDuty's mid-dungeon fight is not log-spammed.
                if (_credited || _clearedAt != 0)
                    LogHoldState(ctx, dungeon.Slot, "holding for credit/settle");
                return ExecutorStatus.InProgress;
            }

            // Not bound. If we were inside and have now left, finish here: success if the slot ever
            // credited, else fail with guidance. (If we were NEVER bound yet, fall through to the run/
            // startup logic below so a duty AutoDuty never entered is still detected.)
            if (_wasBound)
            {
                ctx.AutoDuty.Stop();
                if (_credited || GameState.IsDungeonComplete(dungeon.Slot))
                {
                    Diagnostics.DebugLog.Info($"Book dungeon slot {dungeon.Slot}: left the instance, completing.");
                    return ExecutorStatus.Complete;
                }
                Diagnostics.DebugLog.Warn(
                    $"EnterDuty: dungeon slot {dungeon.Slot} left the instance WITHOUT crediting. Likely the WRONG " +
                    "instance for this book slot (a Duty-Support / revised variant), or the relic was not equipped " +
                    "at the final-boss kill. Stopping.");
                return ExecutorStatus.Failed;
            }
            // Never entered yet -> fall through to the run logic (startup grace + 'never started').
        }

        // AutoDuty drives everything (including its own looping) until it stops. Note when it
        // actually engages (leaves the Stopped stage); a Run that AutoDuty ignored (no path for
        // the duty, cannot queue) never leaves Stopped and must not be read as "duty done".
        if (!ctx.AutoDuty.IsStopped())
        {
            _started = true;
            return ExecutorStatus.InProgress;
        }

        // Tolerate the brief window after hand-off before AutoDuty leaves the Stopped
        // stage, so we do not false-complete on the first tick(s).
        if (Environment.TickCount64 - _startTicks < StartupGraceMs)
            return ExecutorStatus.InProgress;

        // Past the grace and still stopped. If AutoDuty never engaged, it did not run the duty
        // (it stayed Stopped the whole time) -- fail with guidance rather than false-complete.
        if (!_started)
        {
            // For the farms the real duty is the CONFIGURED farm TerritoryType, not step.Territory
            // (which is 0 on a farm step), so report that one or the message names the wrong duty.
            var territory = _farm switch
            {
                FarmKind.Light => ctx.Config.NexusFarmTerritoryType,
                FarmKind.Mahatma => ctx.Config.ZetaFarmTerritoryType,
                _ => step.TerritoryType,
            };
            Diagnostics.DebugLog.Warn(
                $"EnterDuty: AutoDuty never started the duty (TerritoryType {territory}); it stayed stopped " +
                "through the grace. AutoDuty has no path/support for it, it is locked, or it cannot solo-queue " +
                "it. " + (_farm == FarmKind.None
                    ? "Confirm AutoDuty runs it manually first."
                    : "Download AutoDuty's path for this duty (its window has a path downloader), or set the " +
                      "farm duty in /relic config to one AutoDuty already supports."));
            return ExecutorStatus.Failed;
        }

        // AutoDuty started and has now stopped (loops exhausted). Complete if any progress was
        // made (the controller re-arms the farm); fail on no progress so the backoff halts with
        // an actionable log instead of looping (duty locked / no path / AutoDuty mis-set).
        switch (_farm)
        {
            case FarmKind.Light:
                GameState.TryGetNexusLight(out var nowLight);
                return Progressed(nowLight > _baselineLight, ctx, "Light", _leftForNails);

            case FarmKind.Mahatma:
                GameState.TryGetMahatma(out var nowCompleted, out var nowPoints, out _);
                // Dump the raw Mahatma state after the run so a "no progress" result shows whether
                // the duty gave ANY credit (raw moved) or none (not cleared / relic not equipped).
                GameState.LogMahatmaDebug();
                // Progress = overall fill advanced. Comparing completed*PointsMax+points
                // (not points alone) avoids the points-reset-to-0 on a freshly awakened
                // Mahatma reading as "no progress".
                var before = _baselineCompleted * GameState.MahatmaPointsMax + _baselinePoints;
                var after = nowCompleted * GameState.MahatmaPointsMax + nowPoints;
                return Progressed(after > before, ctx, "Mahatma", _leftForNails);

            default:
                // A book DUNGEON objective (DungeonSlot) is only done when the game credits the
                // slot. The per-tick check earlier in Update completes the step the instant that
                // lands (even after a fast AutoDuty exit), so reaching here means AutoDuty has
                // stopped and the slot is STILL uncredited. Give the credit a grace window to
                // propagate -- the RelicNote update can arrive a moment after the duty is left, and
                // the per-tick check above keeps running during the grace and completes if it lands.
                // Only fail once the grace expires with no credit, so a wrong/uncleared duty arms the
                // 3-strike backoff (halt with guidance) instead of re-running forever. Other
                // FarmKind.None duties (base-relic trials, Braves dungeons) complete unconditionally:
                // their completion is quest/item authoritative and re-run is guarded elsewhere.
                if (ctx.CurrentObjective?.Completion.Kind == CompletionKind.DungeonSlot)
                {
                    if (_creditGraceStart == 0)
                        _creditGraceStart = Environment.TickCount64;
                    if (Environment.TickCount64 - _creditGraceStart < CreditGraceMs)
                        return ExecutorStatus.InProgress; // wait for the slot credit to propagate

                    Diagnostics.DebugLog.Warn(
                        $"EnterDuty: AutoDuty finished but the book's dungeon slot " +
                        $"{ctx.CurrentObjective.Completion.Slot} did not credit within {CreditGraceMs / 1000}s. " +
                        $"Resolved to '{ctx.CurrentObjective.DisplayName}' (TerritoryType {step.TerritoryType}, " +
                        $"enteredDuty={_wasBound}). " +
                        (_wasBound
                            ? "The duty WAS entered and cleared. Most likely the resolved TerritoryType is the WRONG " +
                              "instance of this book's dungeon (e.g. a Duty-Support / revised variant rather than the " +
                              "plain dungeon the book credits against), so killing its boss does not flip the book " +
                              "slot. Confirm the dungeon named above is the one the book asks for. (The credit also " +
                              "needs the relic equipped and the player alive at the final-boss kill, but the relic is " +
                              "equipped here.)"
                            : "The duty was NEVER entered: AutoDuty could not queue it (no path/support, locked, " +
                              "or not solo-queueable). Download AutoDuty's path for this duty.") +
                        " Stopping after repeated no-credit clears rather than looping.");
                    return ExecutorStatus.Failed;
                }
                // Quest/item-authoritative trials + Braves dungeons: settle a beat before
                // finalizing so the clear credit is applied before the controller advances.
                if (!ExitDelayElapsed())
                    return ExecutorStatus.InProgress;
                return ExecutorStatus.Complete;
        }
    }

    private static ExecutorStatus Progressed(bool progressed, ExecutionContext ctx, string what, bool leftForNails = false)
    {
        if (progressed)
            return ExecutorStatus.Complete;
        // The last Ifrit hand-off included a nail bail-out, so "no progress" is expected rather than a
        // clear failure -- the controller retries. It only halts after the usual consecutive-failure
        // backoff (here, several such hand-offs in a row). Note BOTH plausible causes honestly: the flag
        // is hand-off level, so a mixed hand-off (a nail bail on one loop, a separate clear failure on
        // another) also lands here.
        if (leftForNails)
        {
            Diagnostics.DebugLog.Warn(
                $"{what} farm: no credit from the last Ifrit hand-off, which included an Infernal Nail bail-out. " +
                "Retrying. If this recurs, either the burst cannot beat the nail phase (use a stronger job/gear, or " +
                "turn off 'Abandon Ifrit EX on Infernal Nails' in /relic config) or the duty is not clearing for " +
                "another reason (death / rotation / wrong combat preset).");
            return ExecutorStatus.Failed;
        }
        if (what == "Mahatma")
            Diagnostics.DebugLog.Warn(
                "Mahatma farm made no progress: the Mahatma gained no credit from that run. A Mahatma " +
                "charges only at the LAST BOSS of a CLEARED duty with the relic equipped; at 40/40 the " +
                "points are capped, so the only remaining progress is the awaken on a clear. So AutoDuty " +
                "either did not actually CLEAR the duty (its combat/rotation must be engaged to kill the " +
                "last boss) or the relic was not equipped at the kill. Confirm AutoDuty clears this duty " +
                $"unattended from its own window. Farm duty TerritoryType: {ctx.Config.ZetaFarmTerritoryType}");
        else
            Diagnostics.DebugLog.Warn(
                $"{what} farm made no progress. Check the duty is unlocked, AutoDuty has a path for it, and may run it (Unsynced). " +
                $"Farm duty TerritoryType: {ctx.Config.NexusFarmTerritoryType}");
        return ExecutorStatus.Failed;
    }

    public void Stop(ExecutionContext ctx)
    {
        // Clear the run latch so a later duty objective can start AutoDuty again.
        ctx.AutoDuty.ResetRun();
        _handedOff = false;
        _started = false;
    }
}
