using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Diagnostics;

namespace Relicable.Steps.Combat;

// Summons the chocobo companion and sets its stance for the open-world grind.
// Uses ActionManager (verified): Gysahl Greens is a normal item use, and the
// chocobo stances are ActionType.BuddyAction. Presence is read from Dalamud's
// IBuddyList.CompanionBuddy.
internal static unsafe class Companion
{
    private const uint GysahlGreensItemId = 4868;

    // Chocobo stances are ActionType.BuddyAction. Verified against the BuddyAction
    // sheet (XIVAPI): 4 Free Stance, 5 Defender Stance, 6 Attacker Stance,
    // 7 Healer Stance.
    private const uint HealerStanceBuddyActionId = 7;

    private const long ThrottleMs = 3000;

    private static long _lastTick;
    private static bool _stanceSet;

    // The summoned chocobo's object id, or 0 when none is out. Handed to the aggressor scan
    // (Targeting.FindNearestAggressor): a mob whose enmity has flipped onto the companion is
    // still an enemy engaged with us, and the healer stance out-threats the player during any
    // window where the rotation is off. Without it that mob was invisible and the kill loop
    // travelled on with the rotation disabled while it kept hitting the chocobo.
    public static ulong CompanionId()
        => Plugin.Buddies.CompanionBuddy?.GameObject?.GameObjectId ?? 0;

    public static void EnsureReady(bool summon, bool healerStance)
    {
        if (!summon)
            return;
        if (Environment.TickCount64 - _lastTick < ThrottleMs)
            return;
        _lastTick = Environment.TickCount64;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        // Chocobos cannot be summoned inside instanced duties.
        if (Plugin.Condition[ConditionFlag.BoundByDuty]
            || Plugin.Condition[ConditionFlag.BoundByDuty56]
            || Plugin.Condition[ConditionFlag.BoundByDuty95])
            return;

        var am = ActionManager.Instance();
        if (am == null)
            return;

        if (Plugin.Buddies.CompanionBuddy == null)
        {
            _stanceSet = false;
            am->UseAction(ActionType.Item, GysahlGreensItemId, player.GameObjectId, 0xFFFF);
            DebugLog.Verbose("Companion: summoning chocobo (Gysahl Greens)");
            return;
        }

        if (healerStance && !_stanceSet)
        {
            if (am->UseAction(ActionType.BuddyAction, HealerStanceBuddyActionId))
            {
                _stanceSet = true;
                DebugLog.Verbose("Companion: set healer stance");
            }
        }
    }
}
