using System;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;

namespace Relicable.Steps;

// Executors that drive an NPC conversation. All share NpcInteractor for the
// approach-target-interact phases; they differ only in what (if anything) they
// pick from a list menu and how completion is confirmed.
//
// Each executor is a singleton reused across steps, but only one step runs at a
// time, so the single NpcInteractor instance is reset in Start and is safe.

// Plain talk: walk to the NPC, interact, let TextAdvance carry the dialogue,
// complete when the conversation ends.
//
// With StepData.UnequipRelicFirst it also takes the relic weapon off before talking and puts it back
// afterwards if the conversation did not consume it -- see the field's docs for why the hand-over
// needs that, and RelicStageMemo for how progress tracking survives the window.
public sealed class InteractNpcExecutor : ITaskExecutor
{
    public StepType Handles => StepType.InteractNpc;

    private readonly NpcInteractor _npc = new();

    // The equipped weapon slots this step unequipped (0 = main hand, 1 = off hand), so Stop can put
    // back exactly what it took. Empty when the step did not unequip anything. Reset every Start:
    // the executor is a reused singleton, so a stale entry would re-equip on an unrelated turn-in.
    private readonly System.Collections.Generic.List<uint> _unequipped = new();

    public void Start(StepData step, ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
        _npc.Reset();
        _unequipped.Clear();

        if (step.UnequipRelicFirst)
            UnequipRelicWeapons();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
        => ToStatus(_npc.Tick(step.NpcDataId, step.Position, ctx));

    public void Stop(ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
        RestoreRelicWeapons();
    }

    // Take the relic out of both weapon slots so the turn-in UI can list it, recording the stage
    // first so planning does not read "no relic at all" while it is off. Both slots are checked
    // because the Paladin's relic is a pair (Curtana + Holy Shield) and the quest wants both.
    private void UnequipRelicWeapons()
    {
        var stage = GameState.EquippedRelicStage();
        if (stage == RelicStage.None)
            return; // nothing relic-ish equipped; the turn-in item is already loose in the bags
        RelicStageMemo.Note(stage);

        // Off hand first: unequipping the main hand can shuffle an off-hand that depends on it, so
        // taking the dependent slot off first keeps both moves predictable.
        for (var slot = 1; slot >= 0; slot--)
        {
            var id = GameState.EquippedWeaponItemId((ushort)slot);
            if (id == 0 || !GameState.IsRelicWeaponId(id))
                continue;
            if (GameState.TryUnequipWeapon((ushort)slot))
            {
                _unequipped.Add(id);
                DebugLog.Info($"Turn-in: unequipped '{GameState.ItemName(id)}' so it can be handed over.");
            }
            else
            {
                DebugLog.Warn($"Turn-in: could not unequip '{GameState.ItemName(id)}' (no free armoury or bag " +
                              "slot?). The hand-over will not list it -- free a slot and resume.");
            }
        }
    }

    // Put back anything we took off that is STILL held: the hand-over consumes the weapon on
    // success, so a weapon we can still find means the turn-in did not happen (aborted, failed, or
    // re-planned) and leaving the character bare-handed would strand the run -- the next step's
    // stage read would say "no relic" and the kill steps would have nothing equipped to credit.
    private void RestoreRelicWeapons()
    {
        if (_unequipped.Count == 0)
            return;
        var restored = false;
        foreach (var id in _unequipped)
        {
            if (GameState.TryFindHeldRelic(i => i == id, includeEquipped: false,
                    out var container, out var slot, out _))
            {
                GameState.TryEquipFromBag(container, slot);
                restored = true;
                DebugLog.Info($"Turn-in did not take '{GameState.ItemName(id)}'; re-equipping it.");
            }
        }
        _unequipped.Clear();
        // Either the weapon is back on (the live read is authoritative again) or it was handed over
        // (there is nothing to stand in for). Both mean the memo has done its job.
        if (restored)
            RelicStageMemo.Clear();
    }

    internal static ExecutorStatus ToStatus(InteractionPhase phase) => phase switch
    {
        InteractionPhase.Done => ExecutorStatus.Complete,
        InteractionPhase.Failed => ExecutorStatus.Failed,
        _ => ExecutorStatus.InProgress,
    };
}

// Accept and run a Trials-of-the-Braves leve, modeled on Battlevest's flow: open
// the levemete, choose the Battlecraft category from its SelectString menu, accept
// offered leves from the GuildLeve board, then run each accepted leve to
// completion. Completion is the book's LeveSlot (RelicNote): when the target leve's
// slot is done the step finishes. If the target was not offered, running the
// accepted (filler) leves and re-opening the board rerolls the list. Gated by leve
// allowances and a cycle cap. See LeveBoard for the addon flow and accept seam.
public sealed class StartLeveExecutor : ITaskExecutor
{
    public StepType Handles => StepType.StartLeve;

    private const long TimeoutMs = 600_000;
    private const int MaxCycles = 40;
    // After AcceptMap fires, the accept needs a server round-trip before it shows in
    // QuestManager; checking on the same tick misread every success as "not accepted".
    private const long AcceptRegisterGraceMs = 2000;
    // Cap on how many ticks we retry closing the levemete board before a queued leve runs. The
    // close matches Battlevest and normally takes hold in a tick or two; the cap only guards
    // against a close we cannot drive so it proceeds instead of looping forever.
    private const int MaxCloseAttempts = 40;

    private readonly NpcInteractor _npc = new();
    private readonly LeveRunner _runner = new();
    private readonly System.Collections.Generic.Queue<uint> _toRun = new();
    private long _startTicks;
    private long _acceptFiredTicks;
    private int _cycles;
    private bool _running;
    private int _closeAttempts;
    private long _lastStateLog; // throttles the diagnostic state heartbeat
    // The leve we are currently accepting: the target, or a filler chosen to cycle the battle-leve
    // rotation when the target is not offered. 0 = accepting the target.
    private uint _pendingAccept;
    // The leve id currently handed to the runner (set at dequeue), so a completed run can tell
    // whether the TARGET or a filler just finished.
    private uint _runningLeveId;
    // When the TARGET leve's run finished (0 = it has not run yet this step). Once set, the target
    // is NOT re-accepted / re-run: a completed battle leve lingers in the accepted list, and the
    // re-queue path trusts that flag, so re-running is what produces the reported open/close spam.
    private long _targetRanAt;
    // Bound on the post-run TURN-IN phase: after the target's objective is cleared we return to the
    // levemete and "Collect Reward." to credit the book slot (the top-of-Update IsLeveComplete check
    // completes the step the instant collection credits it). If it never credits within this window
    // (e.g. a FAILED leve that has no reward to collect), Fail so the controller re-plans instead of
    // waiting forever. Generous: it covers the approach to the levemete plus the collection clicks.
    private const long CollectTimeoutMs = 90_000;
    // Last open-menu signature logged during collection, so the exact "Collect Reward." addon chain is
    // captured once per change (an in-game SEAM) without spamming the log.
    private string _lastCollectSig = string.Empty;
    // Bound on the post-collection menu-close loop: once the slot credits, the levemete's own
    // SelectString (and any reward/confirm window) is STILL open and keeps us in the NPC event, so we
    // close it before completing -- see the completion branch in Update. Kept separate from
    // _closeAttempts (the pre-run board exit) so the two loops do not share a budget.
    private int _exitCloseAttempts;

    public void Start(StepData step, ExecutionContext ctx)
    {
        ctx.TextAdvance.Enable(); // verify TextAdvance is on (used globally)
        _npc.Reset();
        _toRun.Clear();
        _running = false;
        _startTicks = Environment.TickCount64;
        _acceptFiredTicks = 0;
        _cycles = 0;
        _closeAttempts = 0;
        _pendingAccept = 0;
        _lastStateLog = 0;
        _runningLeveId = 0;
        _targetRanAt = 0;
        _lastCollectSig = string.Empty;
        _exitCloseAttempts = 0;
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Keep the global leve-return handler's window warm for the whole step, INCLUDING this
        // completion tick: the "return to the levemete?" prompt pops a beat after the slot credits
        // (below), by which point this step is Complete and torn down. Stamping here (before the
        // short-circuit) leaves the window open GraceMs past now, so Plugin.OnUpdate's LeveReturn.Tick
        // still accepts the prompt even if the run Stops immediately after the final leve. See LeveReturn.
        Interaction.LeveReturn.NoteLeveActivity();

        // Completion: the book's leve slot is done.
        var c = ctx.CurrentObjective?.Completion;
        if (c is { Kind: CompletionKind.LeveSlot } && GameState.IsLeveComplete(c.Slot))
        {
            // The slot credited (the reward was collected). But finalizing the reward window
            // (CollectReward's jr.Complete()) drops the game back to the levemete's own SelectString
            // menu, which keeps us in the NPC event -- and being in the event blocks the next step's
            // movement (the reported "errors trying to exit the leve": the follow-up teleport just
            // retries because the character is still occupied). So drive the close (bounded) until we are
            // fully out of the event, THEN complete.
            //
            // Gate on EventConditions.InEvent, NOT only AnyLeveMenuOpen(): there is a brief window where
            // JournalResult has closed but the levemete SelectString has not re-opened yet, so
            // AnyLeveMenuOpen() momentarily reads FALSE. Gating on the menu alone let the step complete in
            // that gap; the SelectString then re-opened with nothing left to close it and the character
            // sat in the event (this exact race is why the previous menu-close fix was not enough).
            // InEvent stays true across those menu transitions, so it closes the reopened SelectString
            // too. Best-effort: CloseAll cancels the SelectString / reward window / stray Yes-No; the
            // attempt cap lets us complete anyway if a close cannot be driven.
            if ((LeveBoard.AnyLeveMenuOpen() || Interaction.EventConditions.InEvent)
                && _exitCloseAttempts < MaxCloseAttempts)
            {
                _exitCloseAttempts++;
                LeveBoard.CloseAll();
                return ExecutorStatus.InProgress;
            }
            return ExecutorStatus.Complete;
        }

        // Diagnostic heartbeat (every 5s): the full decision context, so a "finishes a leve then stands
        // still" stall shows EXACTLY which branch is holding and why (is the slot credited? is the target
        // accepted? out of allowances? which NpcInteractor phase? is a menu / event still open?).
        if (Environment.TickCount64 - _lastStateLog > 5000)
        {
            _lastStateLog = Environment.TickCount64;
            var slot = c is { Kind: CompletionKind.LeveSlot } ? c.Slot : -1;
            // levequestDone (IsLevequestComplete) vs slotComplete (the RelicNote book bit) is the
            // decisive pair for "completed but never credited": levequestDone=true while
            // slotComplete=false means the leve DID finish but its book slot did not credit (a
            // slot-mapping or credit-timing problem, NOT an incomplete leve) -- and re-running the
            // finished leve, which is what looked like "flying back to redo it", cannot help.
            DebugLog.Info($"Leve step [target {step.LeveId}] state: running={_running} toRun={_toRun.Count} " +
                $"pending={_pendingAccept} slot={slot} slotComplete={(slot >= 0 && GameState.IsLeveComplete(slot))} " +
                $"levequestDone={GameState.IsLevequestComplete(step.LeveId)} " +
                $"targetAccepted={GameState.IsLeveAccepted(step.LeveId)} allowances={GameState.LeveAllowances()} " +
                $"npcPhase={_npc.Phase} menuOpen={LeveBoard.AnyLeveMenuOpen()} inEvent={Interaction.EventConditions.InEvent} " +
                $"cycles={_cycles} closeAttempts={_closeAttempts}");
        }

        if (Environment.TickCount64 - _startTicks > TimeoutMs || _cycles > MaxCycles)
        {
            DebugLog.Warn($"Leve: stopped after {_cycles} cycles");
            return ExecutorStatus.Failed;
        }

        // Run accepted leves before accepting more.
        if (_running)
        {
            if (_runner.Tick(ctx))
            {
                _running = false;
                // Record when the TARGET's run finished, so it is not re-accepted below. A filler
                // finishing leaves _targetRanAt at 0 and the rotation continues re-approaching the
                // board for the target as before.
                if (_runningLeveId == step.LeveId)
                    _targetRanAt = Environment.TickCount64;
                // The shared levemete NpcInteractor (_npc) and this executor's own timeout
                // (_startTicks) were both PAUSED -- not ticked -- for the whole multi-minute leve
                // run (LeveRunner can take up to 300s). Both clocks are wall-clock, so without a
                // refresh here the post-run board re-approach is charged the entire run duration and
                // times out on its FIRST tick: _npc's 60s OverallTimeout fires InteractionPhase.Failed,
                // and the 600s executor timeout can trip too. That is the reported "after the leve it
                // teleports back and then nothing happens": the re-open instantly Fails, RelicController
                // re-plans + re-teleports, and 3 such fails hit MaxConsecutiveFailures -> Stop(). A
                // completed leve -- the TARGET or a rotation FILLER (when the target is not offered) --
                // is real progress, so hand both clocks a fresh budget. This is what lets multiple
                // fillers cycle within one run to rotate the target onto the board, instead of one
                // filler per Failed. Bounded by MaxCycles so the rotation cannot loop forever; harmless
                // on the target path (the top-of-Update IsLeveComplete check completes the step the
                // instant the target credits, so this refresh only ever benefits the filler-cycling path).
                _npc.Reset();
                _startTicks = Environment.TickCount64;
            }
            return ExecutorStatus.InProgress;
        }

        // The TARGET leve's objective has been cleared. Do NOT re-accept or re-run it. But clearing the
        // objective in the field does NOT credit the RelicNote book: the leve-slot bit is set on TURN-IN
        // -- returning to the levemete and choosing "Collect Reward." -- which is why leves can be
        // "banked" uncollected across books. The old code only waited a few seconds for a credit that
        // can never come on objective-clear, then Failed and re-accepted the same leve, which is exactly
        // the reported "leves are not turning in / it re-does the same leve" loop. So here we DRIVE the
        // collection: the return prompt already teleported us to the levemete, so approach + interact it
        // and select "Collect Reward." (the top-of-Update IsLeveComplete short-circuit completes the step
        // the instant collection credits the slot). Bounded by CollectTimeoutMs so a leve that has no
        // reward to collect -- a FAILED leve (e.g. a Protection leve whose charge died) -- re-plans
        // instead of hanging.
        if (_targetRanAt != 0)
        {
            if (Environment.TickCount64 - _targetRanAt > CollectTimeoutMs)
            {
                LeveBoard.CloseAll();
                DebugLog.Warn($"Leve {step.LeveId}: reward not collected / slot not credited within " +
                              $"{CollectTimeoutMs / 1000}s (leve still accepted={GameState.IsLeveAccepted(step.LeveId)}). " +
                              "Failing so the run re-plans. If the leve completed its objective but never credits, the " +
                              "'Leve N collect: menus open ->' lines above show the actual 'Collect Reward.' addon chain.");
                return ExecutorStatus.Failed;
            }

            // Log the open collection menus once per change so the exact (offline-untestable) addon chain
            // after "Collect Reward." is captured if it stalls.
            var sig = Interaction.DialogueMenu.OpenSignature();
            if (sig != _lastCollectSig)
            {
                _lastCollectSig = sig;
                if (!string.IsNullOrEmpty(sig))
                    DebugLog.Info($"Leve {step.LeveId} collect: menus open -> {sig}");
            }

            // Drive an open collection menu (finalize the reward / pick "Collect Reward." / pick the
            // completed leve). If none is open and we are not already in the levemete event, approach and
            // interact the levemete to open it.
            if (LeveBoard.CollectReward(Data.Sheets.LeveName(step.LeveId)))
                return ExecutorStatus.InProgress;
            if (!LeveBoard.AnyLeveMenuOpen() && !Interaction.EventConditions.InEvent)
            {
                if (_npc.Tick(step.LevemeteDataId, step.Position, ctx) == InteractionPhase.Failed)
                    _npc.Reset(); // could not reach/interact the levemete; retry until CollectTimeoutMs
            }
            return ExecutorStatus.InProgress;
        }

        // Before running a queued leve, make sure the levemete menus actually closed. Any of the
        // GuildLeve board, the JournalDetail popup, or the levemete SelectString keeps us in the
        // NPC event, which blocks character movement, so one left open by a mistimed close stalls
        // the LeveRunner's travel ("doesn't exit the leve after accepting, gets stuck"). Retry the
        // close until none remain, then run; the attempt cap lets it proceed rather than loop if a
        // close cannot be driven. The open menus are logged once so an unexpected addon is visible.
        if (_toRun.Count > 0 && (LeveBoard.AnyLeveMenuOpen() || Interaction.EventConditions.InEvent)
            && _closeAttempts < MaxCloseAttempts)
        {
            if (_closeAttempts == 0)
                Interaction.DialogueMenu.LogOpenMenus("Leve exit (post-accept)");
            _closeAttempts++;
            LeveBoard.CloseAll();
            return ExecutorStatus.InProgress;
        }
        if (_toRun.Count > 0)
        {
            _closeAttempts = 0;
            _runningLeveId = _toRun.Dequeue();
            _runner.Reset(_runningLeveId);
            _running = true;
            return ExecutorStatus.InProgress;
        }

        // A leve we fired an accept for (the target, or a pending filler) has registered ->
        // queue it to run, then leave the board. Checked before the allowance gate: the
        // allowance was already spent on the accept, so it must still be queued.
        var pendingOrTarget = _pendingAccept != 0 ? _pendingAccept : step.LeveId;
        if (GameState.IsLeveAccepted(pendingOrTarget))
        {
            if (!_toRun.Contains(pendingOrTarget))
                _toRun.Enqueue(pendingOrTarget);
            DebugLog.Verbose($"Leve: {pendingOrTarget} accepted; queued to run");
            _pendingAccept = 0;
            _acceptFiredTicks = 0;
            _closeAttempts = 0;
            LeveBoard.CloseAll();
            _npc.Reset();
            return ExecutorStatus.InProgress;
        }

        if (GameState.LeveAllowances() <= 0)
        {
            DebugLog.Warn("Leve: out of allowances; cannot continue");
            return ExecutorStatus.Failed;
        }

        // An accept was fired: give the server a moment to register it (the check above completes
        // the hand-off the instant it does) instead of re-driving the board every tick.
        if (_acceptFiredTicks != 0 && Environment.TickCount64 - _acceptFiredTicks < AcceptRegisterGraceMs)
            return ExecutorStatus.InProgress;

        // Approach the levemete and walk the accept flow (ported from Battlevest): SelectString
        // category -> GuildLeve board -> select + AcceptMap the current accept id (the target, or a
        // filler when the target is not currently offered).
        var phase = _npc.Tick(step.LevemeteDataId, step.Position, ctx);
        if (phase == InteractionPhase.Failed)
            return ExecutorStatus.Failed;

        if (LeveBoard.CategoryOpen())
        {
            // Open the target leve's own category (the book leves are Grand Company leves, a
            // different tab than the regional battlecraft board).
            LeveBoard.SelectCategory(Data.Locations.LeveCategoryName(step.LeveId));
            return ExecutorStatus.InProgress;
        }

        if (LeveBoard.BoardOpen())
        {
            var acceptId = _pendingAccept != 0 ? _pendingAccept : step.LeveId;
            switch (LeveBoard.AcceptTarget(acceptId))
            {
                case LeveBoard.AcceptResult.Fired:
                    _acceptFiredTicks = Environment.TickCount64;
                    _cycles++;
                    _pendingAccept = acceptId; // wait for IsLeveAccepted(acceptId) above
                    break;

                case LeveBoard.AcceptResult.NotOffered:
                    _cycles++;
                    if (_pendingAccept == 0)
                    {
                        // The TARGET is not offered. Battle leves are not all shown at once and
                        // rotate as you complete them, so accept a DIFFERENT offered leve in this
                        // category, complete it, and re-check -- repeat until the target shows.
                        var filler = ResolveFiller(step);
                        if (filler != 0)
                        {
                            _pendingAccept = filler;
                            DebugLog.Info($"Leve target {step.LeveId} not offered; cycling the rotation via filler {filler}");
                            // next tick AcceptTarget(filler) accepts it
                        }
                        else
                        {
                            DebugLog.Warn($"Leve target {step.LeveId} not offered and no filler leve is available to " +
                                          "cycle the rotation (board empty, or every offered leve is already accepted/queued).");
                            LeveBoard.Close();
                            _npc.Reset();
                        }
                    }
                    else
                    {
                        // The chosen filler is no longer on the board (the list changed): drop it and
                        // re-pick from the fresh board next time.
                        _pendingAccept = 0;
                        LeveBoard.Close();
                        _npc.Reset();
                    }
                    break;

                // NotReady / Selecting: let the addons settle and try next tick.
            }
        }

        return ExecutorStatus.InProgress;
    }

    // Pick a filler leve to cycle the rotation: the first leve offered on the board that is not the
    // target, not already accepted, and not already queued. Resolved to a leve row id via the Leve
    // sheet (scoped to this levemete) so AcceptTarget can select it by name.
    private uint ResolveFiller(StepData step)
    {
        var targetName = Data.Sheets.LeveName(step.LeveId);
        foreach (var name in LeveBoard.OfferedLeveNames())
        {
            if (string.Equals(name, targetName, StringComparison.Ordinal))
                continue;
            var id = Data.Locations.LeveIdByNameAtLevemete(name, step.LevemeteDataId);
            if (id == 0 || GameState.IsLeveAccepted(id) || _toRun.Contains(id))
                continue;
            return id;
        }
        return 0;
    }

    public void Stop(ExecutionContext ctx)
    {
        // Clear any leftover leve navigation so a stale destination (the leve anchor) cannot survive
        // into the next objective. The top-of-Update IsLeveComplete short-circuit can complete this
        // step without the LeveRunner ever reaching its own Finish (which stops the mesh), so after
        // the "return to a nearby aetheryte" teleport vnavmesh would otherwise route the character
        // back to the just-finished leve site. Stopping here (Stop runs on every step completion)
        // guarantees the mesh is halted on the way out regardless of which completion path fired.
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        // Belt-and-suspenders: close any levemete menu still open on the way out, so a teardown that
        // bypasses the completion-branch close loop (an external stop / re-plan while the collection
        // SelectString is up) cannot strand the character in the NPC event. Idempotent no-op when
        // nothing leve-related is open.
        if (LeveBoard.AnyLeveMenuOpen())
            LeveBoard.CloseAll();
    }
}

// Turn in a completed leve at the levemete. Interact and let TextAdvance carry
// the hand-in; complete when the conversation ends.
public sealed class TurnInLeveExecutor : ITaskExecutor
{
    public StepType Handles => StepType.TurnInLeve;

    private readonly NpcInteractor _npc = new();

    public void Start(StepData step, ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
        _npc.Reset();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
        => InteractNpcExecutor.ToStatus(_npc.Tick(step.LevemeteDataId, step.Position, ctx));

    public void Stop(ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}

// Upgrade the relic at the appropriate NPC (Jalzahn / Gerolt). Interact, pick the
// upgrade option from any SelectString list, and confirm success by the equipped
// relic item id changing to the expected value. If the conversation ends without
// the upgrade taking, the step fails so the controller re-plans.
public sealed class UpgradeRelicExecutor : ITaskExecutor
{
    // A list addon stays visible for a frame or more after FireCallback, so firing
    // every tick can double-select into the same or the next menu. Same throttle
    // discipline as TreasureMap (400ms) / NpcInteractor (600ms) / LeveRunner (700ms).
    private const long MenuCooldownMs = 600;

    public StepType Handles => StepType.UpgradeRelic;

    private readonly NpcInteractor _npc = new();
    private long _lastMenuTicks;

    public void Start(StepData step, ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
        _npc.Reset();
        _lastMenuTicks = 0;
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Authoritative success: the relic became the expected item.
        if (GameState.EquippedRelicItemId() == step.ExpectedRelicItemId)
            return ExecutorStatus.Complete;

        var phase = _npc.Tick(step.NpcDataId, step.Position, ctx);

        // Pick the upgrade option from the SelectString list by its configured
        // text (step.MenuOption, e.g. the stage name); fall back to the first entry.
        if (phase == InteractionPhase.InDialogue && DialogueMenu.IsOpen("SelectString")
            && Environment.TickCount64 - _lastMenuTicks >= MenuCooldownMs)
        {
            _lastMenuTicks = Environment.TickCount64;
            if (string.IsNullOrEmpty(step.MenuOption) || !DialogueMenu.SelectByText("SelectString", step.MenuOption))
                DialogueMenu.Select("SelectString", 0);
        }

        if (phase == InteractionPhase.Failed)
            return ExecutorStatus.Failed;

        // Conversation ended but the relic did not change: the upgrade did not
        // happen (wrong option, missing materials). Fail so the controller retries.
        if (phase == InteractionPhase.Done)
        {
            DebugLog.Warn($"UpgradeRelic: dialogue ended but relic is not {step.ExpectedRelicItemId}");
            return ExecutorStatus.Failed;
        }

        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
