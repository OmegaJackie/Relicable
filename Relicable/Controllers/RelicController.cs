using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Relicable.BaseRelic;
using Relicable.Braves;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Controllers;

// The execution engine. A flat, non-blocking state machine driven on the
// framework update tick (see DESIGN.md section 5). It selects the lowest
// incomplete stage and objective, runs the active step's executor until it
// completes, then advances. Companion plugins are released on stop.
public sealed class RelicController
{
    public enum State { Idle, SelectStage, SelectObjective, RunStep, Stopped }

    private readonly ExecutionContext _ctx;
    private IReadOnlyList<RelicObjective> _objectives;
    private readonly Dictionary<StepType, ITaskExecutor> _executors;
    private readonly External.DependencyRegistry _dependencies;

    private State _state = State.Idle;
    private RelicObjective? _objective;
    private int _stepIndex;
    private ITaskExecutor? _activeExecutor;

    // Failure backoff: stop re-selecting an objective that keeps failing rather than
    // looping forever. This is what kept Novus melding "stalling" -- the meld step
    // failed (auto-meld off or the meld UI not open), the controller re-selected the
    // same objective, and it failed again, indefinitely.
    private string _lastFailedObjectiveId = string.Empty;
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailures = 3;

    // Only redirect to a co-located book FATE when it has at least this long remaining, so there
    // is time to reach and clear it (the user-facing "more than 3 minutes remaining").
    private const long FateMinRemainingSeconds = 180;

    // AllStepsDone objectives have no game-memory completion flag, so once their
    // steps run we record them here (seeded from and written back to config so they
    // survive a reload) to avoid re-selecting them.
    private readonly HashSet<string> _proceduralDone;

    // Quest-path (sequence-driven) base-relic objectives: in-memory "already ran this
    // sequence" markers so the engine waits for the game's quest sequence to advance
    // rather than re-running the step. Cleared whenever the live sequence changes, and
    // NOT persisted, so a repeated relic re-runs the path. _lastPathSeq is the last
    // observed live sequence (-1 = none yet).
    private readonly HashSet<string> _pathDone = new();
    private int _lastPathSeq = -1;

    // Watchdog for the start-of-line accept (live sequence 0). The generated seq-0 accept step
    // (InteractNpc Gerolt) completes on the dialogue closing, NOT on a quest being accepted, so if a
    // prerequisite we did not pre-detect (e.g. the class's level-50 job quest) blocks the accept, the
    // sequence never leaves 0 and the run would idle at Gerolt forever. This stamps when the accept
    // first finished (entered _pathDone) with the sequence still 0; if it is still stuck after
    // AcceptStallMs, the controller stops with guidance. 0 = not waiting; reset on any sequence change.
    private long _acceptStalledSince;
    private const long AcceptStallMs = 20_000;

    // Repeat-relic Animus restart guard. When a fresh Atma weapon carries a previous relic's stale,
    // complete last-book note, the engine auto-buys book 1 to start THIS weapon's book run. This holds
    // the equipped Atma weapon id it did that for, so the wrap fires ONCE per weapon: after the engine
    // drives books 1..9 back to a complete note 9 on the SAME weapon, it runs the Animus enhancement
    // instead of re-buying book 1 forever.
    //
    // PERSISTED (Configuration.AnimusBooksDrivenWeaponId), not a field. It used to be in-memory, and
    // the gap that opened was the obvious one: finish book 9, reload the plugin (or restart the game),
    // press Start -- the witness is gone, the note still reads complete, and the run buys book 1 and
    // grinds all nine again. Written through a property so every existing assignment site persists.
    private uint AnimusWrapWeaponId
    {
        get => _ctx.Config.AnimusBooksDrivenWeaponId;
        set
        {
            if (_ctx.Config.AnimusBooksDrivenWeaponId == value)
                return;
            _ctx.Config.AnimusBooksDrivenWeaponId = value;
            // Saved immediately rather than on Dispose: the whole failure this fixes is a session
            // that ended without a clean shutdown between the last book and the next Start.
            Plugin.PluginInterface.SavePluginConfig(_ctx.Config);
        }
    }

    // The Atma weapon the enhancement flow was last pointed at, and the one the game DECLINED to
    // enhance. A refusal is the only authority that separates "this weapon's books are done" from
    // "this note belongs to a previous relic", so it is earned by trying, not guessed up front.
    //
    // In-memory ON PURPOSE, unlike the books-driven marker. A wrong "refused" would send a finished
    // weapon back to G'Jusana for nine more books, so it must not outlive the session that observed
    // it; the cost of re-deriving it after a reload is one wasted trip to Jalzahn.
    private uint _animusUpgradeTriedWeaponId;
    private uint _animusUpgradeRefusedWeaponId;
    private int _animusUpgradeFailures;

    // Failures of the enhancement flow before we accept "declined" as the explanation. One is not
    // enough: Jalzahn's turn-in submenu is a SEAM (see AnimusUpgradeExecutor), so a single menu
    // hiccup must not be read as proof the books belong to someone else and buy a book over it.
    private const int AnimusUpgradeFailuresBeforeBookRun = 2;

    // Calibration aid (/relic bravesseq): the last (material-quest id, sequence) written to the debug
    // log, so each Braves material-quest sequence change is logged exactly once. In-memory only.
    private (uint quest, int seq) _lastBravesSeqLogged = (0, -1);

    // Base-relic (Relic-stage) generated objectives (beastmen, trials): "already ran this
    // run" markers. IN-MEMORY ONLY, never persisted. For these the quest sequence is the
    // authority -- a queued trial that did not credit (relic not equipped, wrong sequence)
    // must stay retryable, so persisting "done" would wrongly halt the next launch with the
    // part marked complete. Cleared on Start so each run re-evaluates the parts.
    private readonly HashSet<string> _relicRan = new();

    // Round-robin bookkeeping for spawn-gated book FATEs. When a FATE objective
    // rotates off (its FATE has not spawned within the window; the executor returns
    // ExecutorStatus.Rotate), we stamp the tick it was last tried here, and objective
    // selection prefers the least-recently-tried FATE. This cycles through a book's
    // incomplete FATEs IN CONSECUTIVE SLOT ORDER (1 -> 2 -> 3 -> 1 ...; equal ticks
    // tiebreak by slot) instead of re-picking the same dead one. Its presence also marks
    // "already tried this run", which drives the first-pass-quick / later-pass-wait timing
    // (see BeginStep). IN-MEMORY ONLY, and cleared on Start; a FATE not in the map (never
    // rotated) sorts first.
    private readonly Dictionary<string, long> _fateCheckedTick = new();

    // First pass through a book's FATEs: only GLANCE for this many seconds (skip an unspawned FATE
    // fast and move to the next in order) rather than waiting the full Config.FateRotateSeconds. On
    // later passes each FATE gets the full configured wait. Kept a touch above the zone/FATE-table
    // load time so a FATE that IS up is not skipped while the table is still populating.
    private const int FirstPassFateCheckSeconds = 15;

    private readonly Steps.Combat.DeathRecovery _death = new();

    // Global aggro backstop: fights back when something is engaged with us and the running step is
    // not engaging it. Ticked for every step (see Tick), which is the whole point -- the loops that
    // never defended themselves are the ones that never called DefendSelf.
    private readonly Steps.Combat.AggroWatchdog _aggro = new();

    // TickCount64 of the previous Tick, so a defended tick can be given back to the step's deadline.
    private long _lastTick;

    // Non-empty while the aggro watchdog has intervened (or has spotted a fight that is going
    // nowhere); surfaced in the main window so it is never a silent takeover.
    public string AggroWatchdogStatus => _aggro.Status;

    // Atma-stage delegation to CBT's Fate Tool Kit (Configuration.AtmaBackend). Ticked every
    // running frame; while it owns the Atma stage the normal objective engine is parked.
    private readonly AtmaCbtDriver _atmaCbt = new();

    public State Current => _state;
    public RelicObjective? ActiveObjective => _objective;
    public int ActiveStepIndex => _stepIndex;

    // Non-empty while CBT is handling (or awaiting the enhancement for) the Atma stage; shown in
    // the main window so the delegation is visible.
    public string AtmaDelegationStatus => _atmaCbt.Status;

    // Non-empty while the BUILT-IN Atma farm is working a zone: how many of THIS zone's atma are
    // held against the per-zone target (Config.AtmaPerZone). Shown in the main window so the point
    // at which it moves to the next zone is visible.
    public string AtmaZoneProgress
    {
        get
        {
            if (_state is State.Idle or State.Stopped)
                return string.Empty;
            if (_objective is not { Stage: RelicStage.Atma } o
                || o.Completion.Kind != CompletionKind.ItemCount)
                return string.Empty;
            var target = AtmaTarget(o);
            return $"This zone's atma: {GameState.InventoryCount(o.Completion.ItemId)}/{target} " +
                   "held, then on to the next zone.";
        }
    }

    // True while Relicable has stepped aside because CBT's Fate Tool Kit is running alongside it
    // (an uncoordinated co-run, NOT the Atma-backend delegation). Surfaced in the main window so the
    // user knows why Relicable is idle and how to reclaim control (turn off CBT's Fate Tool Kit).
    private bool _cbtConflict;
    public bool CbtFateToolKitConflict => _cbtConflict;

    public RelicController(
        ExecutionContext ctx,
        IReadOnlyList<RelicObjective> objectives,
        IEnumerable<ITaskExecutor> executors,
        External.DependencyRegistry dependencies)
    {
        _ctx = ctx;
        _objectives = objectives;
        _executors = executors.ToDictionary(e => e.Handles);
        _dependencies = dependencies;
        _proceduralDone = new HashSet<string>(ctx.Config.CompletedProceduralObjectives);
    }

    // "/relic booksdone": record that the EQUIPPED Atma weapon's nine Trials of the Braves books are
    // finished, so the run goes to Jalzahn for the Atma -> Animus enhancement instead of reading the
    // complete note as a previous relic's leftover and buying book 1.
    //
    // This exists because the ambiguity is real and not ours to solve: the game keeps the last bought
    // note active forever, so on a repeat relic "note 9, complete" genuinely does not say which relic
    // finished it. Relicable records the answer as it drives the books, but a player who did them by
    // hand -- or who reloaded the plugin before the record was written -- has no such record, and the
    // wrong guess costs 500 poetics and a nine-book regrind. Returns the message to show.
    public string MarkAnimusBooksDone()
    {
        var equipped = GameState.EquippedRelicItemId();
        if (equipped == 0)
            return "no relic weapon is equipped. Equip the Atma weapon whose books you finished, then run this again.";

        var stage = GameState.EquippedRelicStage();
        if ((int)stage >= (int)RelicStage.Animus)
            return $"the equipped weapon is already {stage}, so its Animus enhancement is done -- nothing to record.";
        if (stage != RelicStage.Atma)
            return $"the equipped weapon is {stage}, not an Atma weapon. The Trials of the Braves are done ON an " +
                   "Atma weapon, so equip that one first.";

        AnimusWrapWeaponId = equipped;
        // Also overrides a "Jalzahn declined this weapon" conclusion from earlier in the session:
        // the user is telling us directly, and they outrank an inference drawn from a failed menu.
        _animusUpgradeRefusedWeaponId = 0;
        _animusUpgradeFailures = 0;
        var note = GameState.ActiveRelicNoteId();
        var incomplete = GameState.IncompleteActiveBookSlots();
        if (incomplete.Count > 0)
            return $"recorded, but note {note} still shows incomplete slot(s): {string.Join(", ", incomplete)}. " +
                   "Jalzahn will not offer the enhancement until the book is actually finished.";
        return $"recorded: the books for this Atma weapon are finished (note {note}). /relic start will now go to " +
               "Jalzahn (Fallgourd Float, North Shroud) for the Atma -> Animus enhancement.";
    }

    // Replace the objective set (used by /relic reload). Re-plans if mid-run.
    public void ReloadObjectives(IReadOnlyList<RelicObjective> objectives)
    {
        _objectives = objectives;
        if (_state == State.RunStep)
        {
            _activeExecutor?.Stop(_ctx);
            _activeExecutor = null;
            _state = State.SelectObjective;
        }
        DebugLog.Info($"Reloaded {objectives.Count} objectives");
    }

    // Re-plan from objective selection. Called by the UI when the stage-selection
    // mode or the manual stage changes, so the new choice takes effect immediately
    // instead of after the current step finishes. No-op when not running.
    public void Replan()
    {
        if (_state is State.Idle or State.Stopped)
            return;
        _activeExecutor?.Stop(_ctx);
        _activeExecutor = null;
        _state = State.SelectObjective;
        DebugLog.Info("Replan requested; re-selecting objective");
    }

    // Required dependencies that are not even loaded (a loaded-but-no-IPC plugin is
    // treated as usable). Empty means good to go.
    public IReadOnlyList<string> MissingRequiredDependencies()
        => _dependencies.MissingRequired();

    // Returns false (and does not start) if a required dependency gate is absent.
    public bool Start()
    {
        if (_state is not (State.Idle or State.Stopped))
            return true;
        if (MissingRequiredDependencies().Count > 0)
            return false;
        _state = State.SelectStage;
        // Re-evaluate base-relic parts fresh each run: drop the in-session "ran this" markers
        // so a part that did not credit last time (e.g. the relic was not equipped) is retried
        // rather than skipped.
        _relicRan.Clear();
        _pathDone.Clear();
        _lastPathSeq = -1;
        _fateCheckedTick.Clear();
        _aggro.Reset();
        // A fresh Start re-earns the "Jalzahn declined this weapon" conclusion rather than inheriting
        // it: the user may have fixed whatever actually blocked the enhancement (a full inventory, a
        // half-open menu), and re-trying costs a trip while being wrong costs nine books.
        _animusUpgradeTriedWeaponId = 0;
        _animusUpgradeRefusedWeaponId = 0;
        _animusUpgradeFailures = 0;
        // A fresh Start re-tries every Braves accept: the player may have just done the sidequest that
        // was gating one, or cleared whatever stopped the giver offering it.
        _bravesAcceptFailed.Clear();
        _bravesFetchTried = false;
        _bravesDoneLogged = false;
        DebugLog.Info("Start: entering SelectStage");
        return true;
    }

    // Why the run halted, when it halted because the PLAYER has something to do (accept a quest, equip
    // the right weapon, gather an item the engine does not farm). Empty for a plain stop.
    //
    // This exists because a stopped run with no active objective renders an empty main window: the
    // explanation went to the debug log, which most players never open, so "it just stops and shows
    // nothing" was indistinguishable from a broken plugin. Set by StopWith, cleared by Stop and Start.
    public string StopReason { get; private set; } = string.Empty;

    // Halt with guidance the player must act on: log it AND keep it for the main window. Stop() clears
    // StopReason, so it is assigned after.
    private void StopWith(string guidance)
    {
        DebugLog.Warn(guidance);
        Stop();
        StopReason = guidance;
    }

    public void Stop()
    {
        StopReason = string.Empty;
        _activeExecutor?.Stop(_ctx);
        _activeExecutor = null;
        _ctx.Navmesh.Stop();
        _ctx.Rotation.Disable();
        // A deliberate Stop must also halt a handed-off AutoDuty run: the executor's
        // own Stop only clears the run latch, so without this a mid-farm Stop left
        // AutoDuty looping the configured duty unsupervised. Idempotent when AutoDuty
        // is already stopped, so the normal completion paths are unaffected.
        _ctx.AutoDuty.Stop();
        // A deliberate Stop must also halt a delegated CBT Atma grind (idempotent otherwise).
        _atmaCbt.EnsureStopped(_ctx);
        _aggro.Reset();
        _cbtConflict = false;
        _state = State.Stopped;
        DebugLog.Info("Stop: halted, companions released");
    }

    // Called every framework tick by the plugin.
    public void Tick()
    {
        // Real time since the previous tick, stamped before anything can return early, so the aggro
        // watchdog can hand a step back exactly the time it spent fighting.
        var tickNow = Environment.TickCount64;
        var tickDelta = _lastTick == 0 ? 0 : tickNow - _lastTick;
        _lastTick = tickNow;

        // Calibration heartbeat (debug only): record Braves material-quest sequence changes so the
        // per-drop RequestedAtSequences can be read off. Independent of the run state.
        if (DebugLog.On)
            LogBravesSequenceChange();

        // Cleared each tick and re-asserted only by the CBT co-run guard below, so the main-window
        // warning never lingers after CBT is turned off or the controller takes another branch.
        _cbtConflict = false;

        // Death handling: while running, recover from death and resume the current
        // objective from its start rather than stopping.
        if (_state == State.RunStep && _ctx.Config.RecoverOnDeath)
        {
            switch (_death.Tick())
            {
                case Steps.Combat.DeathRecovery.Result.Reviving:
                    return; // dead; don't run steps until revived
                case Steps.Combat.DeathRecovery.Result.JustRevived:
                    _activeExecutor?.Stop(_ctx);
                    _activeExecutor = null;
                    _state = State.SelectObjective;
                    return;
            }
        }

        // Atma stage delegated to CBT's Fate Tool Kit: while the driver owns the Atma stage it
        // runs the CBT grind and returns true, so the built-in objective engine stays parked
        // (its Atma objectives never run under the CBT backend). Any active executor is stopped
        // once on hand-off so no stale navmesh/rotation lingers. When the driver returns false
        // (not the Atma stage, or the Zodiac weapon obtained) it also stops CBT if it was running.
        if (_state is State.SelectStage or State.SelectObjective or State.RunStep && _atmaCbt.Tick(_ctx))
        {
            if (_activeExecutor != null)
            {
                _activeExecutor.Stop(_ctx);
                _activeExecutor = null;
                _state = State.SelectObjective;
            }
            return;
        }

        // CBT Fate Tool Kit co-run guard: if the user is running CBT's autonomous FATE grinder at the
        // same time as Relicable (WITHOUT the Atma-backend delegation, which is handled above and
        // returns before here), both would drive the character via vnavmesh and fight over movement.
        // Per the user's choice, Relicable STEPS ASIDE -- it parks its engine (stopping any movement /
        // combat) so CBT drives alone; turning CBT's Fate Tool Kit off resumes Relicable automatically.
        // Only the tweak being ENABLED is observable over CBT's IPC (its run state is not exposed), so
        // that is the signal; the tweak Relicable's own delegation enables is disabled again when that
        // delegation ends, so this never trips on Relicable's own doing.
        if (_state is State.SelectStage or State.SelectObjective or State.RunStep
            && _ctx.Bot?.IsTweakEnabled(AtmaCbtDriver.TweakClassName) == true)
        {
            if (_activeExecutor != null)
            {
                _activeExecutor.Stop(_ctx);
                _activeExecutor = null;
                _state = State.SelectObjective;
            }
            _cbtConflict = true;
            return;
        }

        // Global aggro backstop. Runs for EVERY step, ahead of the step itself, because the loops
        // that never fight back are precisely the ones that never thought to ask -- the teleport
        // executor's in-combat wait stands still for twenty seconds BECAUSE something is hitting it,
        // and the flag walk reads no combat state at all. Owning the tick when it fires gives it the
        // same contract CombatAssist.DefendSelf has inside an executor: the step does not run while
        // we are defending. It only fires on an aggro nothing else has engaged, so every path that
        // already handles its own combat keeps it silent. See Steps/Combat/AggroWatchdog.cs.
        if (_state == State.RunStep && _objective != null && _stepIndex < _objective.Steps.Count)
        {
            var running = _objective.Steps[_stepIndex];
            if (_aggro.Tick(_ctx, running.Type == StepType.ParticipateFate))
            {
                // Credit the fight back to the step's deadline, so a long defense cannot fail the
                // step for the wrong reason.
                //
                // KNOWN LIMIT: this is the only clock the controller owns. Executors keep their own
                // wall-clock deadlines, and those keep running while their Update is not called --
                // their own DefendSelf branches freeze them, but those branches cannot run on a tick
                // they do not get. That is why the loops with real stall clocks (the FATE approach,
                // the leve travel, the flag walk, the treasure-map phases) each call DefendSelf
                // THEMSELVES: doing so pre-empts this watchdog entirely, since the attacker becomes
                // the hard target and an attended aggro never fires it. The watchdog is what catches
                // the loops that have neither -- shops, turn-ins, waits -- where a burnt deadline
                // costs a retry rather than a wrong decision.
                _ctx.StepStartTicks += tickDelta;
                return;
            }
        }

        switch (_state)
        {
            case State.SelectStage:
            case State.SelectObjective:
                SelectNextObjective();
                break;

            case State.RunStep:
                RunStep();
                break;
        }
    }

    private void SelectNextObjective()
    {
        // Retire the books-driven witness the moment the weapon it was about reaches Animus: the
        // enhancement it was guarding has happened. Without this it would outlive its weapon, and
        // since a repeat relic on the SAME job carries the SAME Atma item id, the next relic's fresh
        // Atma would inherit "its books are done" and be marched to Jalzahn with nine books still to
        // do. Checked here rather than at the upgrade site so a manual enhancement clears it too.
        if (AnimusWrapWeaponId != 0 && (int)GameState.EquippedRelicStage() >= (int)RelicStage.Animus)
        {
            DebugLog.Info("The Atma -> Animus enhancement is done; clearing the books-driven marker " +
                          "so the next relic on this job grinds its own books.");
            AnimusWrapWeaponId = 0;
        }

        // Manual duty override: if the player hand-queued a relic duty and is standing inside
        // it, run THAT objective now, regardless of the normal stage/sequence order. This lets
        // a book dungeon (or a relic trial) be entered manually and cleared on the spot instead
        // of only running the book's dungeons in their generated order.
        if (TrySelectCurrentDutyObjective())
            return;

        var activeNote = GameState.ActiveRelicNoteId();

        // If a relic note (Trials of the Braves book) is equipped, focus strictly on
        // that book's objectives WHILE it still has incomplete entries. This stops the
        // engine from picking earlier-stage sample objectives (e.g. Atma, whose
        // item-count looks "incomplete" because the Atmas were already consumed).
        //
        // Once that book is finished, the player has completed Animus, so advance to
        // the later stages (Novus and beyond) instead of stopping. The relic note id
        // remains reported as active even after the book is done, so without this the
        // pool would be empty and the run would halt at the start of Novus.
        // Incomplete AND actually due: a base-relic objective gated to a quest sequence is only a
        // candidate once ITS OWN job's quest has reached that sequence (BaseRelicState.IsSequenceEligible).
        // Applied here, before either selection mode, so the gate holds outside the base-relic branch
        // too -- that hole is what let a finished relic wander into another job's line.
        var incomplete = _objectives
            .Where(o => !IsObjectiveComplete(o) && BaseRelicState.IsSequenceEligible(o))
            .ToList();
        List<RelicObjective> pool;

        if (_ctx.Config.StageMode == StageSelectionMode.Manual)
        {
            // Manual: pin work to the user-inserted stage. This is what lets a stage
            // that was already passed be revisited -- for example farming more Atma,
            // Alexandrite (Novus), or Light (Nexus/Zeta) -- instead of the engine
            // always advancing to the lowest incomplete stage. Animus still requires
            // the matching book to be equipped, so keep that guard for that stage.
            var stage = _ctx.Config.ManualStage;
            pool = incomplete.Where(o => o.Stage == stage).ToList();
            if (stage == RelicStage.Animus && activeNote != 0)
            {
                var book = pool
                    .Where(o => RelicNoteBound(o.Completion.Kind) && o.Book == activeNote)
                    .ToList();
                if (book.Count > 0)
                    pool = book;
            }
            DebugLog.Verbose($"Manual stage selection: {stage} ({pool.Count} incomplete objectives)");
        }
        else
        {
            // Auto: the lowest-incomplete behaviour, but bounded below by the equipped
            // relic weapon's upgrade tier. The weapon is the authoritative record of
            // progress, so every stage at or below the tier it proves complete is
            // dropped from selection. Without this, a farmable lower stage whose
            // completion is a re-armable inventory count -- the Novus Alexandrite farm
            // never reads "complete" once its Alexandrite is consumed -- parks the
            // engine there even for a player already holding a Nexus (or later) weapon.
            // That is the "Nexus seen as Novus" symptom. Manual mode intentionally skips
            // this so a passed stage can still be revisited to farm more.
            // The equipped read, or -- while a step has deliberately taken the weapon OFF for a
            // turn-in it could not otherwise make (the Jalzahn trades, the sequence-14 hand-over) --
            // the tier it noted before doing so. Without that stand-in the live read is None for the
            // length of the trip, which means "no progress at all" here and re-opens finished stages.
            var completedStage = Steps.RelicStageMemo.EffectiveEquippedStage();
            // A relic sitting UNEQUIPPED -- in the armoury or a bag -- reads as None from the
            // equipped-slot scan, and None means "no progress at all" to the filter below, which then
            // mis-routes into work the character has already finished: other jobs' base relics are
            // Stage=Relic and sort ahead of everything.
            //
            // That window is not an edge case, it is what every stage transition looks like. Each
            // upgrade -- and the base relic's own final turn-in -- hands the new weapon back
            // unequipped. Reported live: finishing the Artemis Bow on Bard, and the run immediately
            // showing a Monk objective and going to buy a second quenching oil, because for those few
            // seconds the engine could not see that any relic existed.
            //
            // So the floor comes from the highest relic held ANYWHERE, not just an equipped one (and
            // not just a Zenith, which is all this used to look for -- a bare finished base relic,
            // exactly what the line hands you at the end, was invisible to it). Only consulted when
            // nothing is equipped, so a worn higher-tier weapon is never downgraded by a spare.
            if (completedStage == RelicStage.None)
                completedStage = GameState.HighestHeldRelicStage();
            var advanced = completedStage == RelicStage.None
                ? incomplete
                : incomplete.Where(o => (int)o.Stage > (int)completedStage).ToList();

            if (DebugLog.On && completedStage != RelicStage.None)
                DebugLog.Verbose($"Equipped relic proves completion through {completedStage}; " +
                                 $"{advanced.Count} of {incomplete.Count} incomplete objective(s) remain above it");

            pool = advanced;

            // Base-relic gate: the Atma+ stages upgrade an EXISTING relic, so the engine
            // must not advance into them while the equipped job's base-relic quest is
            // still in progress (sequence > 0) or has never been finished. This is keyed
            // on the per-job quest "A Relic Reborn (<weapon>)", NOT on the equipped weapon
            // -- an unfinished relic equipped for the Part 5 beastmen hunt makes
            // EquippedRelicStage read "Relic", which previously let the engine wrongly
            // jump to Atma. The Relic pool is taken from the full incomplete set (not
            // 'advanced') for the same reason. Relic-stage automation is a later pass, so
            // until those objectives exist this stops with guidance.
            if (BaseRelicState.ShouldWorkBaseRelic())
            {
                var activeJob = BaseRelicState.ActiveRelicJob();
                var liveSeq = BaseRelicState.RelicQuestSequenceFor(activeJob);

                // Every generated Relic objective carries a real Job, and the selection filter is
                // `o.Job == activeJob` -- so an unresolved job does not degrade, it empties the pool
                // outright and the run stops on the generic "no selectable objective / the trial
                // duties did not resolve" dump, which points at the wrong thing entirely. Say what
                // actually happened, and name the raw ClassJob the game reported so the cause is in
                // the log rather than inferred. Reported live: a Summoner sitting at sequence 4 read
                // as "Unknown" and could not select the Chimera.
                if (activeJob == RelicJob.None)
                {
                    var rawJob = BaseRelicState.ActiveClassJobId();
                    var jobName = GameState.ClassJobName(rawJob);
                    StopWith($"A base-relic quest is active (sequence {liveSeq}) but the job could not be " +
                             $"determined: the game reports ClassJob {rawJob}" +
                             (jobName.Length > 0 ? $" ({jobName})" : string.Empty) +
                             ". Every relic objective belongs to a specific job, so nothing can be selected. " +
                             "If you are on Arcanist, equip your Summoner or Scholar soul crystal; otherwise " +
                             "switch to the job whose relic you are building, then /relic start.");
                    return;
                }

                // Drop the in-memory "ran this step" markers when the game's quest sequence
                // advances, so the next quest-path step becomes eligible.
                if (liveSeq != _lastPathSeq)
                {
                    _pathDone.Clear();
                    _lastPathSeq = liveSeq;
                    _acceptStalledSince = 0; // sequence progressed; reset the accept watchdog
                }

                // Guards for the start-of-line accept (live sequence 0). A FINISHED relic quest also
                // reads sequence 0 (QuestManager returns 0 for a completed quest), and the line may not
                // be acceptable yet -- neither is distinguishable from an unstarted relic by the sequence
                // alone, and the generated seq-0 accept objective would otherwise be selected and run
                // (teleport to Gerolt + interact) into an idle. Both surfaced by adversarial review.
                if (liveSeq == 0 && activeJob != RelicJob.None)
                {
                    // (a) The ACTIVE job's own base relic is already complete. ShouldWorkBaseRelic can be
                    //     true here purely from ANOTHER job's active quest (its cross-job signal), so
                    //     there is no start work for this job -- do not re-accept a done quest.
                    if (BaseRelicState.IsBaseRelicObtained(activeJob))
                    {
                        StopWith($"{RelicJobs.DisplayName(activeJob)}'s base relic is already complete; no " +
                                 "start-of-line work for this job. Switch to the job whose relic you want to " +
                                 "progress, then /relic start.");
                        return;
                    }
                    // (b) The line is not unlocked, so "A Relic Reborn" cannot be accepted. Stop with
                    //     guidance instead of talking to Gerolt with no quest to accept and idling.
                    if (!BaseRelicState.RelicLineUnlocked())
                    {
                        StopWith("Cannot start this relic: the Zodiac line is not unlocked yet. Complete " +
                                      "'The Weaponsmith of Legend' (Nedrick Ironheart, Vesper Bay) -- and the ARR " +
                                      "finale 'The Ultimate Weapon' -- then equip the class and /relic start.");
                        return;
                    }
                }

                // 1) A quest-path (sequence-driven) step mapped for the CURRENT sequence
                //    takes precedence -- the engine follows the game step by step.
                var pathStep = _objectives.FirstOrDefault(o =>
                    o.Stage == RelicStage.Relic && o.ActiveAtSequence == liveSeq
                    && (o.Job == RelicJob.None || o.Job == activeJob));

                if (pathStep != null)
                {
                    if (_pathDone.Contains(pathStep.Id))
                    {
                        // Watchdog for the accept (sequence 0): the accept completes on Gerolt's dialogue
                        // closing, not on a quest being accepted, so if the sequence has not advanced past
                        // 0 a while after we ran it, the accept did not take (a prerequisite unmet in a way
                        // guard (b) did not catch, e.g. the class's level-50 job quest). Stop with guidance
                        // rather than idle at Gerolt forever.
                        if (liveSeq == 0)
                        {
                            if (_acceptStalledSince == 0)
                                _acceptStalledSince = Environment.TickCount64;
                            else if (Environment.TickCount64 - _acceptStalledSince > AcceptStallMs)
                            {
                                StopWith("Interacted with Gerolt but 'A Relic Reborn' was not accepted (the quest " +
                                              "sequence did not advance). Ensure the line is unlocked ('The Weaponsmith " +
                                              "of Legend') and your class's level-50 job quest is complete, then /relic start.");
                                return;
                            }
                        }
                        return; // ran this step; wait for the quest sequence to advance
                    }
                    DebugLog.Info($"Base relic ({RelicJobs.DisplayName(activeJob)}): sequence {liveSeq} -> '{pathStep.DisplayName}'");
                    pool = new List<RelicObjective> { pathStep };
                }
                else
                {
                    // 2) No path step for this sequence -> the generated hunt/trial
                    //    objectives (beastmen, primal trials), selected by the normal order.
                    //    These are the non-sequence (ActiveAtSequence < 0) objectives; a
                    //    quest path, when present, only supplements them at its mapped
                    //    sequences (accept, walk, turn-ins), so the two coexist.
                    var genPool = incomplete
                        .Where(o => o.Stage == RelicStage.Relic && o.ActiveAtSequence < 0
                                    && (o.Job == RelicJob.None || o.Job == activeJob)
                                    // Lower-bound gate: a trial only runs once the live quest sequence
                                    // has reached its step, so a later trial (e.g. the Hydra at seq 12)
                                    // is never selected while an earlier turn-in (the seq-11 report to
                                    // Gerolt) is still pending. 0 = no lower bound.
                                    && (o.ActiveFromSequence == 0 || liveSeq >= o.ActiveFromSequence))
                        .ToList();
                    if (genPool.Count == 0)
                    {
                        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                        var relicForJob = _objectives
                            .Where(o => o.Stage == RelicStage.Relic
                                        && (o.Job == RelicJob.None || o.Job == activeJob))
                            .ToList();
                        var generated = relicForJob.Where(o => o.ActiveAtSequence < 0).ToList();

                        // Part 2, the class weapon (delivered at sequence 3): the quest wants a
                        // job-specific weapon with two Grade III materia melded onto it. Buying/crafting
                        // it and melding cannot be automated, so name exactly what is needed rather than
                        // falling through to the generic "not active until sequence N" message --
                        // this is the step the Chimera was previously running ahead of.
                        if (Data.ClassWeaponSteps.IsWindow(liveSeq)
                            && Data.ClassWeaponSteps.For(activeJob) is { } classWeapon)
                        {
                            StopWith($"Base relic (sequence {liveSeq}): the quest wants the melded class " +
                                          $"weapon -- {classWeapon.Annotation}. Buy or craft it, meld the " +
                                          $"{classWeapon.MateriaCount} materia at a materia melder, then hand it to " +
                                          "Gerolt. Open /relic for the step: the weapon and materia there search an " +
                                          "open market board on click, with a travel button to the Limsa Lominsa " +
                                          "board and an Artisan crafting list for the weapon and its pre-crafts.");
                            return;
                        }

                        // A trial that is incomplete but GATED to a later sequence than the live one
                        // means the quest is sitting at a step the engine does not automate -- almost
                        // always a Gerolt turn-in / item delivery between trials (report the beastman
                        // hunt at seq 11, report the Hydra at 13, hand the weapon over at 14, deliver
                        // the primal drops at 18, or the oil at 19). Stop with that guidance rather than
                        // the generic "no objective" dump, and never run the later trial early.
                        var gatedNext = generated
                            .Where(o => !IsObjectiveComplete(o) && o.ActiveFromSequence > liveSeq)
                            .OrderBy(o => o.ActiveFromSequence)
                            .FirstOrDefault();
                        if (gatedNext != null)
                        {
                            StopWith($"Base relic ({RelicJobs.DisplayName(activeJob)}): at sequence {liveSeq}, " +
                                          $"the next automated step ('{gatedNext.DisplayName}') is not active until " +
                                          $"sequence {gatedNext.ActiveFromSequence}. Advance the quest first -- report to " +
                                          "Gerolt at Hyrstmill (or deliver the requested item), then /relic start. The " +
                                          "between-trial turn-ins are not yet automated.");
                            return;
                        }

                        // Expected end state: every combat/duty objective is done and only the
                        // un-automated tail remains (the oil purchase + final turn-in). Say that
                        // plainly instead of dumping the diagnostic.
                        if (generated.Count > 0 && generated.All(IsObjectiveComplete))
                        {
                            StopWith($"Base relic ({RelicJobs.DisplayName(activeJob)}): all automatable " +
                                          $"content is complete (sequence {liveSeq}, Relicable {ver}). Follow the " +
                                          "quest journal to finish: buy Radz-at-Han Quenching Oil from Auriana " +
                                          "(15 Allagan tomestones of poetics, Revenant's Toll) and turn in to " +
                                          "Gerolt. The oil exchange and final turn-in are not yet automated.");
                            return;
                        }

                        // Otherwise something is genuinely missing: dump every Relic objective for
                        // the job so the cause is visible (never generated, vs marked complete, vs
                        // filtered by sequence). The build version rules out a stale DLL; absent
                        // p06-p09 rows mean the trial duties did not resolve.
                        DebugLog.Warn($"On the base-relic quest for {RelicJobs.DisplayName(activeJob)} " +
                                      $"(sequence {liveSeq}); no selectable objective. Relicable {ver}; " +
                                      $"{relicForJob.Count} Relic objective(s) for this job:");
                        foreach (var o in relicForJob)
                            DebugLog.Warn($"  [{(IsObjectiveComplete(o) ? "done" : "open")}] {o.Id} " +
                                          $"activeAt={o.ActiveAtSequence} completeAt={o.CompleteAtSequence} " +
                                          $"oneTimeDuty={o.OneTimeDutyContentId}");
                        StopWith("If no p06-p09 trial rows are listed, the trial duties did not resolve " +
                                 "(name/case); otherwise they are being marked complete. Run /relic prereq.");
                        return;
                    }
                    pool = genPool;
                }
            }
            // The book branch only owns selection when the equipped weapon is at least the Atma
            // tier: Trials of the Braves books are Animus-stage work done ON an Atma weapon. The
            // game keeps the LAST bought Relic Note active forever (it only changes on a G'Jusana
            // purchase), so a repeat relic still at the Zenith step carries the previous relic's
            // completed book -- that stale note must not capture the run and stop it ("Relic Note
            // 9 is complete but the weapon is still Relic"); the Atma-stage gate below owns the
            // pre-Atma weapon instead.
            else if (activeNote != 0 && (int)completedStage >= (int)RelicStage.Atma)
            {
                var book = advanced
                    .Where(o => RelicNoteBound(o.Completion.Kind) && o.Book == activeNote)
                    .ToList();
                if (book.Count > 0)
                {
                    pool = book;
                    // Mark that THIS Atma weapon is actively running its books, so when the run later
                    // reaches a complete last-book note, the no-next-book branch recognizes the books
                    // were just driven (stop for the manual Animus enhancement) rather than wrapping.
                    AnimusWrapWeaponId = GameState.EquippedRelicItemId();
                }
                else if ((int)completedStage >= (int)RelicStage.Animus)
                {
                    // The book's entries are all complete AND the equipped weapon proves Animus is
                    // done (an Animus weapon or later), so genuinely advance to the later stages.
                    DebugLog.Info($"Relic note {activeNote} has no incomplete entries; advancing past Animus");
                    pool = advanced.Where(o => o.Stage > RelicStage.Animus).ToList();
                }
                else
                {
                    // Fail-safe against a silently-skipped book slot. "No incomplete generated
                    // objective" only proves the book is done if the game's own RelicNote memory
                    // agrees. A slot Relicable could not generate (e.g. a dungeon whose TerritoryType
                    // did not resolve) is invisible to the pool yet still incomplete in memory, so
                    // buying the next book would just fail at G'Jusana ("the Relic Note did not
                    // advance"). Stop with the exact incomplete slots instead of that doomed purchase.
                    var incompleteSlots = GameState.IncompleteActiveBookSlots();
                    if (incompleteSlots.Count > 0)
                    {
                        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                        StopWith($"Relic Note {activeNote} has no objective the engine can run, but the game's " +
                                 $"book memory still shows incomplete slot(s): {string.Join(", ", incompleteSlots)}. " +
                                 "The book is NOT finished, so the next book cannot be bought yet. The usual cause is a " +
                                 "book slot Relicable could not generate (a dungeon whose TerritoryType did not resolve); " +
                                 $"update Relicable ({ver}) or clear those slots manually from the Trials of the Braves " +
                                 "book, then /relic start.");
                        return;
                    }

                    // The book is complete but the weapon is still pre-Animus (Atma), so more Trials
                    // of the Braves books remain for THIS relic. Auto-advance: buy the next book from
                    // G'Jusana in Mor Dhona (it becomes the active Relic Note on purchase), then keep
                    // filling it.
                    var (nextBook, _) = Data.AnimusBookData.NextBook(activeNote);
                    if (nextBook != 0 && Data.AnimusBookData.GJusanaNpcId != 0)
                    {
                        DebugLog.Info($"Relic Note {activeNote} finished; auto-buying the next book ({nextBook}) from G'Jusana.");
                        pool = new List<RelicObjective> { BuildBuyBookObjective(activeNote, nextBook) };
                        // This weapon is advancing through its books (see the fill case) -- so a later
                        // complete last-book note is "books done", not a fresh stale note to wrap.
                        AnimusWrapWeaponId = GameState.EquippedRelicItemId();
                    }
                    else
                    {
                        // No next book row: activeNote is the LAST book (9), complete. Two realities share
                        // this state: (a) a REPEAT relic's fresh Atma weapon inherited the PREVIOUS relic's
                        // stale note (the note never clears on its own) and must buy book 1 to start ITS
                        // own run; (b) this weapon just finished its own 9 books and the Atma -> Animus
                        // enhancement remains.
                        //
                        // ORDER BY THE COST OF BEING WRONG, because nothing in game memory separates them:
                        //
                        //   guess (b), wrong -> Jalzahn does not offer the enhancement, the flow fails,
                        //                       we learn it from the game and buy book 1. Cost: a teleport.
                        //   guess (a), wrong -> 500 poetics and all nine books again. Cost: hours.
                        //
                        // So (b) goes first, always, and the expensive one is only taken once the game
                        // ITSELF has told us the books are not done (_animusUpgradeRefusedWeaponId, set
                        // when the enhancement flow gives up on this weapon -- see RunStep).
                        //
                        // It used to be the other way round, keyed off the Animus stage quest ("you have
                        // finished Animus before, so this note is a leftover"). That is not a
                        // discriminator at all: per AnimusUpgradeExecutor, quest 66972 completes EARLY in
                        // the stage rather than at the enhancement, so it reads true for anyone past
                        // their first book -- first relic included. It sent freshly-finished book runs
                        // back to G'Jusana to start over.
                        var equippedId = GameState.EquippedRelicItemId();
                        var (firstBook, _) = Data.AnimusBookData.NextBook(0);
                        var refused = equippedId != 0 && equippedId == _animusUpgradeRefusedWeaponId;

                        if (refused && firstBook != 0 && Data.AnimusBookData.GJusanaNpcId != 0)
                        {
                            // Warn, not Info: this spends 500 poetics and restarts a nine-book grind, so
                            // it must be visible without the debug toggle -- but by now it is a
                            // CONCLUSION rather than a guess, because the enhancement was tried and the
                            // game declined it.
                            DebugLog.Warn($"Jalzahn would not enhance Atma weapon {equippedId}, so the complete note " +
                                          $"{activeNote} it carries belongs to a PREVIOUS relic. Starting this weapon's " +
                                          "own Trials of the Braves run (buy book 1) from G'Jusana.");
                            pool = new List<RelicObjective> { BuildBuyBookObjective(activeNote, firstBook) };
                        }
                        else
                        {
                            // The last book is complete on an Atma weapon and the game has not told us
                            // otherwise, so the remaining Animus work is the Atma -> Animus enhancement at
                            // Jalzahn ("Relic Weapon Atma Enhancement"). AnimusUpgradeExecutor unequips the
                            // Atma weapon (it must be unequipped to list in the turn-in menu; the same job is
                            // kept), drives the menu, and re-equips the resulting Animus weapon. Selected
                            // explicitly (not via the normal pool) because a stale complete note leaves the
                            // book-1..8 objectives reading incomplete against it, which would otherwise
                            // capture selection.
                            var upgrade = _objectives.FirstOrDefault(o =>
                                o.Completion.Kind == CompletionKind.AnimusUpgraded && !IsObjectiveComplete(o));
                            if (upgrade != null)
                            {
                                _animusUpgradeTriedWeaponId = equippedId;
                                DebugLog.Info($"Relic Note {activeNote} (the last book) is complete on Atma weapon " +
                                              $"{equippedId}; running the Atma -> Animus enhancement at Jalzahn. " +
                                              (AnimusWrapWeaponId == equippedId && equippedId != 0
                                                  ? "These are this weapon's own books."
                                                  : "There is no record of this weapon driving these books, so if Jalzahn " +
                                                    "declines they belong to a previous relic and book 1 is bought instead."));
                                pool = new List<RelicObjective> { upgrade };
                            }
                            else
                            {
                                StopWith($"Relic Note {activeNote} (the last book) is complete, but the animus-upgrade " +
                                              "objective did not load (run /relic reload). Perform the 'Relic Weapon Atma " +
                                              "Enhancement' at Jalzahn (Fallgourd Float, North Shroud) manually, then /relic start.");
                                return;
                            }
                        }
                    }
                }
            }
            else if (completedStage == RelicStage.Atma && activeNote == 0)
            {
                // Atma weapon equipped but NO Trials of the Braves book yet -> the Animus stage begins
                // by buying the FIRST book from G'Jusana (Mor Dhona); the book is granted and becomes
                // the active Relic Note on purchase, then the book work fills it. Same auto-buy
                // machinery as the next-book path above, with completedBook 0 (BuyRelicBookExecutor
                // reads the live note itself and buys NextBook(0) = book 1); BuildBuyBookObjective(0)
                // completes when the active Relic Note advances past 0.
                var (firstBook, _) = Data.AnimusBookData.NextBook(0);
                if (firstBook != 0 && Data.AnimusBookData.GJusanaNpcId != 0)
                {
                    DebugLog.Info("Atma weapon equipped, no book yet: auto-buying the first Trials of the Braves book from G'Jusana (Mor Dhona).");
                    pool = new List<RelicObjective> { BuildBuyBookObjective(0, firstBook) };
                    // This weapon is starting its book run -> a later complete last-book note is "books
                    // done" (manual Animus enhancement), not a fresh stale note to wrap.
                    AnimusWrapWeaponId = GameState.EquippedRelicItemId();
                }
                else
                {
                    StopWith("Atma weapon equipped: buy the first Trials of the Braves book from G'Jusana in Mor " +
                                  "Dhona and equip it, then /relic start. (Auto-buy could not resolve G'Jusana or the first book row.)");
                    return;
                }
            }

            // Atma-stage gate: a Relic-tier weapon past its base quest is a Zenith (or a bare
            // relic awaiting the Furnace), and the ONLY valid work is the Atma stage -- the
            // 12-zone atma FATE farm, then the Zenith -> Atma enhancement at Jalzahn. Without
            // this bound the pool would fall through to the re-armable later stages (the Novus
            // Alexandrite farm never reads complete), the mirror of the Braves gate below. The
            // atma-upgrade objective sorts after the farms (KindPriority), and only becomes
            // selectable once every farm objective's atma is held (all 12), so the order is
            // farm -> upgrade without an explicit count check here. Under the CBT backend the
            // per-tick delegation guard still parks the engine while CBT farms; at 12/12 that
            // delegation ends and this gate hands the upgrade objective to the engine.
            if (completedStage == RelicStage.Relic && !BaseRelicState.ShouldWorkBaseRelic())
            {
                // Zenith gate: the equipped weapon is a finished bare base relic, so the ONLY valid
                // work is its Furnace trade -- and that is automated now (buy the Thavnairian Mist
                // at Auriana if it is not already held, then trade at the Furnace beside Gerolt),
                // so select that objective rather than stopping with guidance. It is authored at
                // the Atma stage because Auto filters the pool to stages ABOVE the completed one
                // and a bare base relic already reads RelicStage.Relic; its ZenithTraded priority
                // sorts it ahead of the 12 atma farms, which cannot progress until it is done.
                if (BaseRelicState.EquippedNeedsZenith())
                {
                    var zenith = pool.FirstOrDefault(o => o.Completion.Kind == CompletionKind.ZenithTraded);
                    if (zenith != null)
                    {
                        pool = new List<RelicObjective> { zenith };
                    }
                    else
                    {
                        StopWith("Base relic done, but the equipped weapon has not been Zenith-upgraded yet, " +
                                      "and the zenith-upgrade objective is not loadable (run /relic reload). Trade " +
                                      "it + 3x Thavnairian Mist at the Furnace beside Gerolt (Hyrstmill, North " +
                                      "Shroud) -- see the main window's Zenith guidance -- then /relic start.");
                        return;
                    }
                }
                else
                {
                    // Zenith already done: the normal Atma-stage work (the 12-zone FATE farm, then
                    // the Zenith -> Atma enhancement at Jalzahn). The zenith-upgrade objective is
                    // dropped here so a completed trade cannot re-select it.
                    var atmaPool = pool
                        .Where(o => o.Stage == RelicStage.Atma && o.Completion.Kind != CompletionKind.ZenithTraded)
                        .ToList();
                    if (atmaPool.Count > 0)
                    {
                        pool = atmaPool;
                    }
                    else
                    {
                        StopWith("Zenith equipped but no Atma-stage objective is loadable (the atma farm/upgrade " +
                                      "data files are missing?). Run /relic reload, or farm the 12 atmas and perform " +
                                      "the 'Relic Weapon Zenith Enhancement' at Jalzahn (Hyrstmill) manually.");
                        return;
                    }
                }
            }

            // Braves (il125) gate: the Nexus -> Zodiac Braves upgrade ("Wherefore Art Thou,
            // Zodiac" plus its four quests and a second set of Trials of the Braves books)
            // is a manual stage this engine does not yet automate. A player whose equipped
            // weapon proves Nexus complete but not Braves (no il125 weapon) therefore has no
            // automated work -- stop with guidance rather than fall through to the Zeta
            // (Mahatma) farm, which cannot progress without the il125 weapon. The
            // pool.Any(Braves) check makes this self-disable once Braves objectives exist.
            // Braves (il125): run the dungeons the accepted material quest(s) are CURRENTLY requesting
            // (each Braves objective is tagged with its quest and the step[s] that request its drop). The
            // four quests can be active at once and done in any order, so eligibility is per-objective
            // (IsBravesDungeonEligible), not a single active quest. Otherwise stop with guidance. The
            // dungeons run via AutoDuty and complete when their drop (a Key Item) is in the bag.
            if (completedStage == RelicStage.Nexus)
            {
                // Take EVERY outstanding stage quest before touching a dungeon. The four material
                // quests run simultaneously and each one adds its own dungeons, so accepting them all
                // up front maximises what is runnable and saves trickling back to Mor Dhona between
                // batches. It also closes an ordering trap that made this worse than "lazy": the moment
                // one material quest is accepted at a sequence that requests a dungeon, that dungeon
                // fills the pool and the accept branch is never reached again -- so with the accept
                // check last, "A Treasured Mother" (last in the order) was never picked up at all.
                if (TrySelectBravesAccept() is { } accept)
                {
                    DebugLog.Info($"Braves: {accept.DisplayName}.");
                    pool = new List<RelicObjective> { accept };
                }
                else if (TrySelectBravesFetch() is { } fetch)
                {
                    // Pull the quest materials you already own off your retainers before the dungeons,
                    // so the report step is not reached only to stop on items that were parked all along.
                    DebugLog.Info($"Braves: {fetch.DisplayName}.");
                    pool = new List<RelicObjective> { fetch };
                }
                else
                {
                    var bravesPool = pool
                        .Where(o => o.Stage == RelicStage.Braves && IsBravesDungeonEligible(o))
                        .ToList();
                    if (bravesPool.Count > 0)
                    {
                        pool = bravesPool;
                    }
                    else if (TrySelectBravesReport() is { } report)
                    {
                        // No dungeon eligible, but a quest has obtained a batch and is waiting for the
                        // NPC report/turn-in -> do that (teleport, interact, hand over) to advance it.
                        DebugLog.Info($"Braves: no dungeon requested; {report.DisplayName}.");
                        pool = new List<RelicObjective> { report };
                    }
                    else
                    {
                        StopWith(AnyBravesMaterialQuestAccepted()
                            ? "Braves: no dungeon item is being requested right now across your accepted material " +
                              "quest(s) -- the current step's drops are obtained, or the next batch is not requested " +
                              "yet. Gather the vendor/crafted items and turn in what you have; the engine resumes when " +
                              "a quest asks for the next dungeon items." + BravesReportGuidance() + BravesAcceptBlockedGuidance()
                            : "Nexus complete, but no Braves quest could be accepted automatically. Take " +
                              $"'{Data.BravesData.QuestZodiac}' from Jalzahn (Hyrstmill, North Shroud), then the four " +
                              "material quests (A Ponze of Flesh, Labor of Love, Method in His Malice, A Treasured " +
                              "Mother -- all four can be active at once), and the engine will run whichever dungeons " +
                              "are being requested." + BravesAcceptBlockedGuidance());
                        return;
                    }
                }
            }
        }

        // Braves (il125) safety net: only run a dungeon whose OWN material quest is accepted AND is
        // currently requesting the drop, regardless of how 'pool' was built above. The Auto/Nexus gate
        // already applies this, but the Manual branch (pinned to Braves) and an Auto path that skipped
        // the gate (no recognized relic equipped) would otherwise leave all 16 dungeons across all four
        // quests eligible -- the "random dungeons not in any quest" symptom. Per-objective, so the four
        // quests can be worked simultaneously.
        //
        // ENTRY IS GATED ON HOLDING THE END ITEM, not on the pool. The old test -- "does the pool
        // contain a quest-tagged Braves objective?" -- was documented as a no-op for every other
        // stage, and that was simply false. Pool membership only means INCOMPLETE, and a Braves
        // dungeon objective completes on KeyItemCount: the drop being absent from your key items.
        // That is true for everyone who has not reached the stage, and true again afterwards because
        // the turn-in consumes the drops. So all 16 objectives sat in the pool for an Animus- or
        // Novus-tier weapon, this block ran, and TrySelectBravesAccept sent the run off to accept
        // Braves quests two stages early -- or, on a first relic, burned four cross-zone trips per
        // Start on quests the giver cannot offer. Asking whether the stage's OUTPUT already exists
        // answers both: it is the one signal neither the repeatable quests nor the consumed
        // materials can give.
        if (BravesStageWanted(pool))
        {
            // Outstanding stage quests come first here too (the Auto/Nexus gate above already did this;
            // this covers Manual pinned to Braves and an Auto path that skipped the gate). Without it a
            // dungeon that became eligible from the FIRST accepted quest preempts the rest forever.
            if (TrySelectBravesAccept() is { } pending)
            {
                DebugLog.Info($"Braves: {pending.DisplayName}.");
                pool = new List<RelicObjective> { pending };
            }
            else if (TrySelectBravesFetch() is { } pendingFetch)
            {
                DebugLog.Info($"Braves: {pendingFetch.DisplayName}.");
                pool = new List<RelicObjective> { pendingFetch };
            }
            else
            {
                var bravesFiltered = pool
                    .Where(o => o.Stage != RelicStage.Braves || IsBravesDungeonEligible(o))
                    .ToList();
                if (bravesFiltered.Count == 0)
                {
                    // No dungeon eligible -> a delivery is due; report/turn in (mirrors the Auto/Nexus
                    // gate above so Manual-pinned-to-Braves, and an Auto path that skipped that gate, also
                    // hand over the vendor/crafted/drop batch instead of stopping with the items in hand).
                    if (TrySelectBravesReport() is { } report)
                    {
                        DebugLog.Info($"Braves: no dungeon requested; {report.DisplayName}.");
                        pool = new List<RelicObjective> { report };
                    }
                    else
                    {
                        StopWith(AnyBravesMaterialQuestAccepted()
                            ? "Braves: no dungeon item is being requested right now across your accepted material " +
                              "quest(s) (the current step's drops are obtained, or the next batch is not requested yet). " +
                              "Turn in what you have; the engine resumes when a quest asks for the next dungeon items." +
                              BravesReportGuidance() + BravesAcceptBlockedGuidance()
                            : "Braves: no stage quest could be accepted automatically. Take " +
                              $"'{Data.BravesData.QuestZodiac}' from Jalzahn (Hyrstmill, North Shroud), then the four " +
                              "material quests, and the engine will run whichever dungeons are being requested." +
                              BravesAcceptBlockedGuidance());
                        return;
                    }
                }
                else
                {
                    pool = bravesFiltered;
                }
            }
        }

        // Diagnostic: show what the engine considers incomplete for the active book,
        // so a "why is it doing the FATE" can be traced (e.g. monster slots reading
        // as already complete).
        if (DebugLog.On)
        {
            var counts = string.Join(", ", pool
                .GroupBy(o => o.Completion.Kind)
                .Select(g => $"{g.Key}={g.Count()}"));
            DebugLog.Info($"SelectObjective: activeNote={activeNote}, totalObjectives={_objectives.Count}, incomplete[{counts}]");
        }

        // Manual book work: drop the kinds the user has unticked. Applied to book slots ONLY --
        // every other objective (upgrades, the atma farm, gauge farms, the base relic) is not a
        // "kind" the user is choosing between, and unticking Dungeons must not strand the run by
        // also hiding, say, the Jalzahn enhancement. If the filter would empty the pool the run
        // would just stop with no explanation, so it is reported and skipped instead.
        pool = ApplyBookKindFilter(pool);

        // "Run next": a slot the user clicked in the main window jumps the queue once. Checked
        // before the opportunistic FATE grab, because an explicit click should beat a heuristic.
        if (TakeForcedObjective(pool) is { } forced)
        {
            if (!EquippedRelicOk(forced, activeNote))
                return; // guard stopped the run with guidance
            _objective = forced;
            _ctx.CurrentObjective = forced;
            _stepIndex = 0;
            DebugLog.Info($"Objective selected (Run next, user-picked): {forced.Stage} '{forced.DisplayName}'");
            BeginStep();
            _state = State.RunStep;
            return;
        }

        // Opportunistic co-located FATE: if a book FATE is up right now in the zone we are standing
        // in -- or in a zone where we also have enemy work -- do it NOW rather than deferring it to
        // last. One teleport covers both, and the FATE will not be up later. Overrides the
        // FATE-last ordering below.
        if (FindCoLocatedActiveFate(pool) is { } coFate)
        {
            if (!EquippedRelicOk(coFate, activeNote))
                return; // guard stopped the run with guidance
            _objective = coFate;
            _ctx.CurrentObjective = coFate;
            _stepIndex = 0;
            DebugLog.Info($"Objective selected (co-located FATE, saves a teleport): {coFate.Stage} '{coFate.DisplayName}'");
            BeginStep();
            _state = State.RunStep;
            return;
        }

        // Authored Atma/Books run order: enemies, then leves, then dungeons, then FATEs.
        // FATEs are gated on the FATE actually being active, so they go last (both here
        // and across books), then by slot.
        _objective = pool
            // FATE book slots are gated on the FATE actually being active, so they are the
            // lowest priority: do every other kind of book work first (across all books)
            // and only fall to FATEs when nothing else remains. Without this a book's FATE
            // could be selected while its FATE is inactive, stranding the run waiting at
            // the FATE instead of doing an available enemy or leve.
            .OrderBy(o => o.Completion.Kind == CompletionKind.FateSlot ? 1 : 0)
            .ThenBy(o => (int)o.Stage)
            .ThenBy(o => o.Book)
            .ThenBy(o => KindPriority(o.Completion.Kind))
            // Round-robin among a book's FATEs: the least-recently-tried FATE goes first
            // so rotating off a dead FATE picks a different one. Non-FATE objectives are
            // never stamped, so this is a no-op (0) for them and does not disturb their
            // enemy/leve/dungeon order above.
            .ThenBy(o => o.Completion.Kind == CompletionKind.FateSlot
                ? _fateCheckedTick.GetValueOrDefault(o.Id, 0L) : 0L)
            .ThenBy(o => o.Completion.Slot)
            .ThenBy(o => o.Id, StringComparer.Ordinal) // deterministic among equal-rank objectives
            .FirstOrDefault();

        if (_objective == null)
        {
            // Nothing left: the relic line is finished.
            Stop();
            return;
        }

        // Equipped-relic guard: a relic-note objective only progresses with its
        // matching weapon equipped, so refuse to grind uselessly.
        if (!EquippedRelicOk(_objective, activeNote))
            return;

        _ctx.CurrentObjective = _objective;
        _stepIndex = 0;
        DebugLog.Info($"Objective selected: {_objective.Stage} '{_objective.DisplayName}' ({_objective.Steps.Count} steps)");
        BeginStep();
        _state = State.RunStep;
    }

    // Manual duty override. When the player is standing inside a duty instance, check whether
    // that exact zone is the target of an incomplete relic objective's EnterDuty step (a book
    // dungeon, a base-relic trial, or a Braves dungeon -- everything that hands a specific
    // TerritoryType to AutoDuty). If so, select and run it now, out of the normal order, so a
    // manually-queued relic dungeon is verified and cleared "like usual". The gauge farms carry
    // TerritoryType 0 on their step (the farm zone comes from config), so they never match here.
    //
    // Returns true when it handled selection (either started the objective, or the equipped-relic
    // guard stopped the run with guidance) -- the caller then returns without normal selection.
    // Returns false only when the current zone is not a relic duty we know, so the normal
    // stage/sequence selection runs as before (including when not in a duty at all).
    private bool TrySelectCurrentDutyObjective()
    {
        if (!InInstance())
            return false;

        var territory = (uint)Plugin.ClientState.TerritoryType;
        if (territory == 0)
            return false;

        var candidates = _objectives
            .Where(o => !IsObjectiveComplete(o)
                        && o.Steps.Any(s => s.Type == StepType.EnterDuty && s.TerritoryType == territory))
            .ToList();
        if (candidates.Count == 0)
        {
            // We ARE inside a duty, but no incomplete relic objective targets this exact zone.
            // Log the live territory against every known duty objective's territory so a
            // mismatch (the generator normalised to a different instance id, or the slot already
            // reads complete) is visible, then fall through to normal selection.
            if (DebugLog.On)
            {
                var known = string.Join(", ", _objectives
                    .SelectMany(o => o.Steps
                        .Where(s => s.Type == StepType.EnterDuty && s.TerritoryType != 0)
                        .Select(s => $"{o.DisplayName}->T{s.TerritoryType}{(IsObjectiveComplete(o) ? "(done)" : "")}")));
                DebugLog.Info($"Manual duty override: inside duty TerritoryType {territory}, but no incomplete " +
                              $"relic-duty objective targets it. Known duty objectives: [{known}]");
            }
            return false; // not a relic duty we have an objective for -> fall through to normal selection
        }

        // A dungeon can appear in more than one book's slots; prefer the objective for the
        // currently-equipped book so the credit lands, else take the first match.
        var activeNote = GameState.ActiveRelicNoteId();
        var match = candidates.FirstOrDefault(o => activeNote != 0 && o.Book == activeNote)
                    ?? candidates[0];

        // The book-dungeon / trial credit still needs the matching relic equipped; keep the guard
        // (it stops with actionable guidance when the wrong or no relic is equipped).
        if (!EquippedRelicOk(match, activeNote))
            return true; // guard already stopped the run; do not fall through to normal selection

        _objective = match;
        _ctx.CurrentObjective = match;
        _stepIndex = 0;
        DebugLog.Info($"Manual duty override: inside TerritoryType {territory}; running relic duty '{match.DisplayName}' out of order.");
        BeginStep();
        _state = State.RunStep;
        return true;
    }

    // In an instanced duty (any of the three bound-by-duty flags AutoDuty/EnterDuty also check).
    private static bool InInstance()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    private static bool RelicNoteBound(CompletionKind kind)
        => kind is CompletionKind.MonsterSlot or CompletionKind.DungeonSlot
            or CompletionKind.FateSlot or CompletionKind.LeveSlot;

    // A Braves dungeon objective is eligible when its OWN material quest is accepted AND that quest is
    // currently requesting this drop. The four material quests can be active SIMULTANEOUSLY and done in
    // ANY ORDER (per the FFXIV wiki), so eligibility is keyed per-objective on its BravesQuest -- not on
    // a single "the active quest" -- letting the engine run whichever dungeons are requested across every
    // accepted quest. The material quests batch their dungeon items across turn-in steps, so a drop only
    // drops while its quest sits at its step; ActiveAtQuestSequences holds those step(s). Empty until
    // calibrated (/relic bravesseq): then "accepted" alone gates it (the pre-calibration behaviour). A
    // non-Braves / untagged objective is unaffected (returns true).
    private static bool IsBravesDungeonEligible(RelicObjective o)
    {
        if (o.Stage != RelicStage.Braves || string.IsNullOrEmpty(o.BravesQuest))
            return true;
        // Accepting a quest is not dungeon work -- it runs precisely BECAUSE the quest is not accepted
        // yet, so the accepted-and-requesting test below would filter out the one objective that fixes
        // that. (This filter is a safety net against running dungeons no quest asked for; an accept
        // trip is neither a dungeon nor unrequested.)
        if (o.Completion.Kind == CompletionKind.BravesQuestAccepted)
            return true;
        var seq = GameState.QuestSequence(Data.BravesData.MaterialQuestId(o.BravesQuest));
        if (seq <= 0)
            return false; // this drop's material quest is not accepted
        return o.ActiveAtQuestSequences.Count == 0 || o.ActiveAtQuestSequences.Contains(seq);
    }

    // Should the Braves stage be offered work at all?
    //
    // Two questions, in this order:
    //   1. Is there Braves work in the pool to gate? (cheap; keeps this a no-op for other stages)
    //   2. Do we already HOLD the stage's end item, RelicTargetCount times over?
    //
    // (2) is the one that stops the run re-accepting a quest it just finished. It is deliberately a
    // question about the weapon and not about the quests, because the four material quests are
    // repeatable -- a finished one reports sequence 0, indistinguishable from never having taken it
    // -- and the materials are consumed at turn-in, so neither can testify that the stage is done.
    // A Braves (or Zeta) weapon in hand can, and it survives a plugin reload, a relog, and a job
    // change, which an in-memory latch would not.
    //
    // RepeatCompletedStages turns the count off for players deliberately building another, and is
    // also the way out if the count cannot see a finished weapon (parked in a retainer or the
    // glamour dresser rather than the bags/armoury).
    private bool BravesStageWanted(IReadOnlyList<RelicObjective> pool)
        => pool.Any(o => o.Stage == RelicStage.Braves && !string.IsNullOrEmpty(o.BravesQuest))
           && BravesStageReached()
           && !BravesStageSatisfied();

    // Has the weapon actually got AS FAR AS the Braves upgrade?
    //
    // Neither of the other two gates asks this, and between them they leave a hole that swallows
    // four whole stages. Pool membership only means INCOMPLETE, and a Braves dungeon objective is
    // incomplete whenever its drop is absent from your key items -- true for everyone below the
    // stage. BravesStageSatisfied then asks whether the stage's END ITEM exists, but that is a
    // count across every weapon you OWN, which is the wrong question for someone building a
    // SECOND relic: with one finished Zodiac Braves (or Zeta) weapon and "Relics to build" at 2,
    // held(1) < target(2), so it answers "not satisfied" at every stage of the new weapon.
    //
    // Both being true at Atma, this block took the pool over and ran Braves dungeons -- and
    // because it REPLACES the pool rather than adding to it, Atma, Animus, Novus and Nexus were
    // skipped outright. Reported live: a Summoner on Atma sent repeatedly through the Tam-Tara
    // Deepcroft (a drop for 'A Treasured Mother', a quest four stages ahead of the weapon).
    //
    // The EQUIPPED weapon is the authority here, deliberately, and not the held count the
    // end-item test uses: the Nexus -> Braves upgrade operates on the weapon in your hands, while
    // a held count sees the finished relic parked in the armoury and concludes the wrong thing.
    private bool BravesStageReached()
    {
        // Manual mode is an explicit instruction to work a stage -- revisiting one is the entire
        // point of it -- so it is honoured rather than second-guessed.
        if (_ctx.Config.StageMode == StageSelectionMode.Manual)
            return _ctx.Config.ManualStage == RelicStage.Braves;

        var equipped = Steps.RelicStageMemo.EffectiveEquippedStage();
        // Nothing recognizable in hand is the case this whole block exists to cover, so it stays
        // permissive there rather than stranding a player whose relic is briefly off (a Jalzahn
        // trade already keeps its tier alive through RelicStageMemo, so this really does mean
        // "no relic at all").
        return equipped == RelicStage.None || equipped >= RelicStage.Nexus;
    }

    // The end-item test on its own. Every path that could start Braves work asks this -- the entry
    // gate above AND TrySelectBravesAccept/TrySelectBravesFetch -- because the Nexus branch reaches
    // those directly, and a guard on one route only is how the re-accept survived in the first place.
    private bool BravesStageSatisfied()
    {
        if (_ctx.Config.RepeatCompletedStages)
            return false;

        var target = Math.Max(1, _ctx.Config.RelicTargetCount);
        var held = GameState.HeldRelicCountAtOrAbove(RelicStage.Braves);
        if (held < target)
            return false;

        // Edge-triggered: this is reached on every selection pass once the stage is done, and an
        // unconditional line here would fill the log with the same sentence forever.
        if (!_bravesDoneLogged)
        {
            _bravesDoneLogged = true;
            DebugLog.Info($"Braves: {held} finished relic weapon(s) held and the target is {target}; " +
                          "the stage's quests are done and will not be taken again. Raise 'Relics to " +
                          "build' or tick 'Repeat completed stages' to run it again.");
        }
        return true;
    }

    private bool _bravesDoneLogged;

    // True when at least one Braves material quest is currently accepted (drives the guidance wording:
    // "accept a quest" vs "turn in what you have and the next dungeon step will open").
    private static bool AnyBravesMaterialQuestAccepted()
    {
        foreach (var name in Data.BravesData.MaterialQuests)
            if (GameState.QuestSequence(Data.BravesData.MaterialQuestId(name)) > 0)
                return true;
        return false;
    }

    // "Who / where to report to" for each accepted material quest, appended to the "no dungeon
    // requested" guidance so the player knows exactly which NPC advances/turns in each quest (its
    // batch is done or the next batch needs its non-dungeon items first). Empty when none accepted.
    private static string BravesReportGuidance()
    {
        var parts = new List<string>();
        foreach (var name in Data.BravesData.MaterialQuests)
        {
            var seq = GameState.QuestSequence(Data.BravesData.MaterialQuestId(name));
            if (seq <= 0)
                continue; // only quests currently accepted
            // Sequence-aware: who advances a quest depends on where it is (A Treasured Mother reports
            // to Ealdwine at Swiftperch between batches, Brangwine only for the final turn-in).
            var (npc, _, _, _, where) = Data.BravesData.TurnInNpc(name, seq);
            if (!string.IsNullOrEmpty(npc))
                parts.Add($"{name} -> {npc} ({where})");
        }
        return parts.Count == 0
            ? string.Empty
            : " Report to / turn in at: " + string.Join("; ", parts) + ".";
    }

    // A Braves material quest that is accepted AND holds an obtained dungeon drop not yet handed over is
    // ready for its NPC report/turn-in (which advances it to the next batch). Returns a synthetic report
    // objective for the first such quest, or null. Only reached when no dungeon is eligible.
    // Braves stage quests this run tried to accept and could not (the giver did not offer it). Skipped
    // on later passes so the stage keeps running its other work; cleared by Start.
    private readonly HashSet<string> _bravesAcceptFailed = new(StringComparer.OrdinalIgnoreCase);

    // Whether the Braves retainer fetch has already had its one trip this run. Cleared by Start.
    private bool _bravesFetchTried;

    // Names any stage quest that is NOT in hand and cannot be, so a stop that looks like "the engine
    // ignored a quest" says what is actually blocking it. Empty when nothing is blocked.
    private string BravesAcceptBlockedGuidance()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var quest in Data.BravesData.AcceptOrder)
        {
            if (Steps.BravesAcceptExecutor.IsInHand(quest))
                continue;
            var prereq = Data.BravesData.Prerequisite(quest);
            if (prereq.QuestId != 0 && !GameState.IsQuestComplete(prereq.QuestId))
                sb.Append($"\n'{quest}' is not offered yet: it is gated behind the sidequest " +
                          $"'{prereq.Name}' from {prereq.Npc} ({prereq.Where}). Complete that and " +
                          "Relicable will pick the quest up on the next Start.");
            else if (_bravesAcceptFailed.Contains(quest))
                sb.Append($"\n'{quest}' could not be accepted (its giver did not offer it); accept it yourself, " +
                          "then /relic start.");
        }
        return sb.ToString();
    }

    // A retainer trip for the Braves quest materials, when any is short in the bags AND the last bell
    // scan saw it on a retainer -- or null when there is nothing to pull. The vendor/crafted pieces
    // (Bombard Core, Sacred Spring Water, the "Perfect ..." crafts) are bought or crafted rather than
    // farmed, so they habitually sit on a retainer; without this the run reached the report step and
    // stopped with "gather the vendor/crafted items" for items the player already owned.
    //
    // Requires AutoWithdrawFromRetainers: with it off the fetch engine only REPORTS what to drag out,
    // which an unattended run cannot act on, so Wanted() returns nothing and this never selects.
    private RelicObjective? TrySelectBravesFetch()
    {
        // No point pulling Braves materials off the retainers for a stage that is finished -- that
        // was a wasted summoning-bell trip every run.
        if (BravesStageSatisfied())
            return null;

        // ONE trip per run. The objective's completion re-reads the live plan, so a material the bell
        // scan sees but the retrieve cannot land (full bags, a stale snapshot) would otherwise leave it
        // incomplete forever and the run would fly to the bell on a loop.
        if (_bravesFetchTried)
            return null;
        var want = Steps.FetchBravesMaterialsExecutor.Wanted(_ctx);
        if (want.Count == 0)
            return null;
        _bravesFetchTried = true;
        return new RelicObjective
        {
            Stage = RelicStage.Braves,
            Id = "braves-fetch-materials",
            DisplayName = $"Fetch {want.Count} Braves material(s) from your retainers",
            Steps = new List<StepData> { new() { Type = StepType.FetchBravesMaterials } },
            Completion = new CompletionCondition { Kind = CompletionKind.BravesMaterialsFetched },
        };
    }

    // The next Braves stage quest that is not in hand, as a travel-and-accept objective -- or null when
    // all five are. Order matters: the umbrella ("Wherefore Art Thou, Zodiac", from Jalzahn) must be
    // taken first because until it is, the four material quests are not offered at all. The four are
    // then accepted in turn; they may all be active at once, and each one accepted immediately makes
    // its dungeons eligible, so the run flows straight from here into real work.
    private RelicObjective? TrySelectBravesAccept()
    {
        // Already holding the end item -> the stage is done and its quests are never taken again.
        // Here as well as at the entry gate because the Nexus branch calls this directly.
        if (BravesStageSatisfied())
            return null;

        foreach (var quest in Data.BravesData.AcceptOrder)
        {
            if (Steps.BravesAcceptExecutor.IsInHand(quest))
                continue;
            // Gated behind another quest (only A Treasured Mother is, behind "One Man's Trash"): the
            // giver would have nothing to offer, so skip it and keep working the others. The stop
            // guidance names it rather than letting it look like the engine forgot the quest.
            var prereq = Data.BravesData.Prerequisite(quest);
            if (prereq.QuestId != 0 && !GameState.IsQuestComplete(prereq.QuestId))
                continue;
            // Already tried this run and the giver did not offer it. Skipping stops one un-offerable
            // quest from burning the failure backoff and halting a stage that still has real work.
            if (_bravesAcceptFailed.Contains(quest))
                continue;
            var giver = Data.BravesData.QuestGiver(quest);
            if (giver.DataId == 0)
                continue; // giver did not resolve; try the next quest rather than stall on this one
            return new RelicObjective
            {
                Stage = RelicStage.Braves,
                Id = $"braves-accept-{quest}",
                DisplayName = $"Accept '{quest}' ({giver.Npc}, {giver.Where}) -- click to travel there",
                BravesQuest = quest,
                Territory = giver.Territory,
                // The giver's spot is carried on the step purely so the main window's objective name is
                // click-to-travel (FirstAuthoredSpot); the executor resolves the NPC by data id itself.
                Steps = new List<StepData> { new() { Type = StepType.AcceptBravesQuest, Position = giver.Pos } },
                Completion = new CompletionCondition { Kind = CompletionKind.BravesQuestAccepted },
            };
        }
        return null;
    }

    private RelicObjective? TrySelectBravesReport()
    {
        foreach (var name in Data.BravesData.MaterialQuests)
        {
            var seq = GameState.QuestSequence(Data.BravesData.MaterialQuestId(name));
            if (seq <= 0)
                continue; // not accepted
            if (Data.BravesData.TurnInNpc(name, seq).DataId == 0)
                continue; // NPC did not resolve
            // Report whenever the quest has no dungeon drop left to FARM at its current sequence -- i.e.
            // it is sitting at a DELIVERY step. That covers handing over an obtained dungeon batch AND the
            // VENDOR / CRAFTED / seals delivery steps (step 1 and the final step), which hold no dungeon
            // drop. The previous "only when a drop is held" test skipped those, so a player who had
            // gathered everything stalled with "no dungeon requested" instead of turning in (the reported
            // bug). The report executor hands over exactly what the game's Request window enables and
            // fails with guidance if items are genuinely missing, so firing it here when a delivery is due
            // is safe.
            if (!QuestNeedsDungeonNow(name, seq))
                return BuildBravesReportObjective(name, seq);
        }
        return null;
    }

    // True when the quest, at its current sequence, still has a dungeon drop to OBTAIN (requested at this
    // sequence and not yet held) -- i.e. there is farming to do before the next report. Mirrors
    // IsBravesDungeonEligible's calibration test (RequestedAtSequences); an uncalibrated drop counts as
    // requested whenever the quest is accepted (the pre-calibration behaviour).
    private static bool QuestNeedsDungeonNow(string quest, int seq)
    {
        foreach (var m in Data.BravesData.Materials)
        {
            if (m.Source != Data.BravesSource.DungeonDrop || m.Quest != quest)
                continue;
            if (m.RequestedAtSequences.Count > 0 && !m.RequestedAtSequences.Contains(seq))
                continue; // this drop is not requested at the current sequence
            var keyId = Data.BravesData.KeyItemId(m.ItemName);
            if (keyId != 0 && GameState.KeyItemCount(keyId) == 0)
                return true; // a requested drop is not yet held -> farm it before reporting
        }
        return false;
    }

    // Synthetic per-quest report objective (one BravesReport step). The Id carries the live sequence so
    // the AllStepsDone marker records only THIS report; once it advances the quest (a new sequence) a
    // fresh objective is built. The executor itself drives completion (the quest sequence changing).
    private static RelicObjective BuildBravesReportObjective(string quest, int seq)
        => new()
        {
            Stage = RelicStage.Braves,
            BravesQuest = quest,
            Id = $"braves-report-{quest}-{seq}",
            DisplayName = $"Braves ({quest}): report to {Data.BravesData.TurnInNpc(quest, seq).Npc} to advance the quest",
            Steps = new List<StepData> { new() { Type = StepType.BravesReport } },
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        };

    // When debug logging is on, log each time the ACTIVE Braves material quest changes sequence, so
    // the per-drop RequestedAtSequences numbers can be read off simply by playing the quest.
    // Edge-triggered (a given quest+sequence logs once). Runs regardless of whether automation ran.
    private void LogBravesSequenceChange()
    {
        foreach (var name in Data.BravesData.MaterialQuests)
        {
            var qid = Data.BravesData.MaterialQuestId(name);
            if (qid == 0)
                continue;
            var seq = GameState.QuestSequence(qid);
            if (seq <= 0)
                continue;
            if (_lastBravesSeqLogged.quest == qid && _lastBravesSeqLogged.seq == seq)
                return; // already logged this quest+sequence
            _lastBravesSeqLogged = (qid, seq);
            DebugLog.Info($"Braves calibration: quest '{name}' now at sequence {seq}. If the journal is " +
                          $"asking for a dungeon item, that drop's RequestedAtSequences = {seq}. " +
                          "Run /relic bravesseq for the full held-drop readout.");
            return;
        }
    }

    // A book FATE from the pool that is worth doing NOW instead of last: it is currently active
    // (Running), has more than FateMinRemainingSeconds left, and is in the same overworld zone as
    // an incomplete enemy (monster) objective we would teleport to anyway. Returns null when the
    // feature is off, there is no such FATE, or there is no same-zone enemy work to piggyback on.
    private RelicObjective? FindCoLocatedActiveFate(IReadOnlyList<RelicObjective> pool)
    {
        if (!_ctx.Config.PreferCoLocatedFates)
            return null;

        // 1) A book FATE live in the zone we are STANDING IN. This outranks everything, including
        //    the enemy-work pairing below, because it is the only case that costs no travel at all:
        //    we are already there. It is also the fix for "a FATE was up in my zone and it
        //    teleported away anyway" -- the ordering further down is purely by kind and book, so
        //    without this the engine would happily fly to another zone's enemy slot while a FATE
        //    that will NOT be up later burned down behind it.
        var here = Plugin.ClientState.TerritoryType;
        if (here != 0 && FindActiveFateInZone(pool, here) is { } inZone)
        {
            DebugLog.Info($"FATE '{inZone.DisplayName}' is up in the zone we are already in; " +
                          "taking it before travelling elsewhere.");
            return inZone;
        }

        // 2) Zones where we still have enemy (monster) work in this pool: one teleport covers both.
        var enemyZones = new HashSet<uint>();
        foreach (var o in pool)
            if (o.Completion.Kind == CompletionKind.MonsterSlot && o.Territory != 0)
                enemyZones.Add(o.Territory);
        if (enemyZones.Count == 0)
            return null;

        foreach (var zone in enemyZones)
            if (FindActiveFateInZone(pool, zone) is { } coFate)
            {
                DebugLog.Info($"Co-located FATE '{coFate.DisplayName}' is up in a zone with enemy work; " +
                              "grabbing it to save a teleport.");
                return coFate;
            }
        return null;
    }

    // The first objective in `pool` that is a book FATE slot in `territory` whose FATE is actually
    // running with enough time left to reach and clear it. Shared by both arms above so "is this
    // FATE worth diverting to" is decided in exactly one place.
    private static RelicObjective? FindActiveFateInZone(IReadOnlyList<RelicObjective> pool, uint territory)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var o in pool)
        {
            if (o.Completion.Kind != CompletionKind.FateSlot || o.Territory != territory)
                continue;
            var fateId = FateIdOf(o);
            if (fateId == 0)
                continue;
            // Read the live FATE: only redirect if it is actually up with enough time left.
            if (Steps.Fates.ById((ushort)fateId) is not { State: FateState.Running } fate)
                continue;
            // A FATE at 100% is OVER -- it just has not flipped out of Running yet. There is no credit
            // left to earn there, so diverting to it is always wasted, and because this arm bypasses the
            // _fateCheckedTick round-robin it would re-pick the very FATE the executor just rotated off
            // (the observed pair of back-to-back "already finished on arrival" rotations).
            if (fate.Progress >= 100)
                continue;
            if (fate.StartTimeEpoch + fate.Duration - now <= FateMinRemainingSeconds)
                continue;
            return o;
        }
        return null;
    }

    // ---- Manual book work (main window > Book work) ----

    // The ACTIVE book's incomplete slots, for the main window's pick-a-slot list. Filtered to the
    // live RelicNote so the list matches the book actually in hand, and ordered by kind then slot
    // so it reads like the book page rather than in engine-internal order.
    //
    // Deliberately NOT kind-filtered: the list is how a user reaches a slot whose kind is
    // currently unticked, which is the whole point of being able to force one.
    //
    // Cached briefly because this is called from the ImGui draw loop, i.e. every frame the main
    // window is open, and it evaluates IsObjectiveComplete (a live game-state read) for every
    // loaded objective. A book slot cannot change faster than the player can clear one, so a
    // quarter-second of staleness is invisible and the per-frame cost stops mattering.
    private IReadOnlyList<RelicObjective> _bookSlotCache = System.Array.Empty<RelicObjective>();
    private long _bookSlotCacheTick;
    private const long BookSlotCacheMs = 250;

    public IReadOnlyList<RelicObjective> IncompleteBookSlots()
    {
        if (Environment.TickCount64 - _bookSlotCacheTick < BookSlotCacheMs)
            return _bookSlotCache;
        _bookSlotCacheTick = Environment.TickCount64;
        _bookSlotCache = BuildIncompleteBookSlots();
        return _bookSlotCache;
    }

    private IReadOnlyList<RelicObjective> BuildIncompleteBookSlots()
    {
        var activeNote = GameState.ActiveRelicNoteId();
        if (activeNote == 0)
            return System.Array.Empty<RelicObjective>();
        return _objectives
            .Where(o => BookKindOf(o.Completion.Kind) != null
                        && o.Book == activeNote
                        && !IsObjectiveComplete(o))
            .OrderBy(o => KindPriority(o.Completion.Kind))
            .ThenBy(o => o.Completion.Slot)
            .ToList();
    }

    // The slot the user asked to run next, consumed on the next selection. Not persisted: it is a
    // one-shot "do this one now", not a setting.
    private string? _forcedObjectiveId;

    // Queue a specific objective to be selected next, ahead of the normal order. Ignored silently
    // if the id is no longer in the pool by the time selection runs (it completed, the book
    // advanced, or the stage filter moved) -- a stale click must never wedge the engine.
    public void RunObjectiveNow(string objectiveId)
    {
        _forcedObjectiveId = objectiveId;
        DebugLog.Info($"Run next: queued objective '{objectiveId}'.");
        Replan();
    }

    public bool HasForcedObjective => _forcedObjectiveId != null;

    public void ClearForcedObjective() => _forcedObjectiveId = null;

    private RelicObjective? TakeForcedObjective(IReadOnlyList<RelicObjective> pool)
    {
        if (_forcedObjectiveId == null)
            return null;
        var match = pool.FirstOrDefault(o => o.Id == _forcedObjectiveId);
        // Consumed either way: a request that no longer applies is dropped rather than retried
        // forever against a pool it will never match.
        if (match == null)
            DebugLog.Info($"Run next: '{_forcedObjectiveId}' is no longer available; falling back to normal order.");
        _forcedObjectiveId = null;
        return match;
    }

    // The user-selectable kind of a book slot, or null for everything else in the pool.
    private static BookWorkKinds? BookKindOf(CompletionKind kind) => kind switch
    {
        CompletionKind.MonsterSlot => BookWorkKinds.Enemies,
        CompletionKind.LeveSlot => BookWorkKinds.Leves,
        CompletionKind.DungeonSlot => BookWorkKinds.Dungeons,
        CompletionKind.FateSlot => BookWorkKinds.Fates,
        _ => null,
    };

    // Manual mode: keep only the book-slot kinds the user ticked. Non-book objectives always pass.
    private List<RelicObjective> ApplyBookKindFilter(List<RelicObjective> pool)
    {
        if (_ctx.Config.BookWorkMode != BookWorkSelectionMode.Manual)
            return pool;

        var allowed = _ctx.Config.BookWorkKinds;
        var filtered = pool
            .Where(o => BookKindOf(o.Completion.Kind) is not { } k || (allowed & k) != 0)
            .ToList();

        // Everything left was a book slot of an unticked kind. Filtering to empty would stop the
        // run with "the relic line is finished", which is both wrong and baffling, so say what
        // actually happened and let the unfiltered pool through.
        if (filtered.Count == 0 && pool.Count > 0)
        {
            DebugLog.Warn("Book work is set to Manual but every remaining objective is a kind you have " +
                          "unticked, so the filter was ignored for this pick. Tick more kinds in the main " +
                          "window (Book work), or switch back to Auto.");
            return pool;
        }

        if (DebugLog.On && filtered.Count != pool.Count)
            DebugLog.Verbose($"Book work Manual ({allowed}): {pool.Count - filtered.Count} objective(s) filtered out.");
        return filtered;
    }

    // The FATE row id an Animus FATE objective participates in (from its ParticipateFate step).
    private static uint FateIdOf(RelicObjective o)
    {
        foreach (var s in o.Steps)
            if (s.Type == StepType.ParticipateFate && s.FateId != 0)
                return s.FateId;
        return 0;
    }

    // A synthetic Animus objective that buys `targetBook` from G'Jusana once `completedBook` is
    // finished. One BuyRelicBook step; completes when the active RelicNote is no longer the
    // finished book (RelicNoteAdvanced), which is game-state driven so it is never persisted (a
    // later relic re-buys its own books). The target is passed separately because a repeat
    // relic WRAPS from the last book back to book 1 rather than always buying completedBook+1.
    private static RelicObjective BuildBuyBookObjective(byte completedBook, uint targetBook)
        => new()
        {
            Stage = RelicStage.Animus,
            Book = completedBook,
            Id = $"animus-buybook-{targetBook}",
            DisplayName = $"Buy Trials of the Braves book {targetBook} (G'Jusana, Mor Dhona)",
            Steps = new List<StepData> { new() { Type = StepType.BuyRelicBook } },
            Completion = new CompletionCondition { Kind = CompletionKind.RelicNoteAdvanced, Book = completedBook },
        };

    // Selection priority within a book: enemies, then leves, then dungeons, with FATEs
    // last (spawn-gated). This is the authored Atma/Books order. The Jalzahn Atma upgrade
    // sorts after everything else in its stage so the 12-zone atma farms always run first
    // (the farm ids do not all alphabetize before "atma-upgrade", so the Id tiebreak alone
    // cannot guarantee that -- e.g. "atma-western-thanalan" sorts after it).
    private static int KindPriority(CompletionKind kind) => kind switch
    {
        CompletionKind.MonsterSlot => 0, // enemies
        CompletionKind.LeveSlot => 1,
        CompletionKind.DungeonSlot => 2,
        CompletionKind.FateSlot => 3,
        // The Zenith trade gates the whole Atma stage (the atma FATE farm needs the Zenith weapon
        // equipped), so it sorts ahead of the 12 atma farms (ItemCount = 4) it shares a stage with.
        CompletionKind.ZenithTraded => 3,
        CompletionKind.AtmaUpgraded => 5, // after the atma farms (ItemCount = 4)
        // After all book slots (0..3): the Atma -> Animus enhancement runs once the books are done.
        CompletionKind.AnimusUpgraded => 5,
        // After the Alexandrite farm (AlexandriteCount = 4) and the melding route (SphereScrollFull =
        // 4): the Animus -> Novus enhancement is the last thing in the Novus stage, so the scroll is
        // always full before the trip to Jalzahn is even considered.
        CompletionKind.NovusUpgraded => 5,
        _ => 4,
    };

    // Verifies the correct relic weapon is equipped for an objective. Stops the
    // run with an actionable warning rather than silently making no progress.
    private bool EquippedRelicOk(RelicObjective o, byte activeNote)
    {
        if (RelicNoteBound(o.Completion.Kind))
        {
            if (activeNote == 0)
            {
                StopWith("No relic note is active. Equip the relic weapon for this step, then /relic start.");
                return false;
            }
            if (o.Book != 0 && activeNote != o.Book)
            {
                StopWith($"Wrong relic equipped: active book {activeNote}, this step needs book {o.Book}. Equip the matching relic.");
                return false;
            }
        }

        if (o.RequiredWeaponItemId != 0 && GameState.EquippedRelicItemId() != o.RequiredWeaponItemId)
        {
            StopWith($"Wrong weapon equipped: need item {o.RequiredWeaponItemId}, have {GameState.EquippedRelicItemId()}.");
            return false;
        }

        // The Zeta (Mahatma) farm charges the equipped il125 Zodiac Braves weapon. If a
        // Zeta objective is selected without that weapon equipped, there is no gauge to
        // fill, so the farm could only loop a duty that fills nothing. Stop with guidance.
        // The not-yet-Braves case (still on a Nexus weapon) is handled earlier by the
        // Braves gate in SelectNextObjective; this catches an unequipped or wrong-job
        // il125 weapon once the player has actually reached the Braves stage.
        if (o.Completion.Kind == CompletionKind.MahatmaGauge && !GameState.HasBravesRelicEquipped())
        {
            StopWith("The Zeta (Mahatma) farm needs the il125 Zodiac Braves weapon equipped. " +
                          "Equip it (or finish the Braves stage to obtain it first), then /relic start.");
            return false;
        }

        return true;
    }

    private void RunStep()
    {
        if (_objective == null || _activeExecutor == null)
        {
            _state = State.SelectObjective;
            return;
        }

        var step = _objective.Steps[_stepIndex];
        var status = _activeExecutor.Update(step, _ctx);

        switch (status)
        {
            case ExecutorStatus.InProgress:
                return;

            case ExecutorStatus.Rotate:
                // The objective is not doable right now (a book FATE that has not
                // spawned), but this is NOT a failure: move on to a different objective
                // and leave the failure backoff untouched. Stamp the FATE so selection
                // round-robins to the next incomplete book FATE instead of re-picking
                // this one; it becomes eligible again once every other FATE is tried.
                _activeExecutor.Stop(_ctx);
                _fateCheckedTick[_objective.Id] = Environment.TickCount64;
                DebugLog.Verbose($"Rotating off '{_objective.DisplayName}'; re-selecting");
                _state = State.SelectObjective;
                return;

            case ExecutorStatus.Complete:
                DebugLog.Verbose($"Step {_stepIndex + 1}/{_objective.Steps.Count} ({step.Type}) complete");
                _activeExecutor.Stop(_ctx);
                _consecutiveFailures = 0; // real progress; clear the backoff counter
                _lastFailedObjectiveId = string.Empty;
                _stepIndex++;
                if (_stepIndex >= _objective.Steps.Count)
                {
                    // Steps exhausted. For procedural objectives (no game-memory
                    // flag) record completion so they are not re-selected.
                    if (_objective.Completion.Kind == CompletionKind.AllStepsDone)
                    {
                        if (_objective.ActiveAtSequence >= 0)
                        {
                            // Sequence-driven (quest path): mark done in-memory only, so the
                            // engine waits for the game's quest sequence to advance instead
                            // of re-running this step. Not persisted, so a repeated relic
                            // re-runs the path.
                            _pathDone.Add(_objective.Id);
                        }
                        else if (_objective.Stage == RelicStage.Relic)
                        {
                            // Base-relic generated objective (beastmen / trial): in-memory
                            // only. Running a step (e.g. queuing a trial) is NOT proof the
                            // quest credited it, so persisting completion would mark the part
                            // done forever and halt the next launch. The in-session flag only
                            // stops it being re-selected within this run; the quest sequence
                            // is the real completion authority (IsAllStepsComplete).
                            _relicRan.Add(_objective.Id);
                        }
                        else
                        {
                            _proceduralDone.Add(_objective.Id);
                            if (!_ctx.Config.CompletedProceduralObjectives.Contains(_objective.Id))
                                _ctx.Config.CompletedProceduralObjectives.Add(_objective.Id);
                        }
                    }
                    _state = State.SelectObjective;
                }
                else
                {
                    BeginStep();
                }
                return;

            case ExecutorStatus.Failed:
                _activeExecutor.Stop(_ctx);

                // A failed accept means the giver did not offer that quest. Remember it so the next
                // pass moves on to the stage's other work instead of re-flying to the same NPC until
                // the 3-strike backoff halts a run that still had dungeons to do.
                if (_objective.Completion.Kind == CompletionKind.BravesQuestAccepted
                    && !string.IsNullOrEmpty(_objective.BravesQuest))
                    _bravesAcceptFailed.Add(_objective.BravesQuest);

                // A failed Atma -> Animus enhancement is EVIDENCE, not just a failure. We send the run
                // to Jalzahn first precisely because he is the only one who can say whether the
                // complete note on this weapon is its own or a previous relic's leftover -- so when he
                // will not do it, that is the answer, and the next selection buys book 1 instead of
                // re-trying forever and stopping on the 3-strike backoff with nothing learned.
                // Tolerated twice first, because his turn-in submenu is a SEAM and one menu hiccup
                // must not cost 500 poetics and nine books.
                if (_objective.Completion.Kind == CompletionKind.AnimusUpgraded
                    && _animusUpgradeTriedWeaponId != 0)
                {
                    _animusUpgradeFailures++;
                    if (_animusUpgradeFailures >= AnimusUpgradeFailuresBeforeBookRun)
                    {
                        _animusUpgradeRefusedWeaponId = _animusUpgradeTriedWeaponId;
                        DebugLog.Warn($"The Atma -> Animus enhancement failed {_animusUpgradeFailures} times for weapon " +
                                      $"{_animusUpgradeTriedWeaponId}; taking that as Jalzahn declining it, so the " +
                                      "complete Relic Note belongs to a previous relic. Buying book 1 to start this " +
                                      "weapon's own Trials of the Braves run. If that is wrong -- the books really are " +
                                      "done and something else broke -- stop the run and check the menu lines logged above.");
                    }
                }

                // Count consecutive failures of the same objective; stop after a few
                // instead of re-selecting it forever (the Novus "stall").
                if (_objective.Id == _lastFailedObjectiveId)
                    _consecutiveFailures++;
                else
                {
                    _lastFailedObjectiveId = _objective.Id;
                    _consecutiveFailures = 1;
                }

                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    StopWith($"'{_objective.DisplayName}' failed {_consecutiveFailures}x; stopping to avoid a loop. " +
                             "For Novus melding, infuse from the /relic novus window (the controller does not meld unless auto-meld is on).");
                    return;
                }

                DebugLog.Warn($"Step {_stepIndex + 1}/{_objective.Steps.Count} ({step.Type}) failed; re-planning ({_consecutiveFailures}/{MaxConsecutiveFailures})");
                _state = State.SelectObjective;
                return;
        }
    }

    // The aetheryte the built-in Atma farm teleports to for a given atma item id, read off that
    // zone's own objective (its AetheryteTeleport step), so the tracker's click-to-teleport and the
    // farm always agree on where a zone starts. 0 when the atma is unknown or its data file is not
    // loaded. Cheap: twelve objectives with two steps each, called only while the tracker is drawn.
    public uint AetheryteForAtma(uint atmaItemId)
    {
        if (atmaItemId == 0)
            return 0;
        foreach (var o in _objectives)
        {
            if (o.Stage != RelicStage.Atma
                || o.Completion.Kind != CompletionKind.ItemCount
                || o.Completion.ItemId != atmaItemId)
                continue;
            foreach (var s in o.Steps)
                if (s.Type == StepType.AetheryteTeleport && s.AetheryteId != 0)
                    return s.AetheryteId;
        }
        return 0;
    }

    // How many of this atma-farm objective's atma must be held before the zone is done and the run
    // moves to the next one. The authored JSON threshold is 1 (one atma per zone is all the Zenith ->
    // Atma enhancement consumes); Config.AtmaPerZone raises it for a player banking spare sets for
    // repeat relics, since every extra atma of a zone can only be farmed IN that zone. Never below the
    // authored threshold, so a data file that asks for more still gets it.
    private int AtmaTarget(RelicObjective o)
        => Math.Max(o.Completion.Threshold, Math.Max(1, _ctx.Config.AtmaPerZone));

    private void BeginStep()
    {
        var step = _objective!.Steps[_stepIndex];
        if (!_executors.TryGetValue(step.Type, out var exec))
            throw new InvalidOperationException($"No executor registered for {step.Type}");

        _activeExecutor = exec;
        _ctx.StepStartTicks = Environment.TickCount64;

        // Book-FATE wait window for this attempt: on the FIRST pass through a book's FATEs (this one
        // has not rotated off yet this run, so it is not in _fateCheckedTick) only glance briefly and
        // move to the next in order; on later passes wait the full configured window (default 120s)
        // for it to spawn. Left at 0 for non-FATE steps and the Atma "any FATE" mode (which ignores it
        // and never rotates), so the executor falls back to Config.FateRotateSeconds there.
        _ctx.FateWaitSeconds = step.Type == StepType.ParticipateFate && step.FateId != 0
            ? (_fateCheckedTick.ContainsKey(_objective.Id) ? _ctx.Config.FateRotateSeconds : FirstPassFateCheckSeconds)
            : 0;

        DebugLog.Verbose($"Begin step {_stepIndex + 1}/{_objective.Steps.Count}: {step.Type}");
        exec.Start(step, _ctx);
    }

    // Authoritative completion check, never a proxy event (DESIGN.md 5.4).
    // Slot kinds read RelicNote directly; verified against current CS.
    private bool IsObjectiveComplete(RelicObjective o)
    {
        var c = o.Completion;
        return c.Kind switch
        {
            CompletionKind.MonsterSlot => GameState.MonsterProgress(c.Slot) >= c.Threshold,
            CompletionKind.DungeonSlot => GameState.IsDungeonComplete(c.Slot),
            CompletionKind.FateSlot => GameState.IsFateComplete(c.Slot),
            CompletionKind.LeveSlot => GameState.IsLeveComplete(c.Slot),
            // Atma farms are the ItemCount objectives with a user-facing target: the zone is done (and
            // the run moves to the next zone) once Config.AtmaPerZone of THAT zone's atma are held.
            // Every other ItemCount objective keeps its authored threshold.
            CompletionKind.ItemCount => GameState.InventoryCount(c.ItemId)
                >= (o.Stage == RelicStage.Atma ? AtmaTarget(o) : c.Threshold),
            // Braves dungeon drops are Key Items (KeyItems container), read via KeyItemCount --
            // GetInventoryItemCount (used by ItemCount) never scans that container.
            CompletionKind.KeyItemCount => GameState.KeyItemCount(c.ItemId) >= c.Threshold,
            // Dynamic, user-set target so the Alexandrite farm re-arms whenever the
            // held count drops below the configured goal (lets you go back and farm
            // more). Target of 0 or less means "never auto-complete" (endless).
            CompletionKind.AlexandriteCount =>
                (_ctx.Config.AlexandriteTarget > 0 &&
                 GameState.InventoryCount(Data.NovusData.AlexandriteItemId) >= _ctx.Config.AlexandriteTarget)
                // The farm exists only to feed the melds, and melding SPENDS the stock (one Alexandrite
                // per meld), so a finished scroll leaves the count at ~0 -- which reads as "re-arm" and
                // would send the run back to treasure maps forever, with the Novus enhancement waiting
                // behind it. Once the scroll is at its cap (or the weapon is already Novus) there is
                // nothing left to spend it on, so the farm is done regardless of the count.
                || Novus.NovusScrollState.IsScrollFull(_ctx.Config)
                || GameState.EquippedRelicStage() >= RelicStage.Novus,
            CompletionKind.RelicItem => GameState.EquippedRelicItemId() == c.ExpectedRelicItemId,
            // Full gauge, OR the weapon has already been upgraded to Nexus (the Light was consumed into
            // the upgrade, so a Nexus weapon has no gauge to fill). The second clause stops a manual
            // re-visit pinned to the Nexus stage from re-farming Light on a Nexus weapon after the
            // upgrade -- Auto is already covered by the equipped-tier filter, but Manual mode skips it.
            CompletionKind.LightGauge =>
                GameState.IsLightGaugeFull() || GameState.EquippedRelicStage() >= RelicStage.Nexus,
            // The Zenith -> Atma enhancement is done once the equipped weapon proves the Atma tier
            // (job-agnostic; the 12 atmas are consumed by the trade, so never read the item count).
            CompletionKind.AtmaUpgraded => GameState.EquippedRelicStage() >= RelicStage.Atma,
            // The Atma -> Animus enhancement is done once the equipped weapon proves the Animus tier
            // (job-agnostic; the 9 books are consumed by the trade, so never read the item count).
            CompletionKind.AnimusUpgraded => GameState.EquippedRelicStage() >= RelicStage.Animus,
            // The Sphere Scroll is at its cap, from the game's own infused counter (recorded whenever
            // the scroll's window is open, so hand-melding counts). Also true once the weapon is
            // already Novus: the trade consumes the scroll, so there is nothing left to read and the
            // melding work must not re-arm behind a finished stage.
            CompletionKind.SphereScrollFull =>
                Novus.NovusScrollState.IsScrollFull(_ctx.Config) || GameState.EquippedRelicStage() >= RelicStage.Novus,
            // The Animus -> Novus enhancement is done once the equipped weapon proves the Novus tier
            // (job-agnostic; the Sphere Scroll is consumed by the trade, so never read an item count).
            CompletionKind.NovusUpgraded => GameState.EquippedRelicStage() >= RelicStage.Novus,
            // A Braves stage quest is in hand. Not IsQuestComplete: the four material quests are
            // repeatable and a completed one must be re-accepted for the next weapon.
            CompletionKind.BravesQuestAccepted => Steps.BravesAcceptExecutor.IsInHand(o.BravesQuest),
            // Nothing outstanding is on a retainer (or auto-withdraw is off, which makes this a no-op
            // rather than a stall). Live-read, so it re-arms if more materials are entrusted later.
            CompletionKind.BravesMaterialsFetched => Steps.FetchBravesMaterialsExecutor.Wanted(_ctx).Count == 0,
            // The Novus -> Nexus upgrade is done once the equipped weapon proves the Nexus tier
            // (job-agnostic, read from the weapon rather than a per-job item id).
            CompletionKind.NexusUpgraded => GameState.EquippedRelicStage() >= RelicStage.Nexus,
            // The Furnace trade is done once a "<base> Zenith" weapon is in the hands. The base and
            // Zenith forms are both RelicStage.Relic (the enum has no Zenith tier), so the stage read
            // cannot tell them apart -- the item id is the only thing that can.
            CompletionKind.ZenithTraded => Data.RelicWeaponStages.IsZenithWeapon(GameState.EquippedRelicItemId()),
            CompletionKind.MahatmaGauge => GameState.IsZetaFarmComplete(),
            // Animus book auto-advance: done once the active book is no longer the finished one. Uses
            // "different", not "greater", because a repeat-relic restart wraps from the last book (9)
            // back to book 1 -- a DECREASE a strict greater-than would never see complete. The != 0
            // guard ignores a transient no-note read.
            CompletionKind.RelicNoteAdvanced => GameState.ActiveRelicNoteId() != c.Book && GameState.ActiveRelicNoteId() != 0,
            // Procedural (the engine ran the steps) OR quest-aware: a base-relic part the
            // player finished manually, or that the relic quest has already advanced past,
            // is complete even if the engine never ran it -- so it is not re-farmed.
            CompletionKind.AllStepsDone => IsAllStepsComplete(o),
            _ => false,
        };
    }

    // Completion for an AllStepsDone (procedural) objective. Base-relic (Relic-stage) parts
    // are quest-authoritative: complete when the relic quest has passed the part, OR a
    // one-time quest duty (the Hydra) is already cleared, OR the engine ran the part THIS
    // run. They deliberately never read the PERSISTED procedural flag -- a queued trial that
    // did not credit must stay retryable, otherwise the run halts on the next launch with the
    // part wrongly marked done (the "no objective remains at sequence N" stall). Other stages
    // keep the persisted flag, since their steps are real work only the engine performs.
    private bool IsAllStepsComplete(RelicObjective o)
    {
        if (o.Stage == RelicStage.Relic)
            return BaseRelicState.IsPartCompleteByQuest(o)
                   || (o.OneTimeDutyContentId != 0 && GameState.IsDutyCompleted(o.OneTimeDutyContentId))
                   || _relicRan.Contains(o.Id);
        return _proceduralDone.Contains(o.Id);
    }
}
