using System;
using Dalamud.Game.ClientState.Conditions;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;

namespace Relicable.Steps;

// Zeta stage: attach the next Mahatma to the equipped Zodiac Braves weapon at Remon
// (Swiftperch, Western La Noscea; 50 Allagan Tomestones of Poetics each). Light only
// charges an attached Mahatma, so the farm loop runs this before each duty hand-off:
// it is a no-op while a Mahatma is already attached and only makes the Remon trip when
// one has just awakened and the next must be attached. Remon's aetheryte/position are
// resolved at runtime (ZetaData), so the JSON step is a bare { "type": "AttachMahatma" }.
//
// Flow: (teleport to Swiftperch if needed) -> approach Remon -> interact -> "Mahatma Exchange"
// -> pick the equipped weapon (by relic name) -> pick the "(Available)" zodiac sign -> confirm
// the Poetics cost. The sign picker lingers as a SelectString after the NPC event reports "done",
// so the executor drives any open list menu regardless of that signal. Completion is read from
// memory: the current Mahatma's points reset so GameState.NeedsMahatmaAttach() flips to false.
//
// SEAM: Remon's exact option text/addon is not exposed by any offline data source. The
// attach option is matched on several case-insensitive needles ("mahatma"/"imbue"/...) in
// BOTH list addons (SelectString and SelectIconString), and the open menu is logged once
// (LogOpenMenus) so the real wording is visible if none match. The Poetics prompt is
// confirmed via SelectYesno. The step fails (not false-completes) if the attach does not
// register, so a wrong needle stalls safely rather than lying.
public sealed class AttachMahatmaExecutor : ITaskExecutor
{
    private const long AttachRegisterGraceMs = 3000;

    public StepType Handles => StepType.AttachMahatma;

    private enum Phase { Noop, WaitExit, Teleport, Interact }

    private readonly AetheryteTeleportExecutor _teleport = new();
    private readonly NpcInteractor _npc = new();

    private Phase _phase;
    private long _doneDeadline;
    private StepData? _teleStep;
    private string _lastMenuSig = string.Empty;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _doneDeadline = 0;
        _teleStep = null;
        _lastMenuSig = string.Empty;

        if (!GameState.NeedsMahatmaAttach())
        {
            // A Mahatma is already attached (or none is needed) -> nothing to do.
            _phase = Phase.Noop;
            return;
        }

        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        // Coming straight off the farm duty's last boss, the player can still be bound by the
        // instance (the duty-complete / eject window). Teleport is blocked there, so wait for the
        // overworld before starting the Remon trip rather than firing teleports from inside.
        if (BoundByDuty())
        {
            _phase = Phase.WaitExit;
            return;
        }

        StartRemonTrip(ctx);
    }

    private void StartRemonTrip(ExecutionContext ctx)
    {
        if (ZetaData.RemonAetheryte != 0)
        {
            _teleStep = new StepData { Type = StepType.AetheryteTeleport, AetheryteId = ZetaData.RemonAetheryte };
            _teleport.Start(_teleStep, ctx);
            _phase = Phase.Teleport;
        }
        else
        {
            _npc.Reset();
            _phase = Phase.Interact;
        }
    }

    // Still inside a duty instance (any of the three bound-by-duty flags) -> teleport is blocked.
    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Authoritative completion: a Mahatma is attached again (remainder non-zero), or
        // the stage no longer needs an attach (all 12 done / no Braves weapon equipped).
        if (!GameState.NeedsMahatmaAttach())
            return ExecutorStatus.Complete;

        switch (_phase)
        {
            case Phase.WaitExit:
                // Still in the instance after the farm duty; teleport is blocked. Wait for the
                // eject, then start the Remon trip from the overworld.
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                StartRemonTrip(ctx);
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

            case Phase.Interact:
                var p = _npc.Tick(ZetaData.RemonNpcId, ZetaData.RemonPosition, ctx);

                if (p == InteractionPhase.Failed)
                    return ExecutorStatus.Failed;

                // Drive the menu chain whenever a list menu is open, even if the interactor reports
                // the conversation "done": Remon's final "select the mahatma you wish to obtain"
                // picker lingers as a SelectString after the NPC event ends, so gating on InDialogue
                // alone skipped it and the attach never registered.
                if (DialogueMenu.AnyOpen())
                {
                    // Log each DISTINCT menu as the flow advances, so any new submenu is visible.
                    var sig = DialogueMenu.OpenSignature();
                    if (sig.Length > 0 && sig != _lastMenuSig)
                    {
                        DialogueMenu.LogOpenMenus("Mahatma attach (Remon)");
                        DebugLog.Info($"Mahatma attach: equipped relic name='{GameState.EquippedBravesWeaponName()}'");
                        _lastMenuSig = sig;
                    }
                    // Pick the right option for whichever picker is up (action / weapon / sign), then
                    // confirm the Poetics cost. Each call no-ops once that menu has closed.
                    TrySelectAttach();
                    DialogueMenu.ConfirmYes();
                    _doneDeadline = 0; // a menu is still up; the conversation is not finished
                    return ExecutorStatus.InProgress;
                }

                if (p == InteractionPhase.Done)
                {
                    // No menu open and the conversation ended; allow a moment for the attach to
                    // register (the top-of-Update NeedsMahatmaAttach check completes the step the
                    // instant it does).
                    if (_doneDeadline == 0)
                        _doneDeadline = Environment.TickCount64 + AttachRegisterGraceMs;
                    else if (Environment.TickCount64 > _doneDeadline)
                    {
                        DebugLog.Warn(
                            "Mahatma attach: Remon dialogue ended but no Mahatma attached. " +
                            "Out of Poetics, or an attach menu option was not matched.");
                        return ExecutorStatus.Failed;
                    }
                }

                return ExecutorStatus.InProgress;

            default:
                return ExecutorStatus.Complete; // Noop
        }
    }

    // Remon's attach option lives in a SelectString or SelectIconString; the wording is not in
    // any offline data, so try the likely needles in both list addons. The first match fires.
    private static readonly string[] AttachAddons = { "SelectString", "SelectIconString" };
    // Stage-1 action option ("Mahatma Exchange" / "Imbue ..."). Deliberately NOT a bare "mahatma":
    // the stage-2 weapon-picker header "Select a Zodiac Weapon to receive mahatma." also contains
    // "mahatma", and selecting that header (a no-op line) is what stalled the menu. "exchange"
    // matches "Mahatma Exchange" without matching the header.
    private static readonly string[] AttachNeedles = { "exchange", "imbue", "attach", "infuse" };

    private static bool TrySelectAttach()
    {
        var weapon = GameState.EquippedBravesWeaponName();
        foreach (var addon in AttachAddons)
        {
            if (!DialogueMenu.IsOpen(addon))
                continue;

            // Stage 2 (weapon picker): pick the weapon line, skipping the header
            // ("Select a Zodiac Weapon to receive mahatma.") and the cancel ("Nothing"). The
            // weapon line carries the relic's name (language-proof) and an "(N remaining)" suffix;
            // match either. Iterating entries with explicit indices avoids the header-needle trap.
            // The list's string entries include a leading prompt/header that is display-only and is
            // NOT a selectable callback option, so the callback index is NOT the entry ordinal. Track
            // a separate option index that advances only on real options: selecting by the raw ordinal
            // lands one slot too high on "Nothing" (cancel), which silently ends the menu.
            var optionIndex = -1;
            foreach (var (_, label) in DialogueMenu.ListEntries(addon))
            {
                var lower = label.ToLowerInvariant();

                // Prompt/header line ("...receive mahatma" / "...wish to obtain"): not selectable, so
                // do NOT advance the option index.
                if (lower.Contains("receive mahatma") || lower.Contains("zodiac weapon") || lower.Contains("wish to obtain"))
                    continue;

                optionIndex++; // a real, selectable option (weapon / sign / Nothing)

                if (lower.Contains("nothing"))
                    continue; // counted so later options index correctly, but never chosen

                // Stage 3 (sign picker): the zodiac sign marked "(Available)" -- the next to obtain.
                if (lower.Contains("(available)"))
                {
                    DialogueMenu.Select(addon, optionIndex);
                    return true;
                }

                // Stage 2 (weapon picker): the weapon line (relic name or "(N remaining)").
                if ((!string.IsNullOrEmpty(weapon) && label.Contains(weapon, StringComparison.OrdinalIgnoreCase))
                    || lower.Contains("remaining"))
                {
                    DialogueMenu.Select(addon, optionIndex);
                    return true;
                }
            }

            // Stage 1: the action option that opens the weapon picker ("Mahatma Exchange" / imbue).
            foreach (var needle in AttachNeedles)
                if (DialogueMenu.SelectByText(addon, needle))
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
    }
}
