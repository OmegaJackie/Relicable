using System.Collections.Generic;
using Relicable.BaseRelic;
using Relicable.Data;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Braves;

// Generates Braves-stage dungeon objectives: for each material quest, the four dungeons whose
// drops it needs, run via AutoDuty (unsynced) and complete when the drop is in inventory. The
// controller runs only the ACTIVE material quest's objectives (filtered by RelicObjective.
// BravesQuest), so accepting a material quest "goes to that step" -- the engine runs its dungeons.
// The vendor/craft items and the quest turn-in stay in the planner (not automated here).
public static class BravesDungeonGenerator
{
    public static IReadOnlyList<RelicObjective> Generate()
    {
        var result = new List<RelicObjective>();
        foreach (var m in BravesData.Materials)
        {
            if (m.Source != BravesSource.DungeonDrop)
                continue;
            // The dungeon-drop "Where" is the dungeon name; resolve it to a TerritoryType for
            // AutoDuty (reusing the base-relic catalog's case-insensitive CFC resolver).
            var territory = BaseRelicCatalog.DutyTerritoryId(m.Where);
            if (territory == 0)
                continue;

            // The drop is a Key Item (KeyItems container), resolved via the EventItem sheet, so it
            // is counted with KeyItemCount -- ItemCount reads the normal bags and never sees it,
            // which would leave the objective forever incomplete (or force the AllStepsDone
            // fallback that marks the dungeon done after a single clear, drop or no drop).
            var dropItemId = BravesData.ItemId(m.ItemName);
            var dropKeyId = dropItemId == 0 ? BravesData.KeyItemId(m.ItemName) : 0u;
            CompletionCondition completion;
            if (dropKeyId != 0)
                completion = new CompletionCondition { Kind = CompletionKind.KeyItemCount, ItemId = dropKeyId, Threshold = 1 };
            else if (dropItemId != 0)
                completion = new CompletionCondition { Kind = CompletionKind.ItemCount, ItemId = dropItemId, Threshold = 1 };
            else
                completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone };

            result.Add(new RelicObjective
            {
                Stage = RelicStage.Braves,
                BravesQuest = m.Quest,
                // The live quest sequence(s) at which this drop is requested (empty until calibrated
                // via /relic bravesseq); the controller only runs the dungeon while the quest is there.
                ActiveAtQuestSequences = new List<int>(m.RequestedAtSequences),
                Id = $"braves-{m.Quest}-{m.ItemName}",
                DisplayName = $"Braves ({m.Quest}): {m.Where} -> {m.ItemName}",
                Steps = new List<StepData>
                {
                    // Unsynced so AutoDuty can solo the old ARR dungeon.
                    new() { Type = StepType.EnterDuty, TerritoryType = territory, Loops = 1, Unsynced = true },
                },
                // Done when the quest drop (a Key Item) is in the bag. After the quest is turned in
                // the drop is consumed, but by then the quest is no longer active, so the active-
                // quest filter (not this completion) is what stops the dungeon being re-run.
                Completion = completion,
            });
        }
        return result;
    }

    // Calibration readout for /relic bravesseq. For every material quest, reports whether it is
    // accepted and its LIVE sequence, and for each of its four dungeon drops the held count and the
    // (authored) sequence(s) at which the plugin thinks it is requested. Play each quest and, at the
    // step where the journal asks you to obtain a dungeon item, note the sequence printed here: that
    // is the RequestedAtSequences value for that drop. Empty 'requestedAtSeq' means uncalibrated
    // (the dungeon runs whenever the quest is accepted, the pre-calibration behaviour).
    public static IReadOnlyList<string> CalibrationReport()
    {
        var lines = new List<string>();
        foreach (var name in BravesData.MaterialQuests)
        {
            var qid = BravesData.MaterialQuestId(name);
            if (qid == 0)
            {
                lines.Add($"Braves quest '{name}': quest name did not resolve to a Quest-sheet id.");
                continue;
            }
            var seq = GameState.QuestSequence(qid);
            var status = seq > 0
                ? $"ACCEPTED, live sequence = {seq}"
                : (GameState.IsQuestComplete(qid) ? "completed" : "not accepted");
            lines.Add($"Braves quest '{name}' (id {qid}): {status}");

            foreach (var m in BravesData.Materials)
            {
                if (m.Source != BravesSource.DungeonDrop || m.Quest != name)
                    continue;
                var keyId = BravesData.KeyItemId(m.ItemName);
                var held = keyId == 0 ? 0 : GameState.KeyItemCount(keyId);
                var authored = m.RequestedAtSequences.Count == 0
                    ? "(uncalibrated)"
                    : string.Join(",", m.RequestedAtSequences);
                lines.Add($"    {m.ItemName} <- {m.Where}: held={held}, requestedAtSeq={authored}");
            }

            if (seq > 0)
                lines.Add($"  >> If the journal is asking for a dungeon item right now, its RequestedAtSequences = {seq}.");
        }
        return lines;
    }
}
