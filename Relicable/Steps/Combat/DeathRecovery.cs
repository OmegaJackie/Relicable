using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Diagnostics;

namespace Relicable.Steps.Combat;

// Handles death during a run: instead of stopping, it returns the player to a
// home aetheryte and signals the controller to resume the current objective from
// its start (which begins with a teleport, so the character re-navigates).
//
// THE STATE MACHINE IS THE WHOLE JOB, and getting it wrong produced the reported "you can never
// resurrect -- it keeps dying and respawning dead by the aetheryte". The old check read
//
//     dead = CurrentHp == 0 && !BetweenAreas && !BetweenAreas51
//
// so pressing Return made us "alive" instantly: Return starts a zone transition, BetweenAreas goes
// true, and that clause alone flipped `dead` to false while the character was still a corpse. From
// a live log -- Return issued at 10:13:03.623, "revived; resuming current objective" at
// 10:13:03.720, ninety-seven milliseconds later, before the zone had even changed
// (BossMod Reborn logged BetweenAreas=False at 10:13:05.418). The controller then resumed, found
// itself still dead on arrival, latched death again and re-fired Return on the 4s throttle
// (10:13:07.627 in the same log) -- forever.
//
// The two rules that follow from that:
//   * A ZONE TRANSITION IS NOT INFORMATION about whether we are dead. HP reads 0 and the local
//     player object is being rebuilt, so both answers are unreliable; hold the previous state and
//     decide nothing until it ends.
//   * Neither reading is trusted on a single frame. Death has to hold before we spend a Return on
//     it (a transient HP-0 read as a zone finishes loading would otherwise fire one), and the
//     revive has to hold before the controller is told to resume.
internal sealed unsafe class DeathRecovery
{
    public enum Result { NotDead, Reviving, JustRevived }

    // "Return" General Action id (return to home aetheryte). Verified against the
    // GeneralAction sheet (XIVAPI): 7 Teleport, 8 Return.
    private const uint ReturnGeneralActionId = 8;
    private const long ReturnThrottleMs = 4000;
    // Death must hold this long before we act on it, so the HP-0 read on the frames either side of
    // a zone load cannot trigger a Return on a living character.
    private const long DeathConfirmMs = 1000;
    // ...and being alive must hold this long before we report the revive, so a single optimistic
    // frame cannot hand the controller a corpse to resume with.
    private const long ReviveSettleMs = 1500;
    // Still dead this long after the first Return means something is blocking the recovery
    // (Return on cooldown, a death window waiting on an answer). Say so instead of retrying mutely.
    private const long StuckWarnMs = 45_000;
    // How long after our own Return we will answer a confirmation prompt (see ConfirmReturnPrompt).
    private const long ReturnConfirmWindowMs = 6000;
    // Throttle for the "the game refused Return" line, so a long cooldown logs occasionally.
    private const long BlockedLogMs = 15_000;

    private bool _reviving;
    private long _revivingSince;
    private long _deadSince;
    private long _aliveSince;
    private long _lastReturn;
    private long _lastStuckWarn;
    private long _lastBlockedLog;

    public Result Tick()
    {
        var now = Environment.TickCount64;
        var p = Plugin.ObjectTable.LocalPlayer;
        var zoning = Plugin.Condition[ConditionFlag.BetweenAreas]
                     || Plugin.Condition[ConditionFlag.BetweenAreas51];

        // Mid-transition (or no player object yet): decide nothing, and above all do not read the
        // transition itself as proof of life -- that was the bug. Hold whatever we already knew,
        // which keeps the controller parked while a revive is in flight and leaves an ordinary
        // teleport step (where _reviving is false) completely unaffected.
        if (zoning || p == null)
        {
            _deadSince = 0;
            _aliveSince = 0;
            return _reviving ? Result.Reviving : Result.NotDead;
        }

        // Unconscious is the game's own death flag; the HP read is kept as a second opinion for the
        // frame or two where the flag lags.
        var dead = Plugin.Condition[ConditionFlag.Unconscious] || p.CurrentHp == 0;

        if (dead)
        {
            _aliveSince = 0;
            if (_deadSince == 0)
                _deadSince = now;
            // Not confirmed yet: report what we already believed rather than committing either way.
            if (now - _deadSince < DeathConfirmMs)
                return _reviving ? Result.Reviving : Result.NotDead;

            if (!_reviving)
            {
                _reviving = true;
                _revivingSince = now;
                _lastReturn = 0;
                _lastStuckWarn = 0;
                _lastBlockedLog = 0;
                DebugLog.Info("Death recovery: died; returning to the home aetheryte");
            }
            IssueReturn(now);
            ConfirmReturnPrompt(now);
            WarnIfStuck(now);
            return Result.Reviving;
        }

        _deadSince = 0;
        if (!_reviving)
            return Result.NotDead;

        // Alive again -- but only say so once it has held. Until then we are still recovering.
        if (_aliveSince == 0)
            _aliveSince = now;
        if (now - _aliveSince < ReviveSettleMs)
            return Result.Reviving;

        _reviving = false;
        _revivingSince = 0;
        _aliveSince = 0;
        _lastReturn = 0;
        _lastStuckWarn = 0;
        DebugLog.Info("Death recovery: revived; resuming current objective");
        return Result.JustRevived;
    }

    private void IssueReturn(long now)
    {
        if (now - _lastReturn < ReturnThrottleMs)
            return;
        var am = ActionManager.Instance();
        if (am == null)
            return;

        // Ask the game FIRST whether Return can be used. A blind UseAction that gets refused --
        // Return still on its cooldown, or content where it is simply unavailable -- looks exactly
        // like one that worked: the character stays a corpse and the retry loop reads as the plugin
        // doing nothing at all. Status 0 means usable; anything else is the game's own reason code,
        // which is far more useful in a log than silence. The throttle is NOT stamped on this path,
        // so the moment the block clears the next tick can fire rather than waiting out the window.
        var status = am->GetActionStatus(ActionType.GeneralAction, ReturnGeneralActionId);
        if (status != 0)
        {
            if (now - _lastBlockedLog >= BlockedLogMs)
            {
                _lastBlockedLog = now;
                DebugLog.Warn($"Death recovery: the game is refusing Return right now (status {status}) -- " +
                    "usually its cooldown, or content it cannot be used in. Retrying; raise or return " +
                    "manually and the run picks up from the current objective.");
            }
            return;
        }

        _lastReturn = now;
        var accepted = am->UseAction(ActionType.GeneralAction, ReturnGeneralActionId);
        DebugLog.Verbose($"Death recovery: issuing Return (accepted={accepted})");
    }

    // Answer the "Return to your home point?" confirmation, so recovery never depends on TextAdvance
    // or YesAlready being installed and switched on -- without one, the prompt sits there, the
    // character stays dead, and the 4s retry just raises it again.
    //
    // Scoped hard, because a blanket Yes is never acceptable: only while we are CONFIRMED dead
    // (the caller reaches this from the dead branch alone) and only in the few seconds after OUR
    // OWN Return. In that window the prompts the game can raise are the return confirm and a raise
    // offer, and Yes is the wanted answer to both.
    private void ConfirmReturnPrompt(long now)
    {
        if (_lastReturn == 0 || now - _lastReturn > ReturnConfirmWindowMs)
            return;
        if (Interaction.DialogueMenu.ConfirmYes())
            DebugLog.Verbose("Death recovery: confirmed the Return prompt");
    }

    private void WarnIfStuck(long now)
    {
        if (_revivingSince == 0 || now - _revivingSince < StuckWarnMs)
            return;
        if (now - _lastStuckWarn < StuckWarnMs)
            return;
        _lastStuckWarn = now;
        DebugLog.Warn($"Death recovery: still dead {(now - _revivingSince) / 1000}s after the first Return. " +
            "Return may be on cooldown, or the death window is waiting on an answer -- raise or return " +
            "manually and the run picks up from the current objective.");
    }
}
