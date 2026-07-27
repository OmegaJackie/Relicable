using Dalamud.Game.ClientState.Conditions;

namespace Relicable.Steps.Interaction;

// Helpers over Dalamud condition flags for the "am I in an NPC conversation"
// question. Interaction steps use these to know when a dialogue has opened (so
// TextAdvance can carry it) and when it has closed (so the step can complete).
internal static class EventConditions
{
    // True while engaged in any NPC/quest/cutscene event flow.
    public static bool InEvent
        => Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]
           || Plugin.Condition[ConditionFlag.OccupiedInEvent]
           || Plugin.Condition[ConditionFlag.OccupiedSummoningBell]
           || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
           || Plugin.Condition[ConditionFlag.WatchingCutscene]
           || Plugin.Condition[ConditionFlag.WatchingCutscene78];

    // True when the player is free to act (not zoning, not in an event).
    public static bool Free
        => Plugin.ObjectTable.LocalPlayer != null
           && !InEvent
           && !Plugin.Condition[ConditionFlag.BetweenAreas]
           && !Plugin.Condition[ConditionFlag.BetweenAreas51];
}
