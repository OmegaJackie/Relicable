using System;
using Dalamud.Game.ClientState.Conditions;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;

namespace Relicable.Steps;

// Shared driver for a relic upgrade performed at Jalzahn (Hyrstmill, North Shroud, reached
// from Fallgourd Float). Flow: (wait out a farm duty if still bound) -> optional OnBeforeTrip
// prep (the Atma flow unequips the Zenith weapon so it appears in the turn-in list) -> teleport
// to Fallgourd Float -> approach Jalzahn -> interact -> drive his menu chain by needles, then a
// weapon pick, then sub needles -> complete when IsUpgraded holds. Concrete upgrades: Zenith ->
// Atma (AtmaUpgradeExecutor) and Novus -> Nexus (NexusUpgradeExecutor) share this NPC and trip
// and differ only in needles, target tier, weapon-picking, completion, and prep hooks.
//
// SEAM: Jalzahn's exact submenu wording/structure is not exposed by any offline data source, so
// the menus are driven by case-insensitive needles in both list addons via ECommons' AddonMaster
// (correct entry index, prompt-safe), each distinct open menu is logged once (LogOpenMenus), the
// picks are throttled (MenuActionCooldownMs) and a stuck menu fails after MenuStuckMs. If the
// upgrade does not register the step FAILS (it never false-completes), so a wrong needle stalls
// safely with guidance.
public abstract class JalzahnUpgradeExecutorBase : ITaskExecutor
{
    // Grace after the Jalzahn conversation ends for the upgrade to register before failing.
    private const long UpgradeRegisterGraceMs = 4000;
    // Grace to equip a just-obtained weapon (the traded Atma weapon lands unequipped) before failing,
    // so a weapon that can never be equipped cannot hang the step forever (matches EnsureRelicEquipped).
    private const long EquipGraceMs = 5000;
    // Min gap between menu picks. A list addon re-fired every frame can double-select into the next
    // menu or close it before it settles (the same discipline as BuyRelicBookExecutor).
    private const long MenuActionCooldownMs = 500;
    // Fail if a single Jalzahn menu stays open this long without advancing (our pick did not match).
    private const long MenuStuckMs = 10000;

    public abstract StepType Handles { get; }

    // The tier the weapon reaches when this upgrade succeeds (default completion uses it).
    protected abstract RelicStage TargetStage { get; }

    // Stage-2 needles: an upgrade action inside Jalzahn's branch, tried AFTER the main-menu needle
    // and the weapon pick (so a broad word cannot collide with a sibling service line on the main
    // menu). The Atma flow leaves this empty for exactly that reason ("atma" would match "Relic
    // Weapon Atma Enhancement"); the Nexus flow uses light/nexus needles that match no sibling.
    protected abstract string[] SubMenuNeedles { get; }

    // Stage-1 needles: the branch on Jalzahn's main menu that opens this upgrade's flow. Full
    // phrases so a needle cannot also match a sibling branch's submenu header.
    protected abstract string[] MainMenuNeedles { get; }

    // Log prefix for the menu capture, e.g. "Nexus upgrade (Jalzahn)".
    protected abstract string FlowLabel { get; }

    // Guidance logged when Jalzahn's dialogue ends without the weapon upgrading.
    protected abstract string RegisterFailGuidance { get; }

    // ---- Overridable hooks (defaults keep the Nexus flow unchanged) ----

    // Completion predicate. Default: the equipped weapon reached the target tier.
    protected virtual bool IsUpgraded(ExecutionContext ctx) => GameState.EquippedRelicStage() >= TargetStage;

    // A chance at Start to finish immediately without travelling (return true -> go straight to
    // Done). The Atma flow uses it to equip an Atma weapon that is already held (the trade already
    // happened, or a re-select) -- which must run BEFORE BlockedReason, since the consumed atmas
    // would otherwise trip the <12 block. Default: never.
    protected virtual bool TryFinishEarly(ExecutionContext ctx) => false;

    // The weapon name to select in a turn-in / weapon picker. Default: the equipped main-hand name
    // (the Nexus weapon stays equipped). The Atma flow overrides it to the held Zenith weapon name
    // (it is turned in unequipped).
    protected virtual string WeaponMenuName(ExecutionContext ctx) => GameState.EquippedMainHandName();

    // One-time prep after the blocked check, before the trip. The Atma flow unequips the Zenith
    // weapon here so it lists in Jalzahn's turn-in menu. Default: no-op.
    protected virtual void OnBeforeTrip(ExecutionContext ctx) { }

    // Runs each Interact tick before the NPC tick. Return true when it did work that should re-check
    // completion next tick (the Atma flow equips a just-traded Atma weapon here). Default: no-op.
    protected virtual bool OnInteractTick(ExecutionContext ctx) => false;

    // Cleanup on Stop (the Atma flow re-equips the Zenith if it unequipped and aborted). Default: none.
    protected virtual void OnStop(ExecutionContext ctx) { }

    // Per-run reset of subclass-owned fields (the base Start resets only its own). Called at the very
    // top of Start so a reused singleton executor never carries derived state across runs. Default: none.
    protected virtual void OnReset() { }

    // Non-null blocks the step at Start: a prerequisite is unmet (e.g. fewer than 12 atmas, or the
    // unlock quest is not complete). The step then fails once with this guidance.
    protected virtual string? BlockedReason(ExecutionContext ctx) => null;

    private enum Phase { WaitExit, Teleport, Interact, Equipping, Done, Blocked }

    private readonly AetheryteTeleportExecutor _teleport = new();
    private readonly NpcInteractor _npc = new();

    private Phase _phase;
    private StepData? _teleStep;
    private long _doneDeadline;
    private long _equipDeadline;
    private long _lastMenuAction;
    private long _menuSince;
    private string _lastMenuSig = string.Empty;
    private string _blockedReason = string.Empty;

    public void Start(StepData step, ExecutionContext ctx)
    {
        OnReset();
        _teleStep = null;
        _doneDeadline = 0;
        _equipDeadline = 0;
        _lastMenuAction = 0;
        _menuSince = 0;
        _lastMenuSig = string.Empty;
        _blockedReason = string.Empty;

        // Already at/past the target tier (a re-select after the upgrade, or done manually).
        if (IsUpgraded(ctx))
        {
            _phase = Phase.Done;
            return;
        }

        // The result may be held but not yet equipped (trade already done) -> equip it (bounded),
        // no trip. Runs before BlockedReason on purpose (the consumed atmas would trip <12).
        if (TryFinishEarly(ctx))
        {
            _phase = Phase.Equipping;
            _equipDeadline = Environment.TickCount64 + EquipGraceMs;
            return;
        }

        var blocked = BlockedReason(ctx);
        if (blocked != null)
        {
            _blockedReason = blocked;
            _phase = Phase.Blocked;
            return;
        }

        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        OnBeforeTrip(ctx);

        // Coming straight off a farm duty the player can still be bound by the instance (the
        // duty-complete / eject window); teleport is blocked there, so wait for the overworld.
        _phase = BoundByDuty() ? Phase.WaitExit : StartTrip(ctx);
    }

    private Phase StartTrip(ExecutionContext ctx)
    {
        if (NexusData.FallgourdAetheryte != 0)
        {
            _teleStep = new StepData { Type = StepType.AetheryteTeleport, AetheryteId = NexusData.FallgourdAetheryte };
            _teleport.Start(_teleStep, ctx);
            return Phase.Teleport;
        }
        _npc.Reset();
        return Phase.Interact;
    }

    // Still inside a duty instance (any of the three bound-by-duty flags) -> teleport is blocked.
    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Authoritative completion (any job).
        if (IsUpgraded(ctx))
            return ExecutorStatus.Complete;

        switch (_phase)
        {
            case Phase.Blocked:
                DebugLog.Warn($"{FlowLabel}: {_blockedReason}");
                return ExecutorStatus.Failed;

            case Phase.WaitExit:
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                _phase = StartTrip(ctx);
                return ExecutorStatus.InProgress;

            case Phase.Teleport:
                var t = _teleport.Update(_teleStep!, ctx);
                if (t == ExecutorStatus.Failed)
                    return ExecutorStatus.Failed;
                if (t == ExecutorStatus.Complete)
                {
                    _teleport.Stop(ctx);
                    _npc.Reset();
                    _phase = Phase.Interact;
                }
                return ExecutorStatus.InProgress;

            case Phase.Equipping:
                // A just-obtained weapon (the traded Atma weapon lands unequipped) is being equipped;
                // the top-of-Update IsUpgraded check completes the step the instant it lands. Keep
                // retrying, but bound it so a weapon that can never be equipped fails with guidance
                // instead of hanging InProgress forever.
                OnInteractTick(ctx);
                if (Environment.TickCount64 > _equipDeadline)
                {
                    DebugLog.Warn($"{FlowLabel}: {RegisterFailGuidance}");
                    return ExecutorStatus.Failed;
                }
                return ExecutorStatus.InProgress;

            case Phase.Interact:
                // First: any post-trade adjustment (the Atma flow equips the freshly-traded weapon).
                // If it did work, hand off to the bounded Equipping phase so completion is the equipped
                // tier (not the mere fact a weapon was found), and it cannot hang.
                if (OnInteractTick(ctx))
                {
                    _phase = Phase.Equipping;
                    _equipDeadline = Environment.TickCount64 + EquipGraceMs;
                    return ExecutorStatus.InProgress;
                }

                var p = _npc.Tick(NexusData.JalzahnNpcId, NexusData.JalzahnPosition, ctx);
                if (p == InteractionPhase.Failed)
                    return ExecutorStatus.Failed;

                // Drive the menu chain whenever a list menu is open, even if the interactor reports the
                // conversation "done": the upgrade sub-menu can linger as a SelectString after the NPC
                // event ends (as Remon's Mahatma sign picker does), so gating on InDialogue would skip it.
                if (DialogueMenu.AnyOpen())
                {
                    // Log each DISTINCT menu once as the flow advances, so the real wording is captured.
                    var sig = DialogueMenu.OpenSignature();
                    if (sig.Length > 0 && sig != _lastMenuSig)
                    {
                        DialogueMenu.LogOpenMenus(FlowLabel);
                        _lastMenuSig = sig;
                        _menuSince = Environment.TickCount64; // the menu advanced; restart the stuck timer
                    }

                    // Stuck detector: the SAME menu open (sig unchanged) this long while we keep picking
                    // means our option did not advance it -- fail so the logged menu can be wired, rather
                    // than looping until the generic 60s interaction timeout with worse guidance.
                    if (_menuSince != 0 && Environment.TickCount64 - _menuSince > MenuStuckMs)
                    {
                        DebugLog.Warn($"{FlowLabel}: stuck on the same menu for {MenuStuckMs / 1000}s without it " +
                                      $"advancing. {RegisterFailGuidance}");
                        return ExecutorStatus.Failed;
                    }

                    // Throttle the picks so a list addon is not re-fired every frame.
                    if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
                    {
                        _lastMenuAction = Environment.TickCount64;
                        TrySelectUpgrade(ctx);
                        DialogueMenu.ConfirmYes();
                    }
                    _doneDeadline = 0; // a menu is still up; the conversation is not finished
                    return ExecutorStatus.InProgress;
                }

                if (p == InteractionPhase.Done)
                {
                    // No menu open and the conversation ended; allow a moment for the upgrade to register
                    // (the top-of-Update completion check ends the step the instant it does).
                    if (_doneDeadline == 0)
                        _doneDeadline = Environment.TickCount64 + UpgradeRegisterGraceMs;
                    else if (Environment.TickCount64 > _doneDeadline)
                    {
                        DebugLog.Warn($"{FlowLabel}: {RegisterFailGuidance}");
                        return ExecutorStatus.Failed;
                    }
                }

                return ExecutorStatus.InProgress;

            default:
                return ExecutorStatus.Complete; // Done (already handled at Start)
        }
    }

    // Jalzahn's upgrade path is a SEAM (no offline data). Try, in the order that advances the flow:
    // (1) the main-menu branch that opens this upgrade (so a broad sub needle cannot collide with a
    // sibling service line on the main menu), (2) the specific weapon in a turn-in / weapon picker,
    // (3) an upgrade action inside the branch. The Yes/No cost/confirmation is handled by ConfirmYes
    // in the caller. The first match fires; a null result is fine.
    private static readonly string[] Addons = { "SelectString", "SelectIconString" };

    private bool TrySelectUpgrade(ExecutionContext ctx)
    {
        var weapon = WeaponMenuName(ctx);
        foreach (var addon in Addons)
        {
            if (!DialogueMenu.IsOpen(addon))
                continue;

            foreach (var needle in MainMenuNeedles)
                if (DialogueMenu.SelectByTextSafe(addon, needle))
                    return true;

            if (!string.IsNullOrEmpty(weapon) && DialogueMenu.SelectByTextSafe(addon, weapon))
                return true;

            foreach (var needle in SubMenuNeedles)
                if (DialogueMenu.SelectByTextSafe(addon, needle))
                    return true;
        }
        return false;
    }

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
        OnStop(ctx);
    }
}
