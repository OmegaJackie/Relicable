using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Diagnostics;

namespace Relicable.Steps.Combat;

// Handles death during a run: instead of stopping, it returns the player to a
// home aetheryte and signals the controller to resume the current objective from
// its start (which begins with a teleport, so the character re-navigates).
internal sealed unsafe class DeathRecovery
{
    public enum Result { NotDead, Reviving, JustRevived }

    // "Return" General Action id (return to home aetheryte). Verified against the
    // GeneralAction sheet (XIVAPI): 7 Teleport, 8 Return.
    private const uint ReturnGeneralActionId = 8;
    private const long ReturnThrottleMs = 4000;

    private bool _reviving;
    private long _lastReturn;

    public Result Tick()
    {
        var p = Plugin.ObjectTable.LocalPlayer;
        var dead = p != null
            && p.CurrentHp == 0
            && !Plugin.Condition[ConditionFlag.BetweenAreas]
            && !Plugin.Condition[ConditionFlag.BetweenAreas51];

        if (!dead)
        {
            if (_reviving)
            {
                _reviving = false;
                DebugLog.Info("Death recovery: revived; resuming current objective");
                return Result.JustRevived;
            }
            return Result.NotDead;
        }

        _reviving = true;
        if (Environment.TickCount64 - _lastReturn >= ReturnThrottleMs)
        {
            _lastReturn = Environment.TickCount64;
            var am = ActionManager.Instance();
            if (am != null)
            {
                am->UseAction(ActionType.GeneralAction, ReturnGeneralActionId);
                DebugLog.Verbose("Death recovery: issuing Return");
            }
        }
        return Result.Reviving;
    }
}
