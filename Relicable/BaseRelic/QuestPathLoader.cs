using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using Relicable.Data;
using Relicable.Model;

namespace Relicable.BaseRelic;

// Loads qstxiv quest-path JSON files from Data/questpaths and converts each into
// Relic-stage, sequence-driven RelicObjectives. The filename carries the quest id and
// the relic weapon (e.g. "1125_A Relic Reborn (Artemis Bow).json"), from which the job
// is resolved. Each quest SEQUENCE becomes one objective tagged with ActiveAtSequence,
// so the controller runs the step matching the game's live quest sequence and advances
// when the game does. Unknown JSON keys / interaction types are skipped, not fatal.
public static class QuestPathLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // All quest-path objectives, plus the set of jobs covered (so the static hunt
    // generator can skip those jobs and avoid duplicate Relic-stage objectives).
    public static (IReadOnlyList<RelicObjective> Objectives, IReadOnlySet<RelicJob> CoveredJobs) LoadAll(
        IDalamudPluginInterface pi)
    {
        var objectives = new List<RelicObjective>();
        var covered = new HashSet<RelicJob>();
        var dir = Path.Combine(pi.AssemblyLocation.DirectoryName ?? ".", "Data", "questpaths");
        if (!Directory.Exists(dir))
            return (objectives, covered);

        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var (questId, job) = ParseFileName(Path.GetFileNameWithoutExtension(file));
                if (job == RelicJob.None)
                {
                    Plugin.Log.Warning($"Relicable: quest path '{Path.GetFileName(file)}' -> no relic job resolved; skipped");
                    continue;
                }

                var path = JsonSerializer.Deserialize<QuestPath>(File.ReadAllText(file), Options);
                if (path == null)
                    continue;

                var added = 0;
                foreach (var obj in Convert(path, questId, job))
                {
                    objectives.Add(obj);
                    added++;
                }
                if (added > 0)
                    covered.Add(job);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Relicable: failed to load quest path {file}: {ex.Message}");
            }
        }

        return (objectives, covered);
    }

    // "1125_A Relic Reborn (Artemis Bow)" -> (1125, Bard). Id is the leading number; the
    // job is resolved from the weapon name in parentheses.
    private static (uint QuestId, RelicJob Job) ParseFileName(string name)
    {
        uint questId = 0;
        var us = name.IndexOf('_');
        if (us > 0 && uint.TryParse(name.Substring(0, us), out var id))
            questId = id;

        var open = name.IndexOf('(');
        var close = name.LastIndexOf(')');
        var weapon = open >= 0 && close > open ? name.Substring(open + 1, close - open - 1).Trim() : string.Empty;
        return (questId, BaseRelicData.JobForRelicWeapon(weapon));
    }

    private static IEnumerable<RelicObjective> Convert(QuestPath path, uint questId, RelicJob job)
    {
        foreach (var seq in path.QuestSequence)
        {
            var steps = new List<StepData>();
            foreach (var s in seq.Steps)
            {
                var mapped = MapStep(s);
                if (mapped == null)
                    continue;
                // Auto-teleport to the step's zone first, so a step in another territory is
                // reachable without an explicit teleport step (the executor no-ops when the
                // player is already in that territory). This mirrors how Questionable travels
                // to each objective. Duty steps carry an instanced territory with no overworld
                // aetheryte, so AetheryteForTerritory returns 0 and no teleport is prepended.
                var aetheryte = s.TerritoryId != 0 ? Locations.AetheryteForTerritory(s.TerritoryId) : 0u;
                if (aetheryte != 0)
                    steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aetheryte });
                steps.Add(mapped);
            }
            if (steps.Count == 0)
                continue;

            yield return new RelicObjective
            {
                Stage = RelicStage.Relic,
                Job = job,
                Id = $"questpath-{questId}-seq{seq.Sequence:000}",
                DisplayName = $"{RelicJobs.DisplayName(job)} relic: quest sequence {seq.Sequence}",
                ActiveAtSequence = seq.Sequence,
                CompleteAtSequence = seq.Sequence,
                Steps = steps,
                Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
            };
        }
    }

    // Is this "Interact" step actually aimed at a world OBJECT rather than an NPC?
    //
    // Why this matters: the ONLY workflow the repo has for authoring the remaining jobs' paths
    // is an in-game capture (see tools/generated/quest_gap_checklist.md), and both that capture
    // and Questionable's own published paths express a coffer as a plain "Interact" carrying the
    // object's DataId -- not as a bespoke type. Routed to InteractNpc, such a step gets a finder
    // that matches BaseId only and a 3D 4y arrival gate that never closes on an object whose
    // origin sits above the floor: it would silently stall exactly the way the Bard path did.
    //
    // The discriminator is the id space: actors (NPCs) live below 2,000,000, while EObj rows --
    // coffers, levers, markers -- start at 2,000,000. An explicit "InteractObject" type remains
    // available for hand-authoring (and for naming the object when its id is unknown).
    private static bool IsWorldObject(QuestPathStep s)
        => s.DataId >= 2_000_000 || !string.IsNullOrEmpty(s.ObjectName);

    // Find-by-name-or-id and interact: a quest coffer, a lever, a marker. Two real differences
    // from the NPC path: (1) the finder is name-driven and ObjectKind-tolerant, with the DataId
    // only a stronger optional match, because a quest coffer's live ObjectKind is an unverified
    // seam and NpcInteractor.Find matches BaseId only; and (2) the approach must walk fully ONTO
    // the object -- its origin sits above the floor, so InteractNpc's 3D 4y gate can never close
    // ("a hair too far to open", TreasureMapExecutor.cs:304-308). StopDistance here is only the
    // TRAVEL stop for the streaming-in anchor; the executor owns its own, much tighter, final
    // approach against the object's LIVE position.
    private static StepData WorldObjectStep(QuestPathStep s, System.Numerics.Vector3? pos)
        => new()
        {
            Type = StepType.InteractObject,
            TargetName = s.ObjectName,
            TargetDataId = s.DataId,
            Position = pos,
            StopDistance = s.StopDistance,
        };

    // Map a quest-path step's InteractionType onto an engine StepData. Unhandled types
    // are skipped with a log so a path with an exotic step still loads the rest.
    private static StepData? MapStep(QuestPathStep s)
    {
        var pos = s.Position?.ToVector3();
        switch (s.InteractionType)
        {
            case "WalkTo":
                return new StepData { Type = StepType.MoveTo, Position = pos, StopDistance = s.StopDistance, Fly = s.Fly };

            case "InteractObject":
                return WorldObjectStep(s, pos);

            case "AcceptQuest":
            case "Interact":
            case "CompleteQuest":
                // A world OBJECT (a quest coffer, a lever, a marker) reached via a plain
                // "Interact" is NOT an NPC and must not get the NPC executor -- see
                // IsWorldObject. Only "Interact" is re-routed: a quest accept / turn-in is
                // always a real NPC (Gerolt), so those stay put regardless of id.
                if (s.InteractionType == "Interact" && IsWorldObject(s))
                    return WorldObjectStep(s, pos);
                // Quest accept / turn-in / talk: navigate to the NPC and let TextAdvance
                // carry the dialogue. Position streams the NPC in before targeting it.
                return new StepData { Type = StepType.InteractNpc, NpcDataId = s.DataId, Position = pos };

            case "Combat":
                return new StepData
                {
                    Type = StepType.KillTarget,
                    TargetName = s.EnemyName,
                    Count = s.KillCount > 0 ? s.KillCount : 1,
                    Position = pos,
                };

            case "Duty":
                var territory = s.DutyTerritoryType != 0
                    ? s.DutyTerritoryType
                    : (s.DutyName is { } d ? BaseRelicCatalog.DutyTerritoryId(d) : 0u);
                // Unsynced, like every generated base-relic duty (BaseRelicHuntGenerator):
                // without it AutoDuty queues the old content synced and never starts.
                return territory == 0
                    ? null
                    : new StepData { Type = StepType.EnterDuty, TerritoryType = territory, Loops = 1, Unsynced = true };

            default:
                Plugin.Log.Warning($"Relicable: quest path has unhandled InteractionType '{s.InteractionType}'; step skipped");
                return null;
        }
    }
}
