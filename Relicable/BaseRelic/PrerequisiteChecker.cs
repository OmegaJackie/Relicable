using System.Collections.Generic;
using System.Linq;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.BaseRelic;

// Evaluates "A Relic Reborn" readiness against live game state for one job, the
// Questionable-style "is it done?" engine for the base relic. Everything it reports is
// read from game memory at call time (quest flags, inventory, the cached retainer
// scan, the active job and level); nothing is persisted as progress.
//
// What is authoritative now:
//   - Global prerequisite quests        -> QuestManager.IsQuestComplete (exact)
//   - Level 50 on the relic job          -> ClassJob + Level (exact when on the job)
//   - Material availability (shopping)   -> inventory + retainer cache (exact)
//   - Live relic-quest sequence          -> QuestManager.GetQuestSequence (exact)
//   - Per-part drop possession           -> inventory / key-item count (positive-only)
//
// What is a documented seam (see BaseRelicData.GlobalParts): the exact quest-sequence
// value at which each part finishes. Until those are calibrated in-game, fine-grained
// per-part completion for the parts without a drop item is reported as Unknown and the
// raw live sequence is surfaced so the values can be read off and filled in.
public sealed class PrerequisiteChecker
{
    private readonly Configuration _config;

    public PrerequisiteChecker(Configuration config) => _config = config;

    // The job the report is for: the override if set, otherwise auto-detected from the
    // equipped job. 'detected' is true when a concrete job was resolved either way.
    public RelicJob ResolveJob(out bool detected)
    {
        if (_config.BaseRelicJobOverride != RelicJob.None)
        {
            detected = true;
            return _config.BaseRelicJobOverride;
        }
        var job = RelicJobs.FromClassJobId(GameState.ActiveClassJobId());
        detected = job != RelicJob.None;
        return job;
    }

    public PrerequisiteReport Build()
    {
        var job = ResolveJob(out var detected);
        return BuildReport(job, detected);
    }

    // Build the report for an EXPLICIT job (the questmap job selector), so a first-timer can
    // preview any job's line without switching to it or setting the config override. Level is
    // still only readable for the active job, so a previewed non-active job reports its level
    // requirement as Unknown -- the same as the auto path when not on the job.
    public PrerequisiteReport BuildFor(RelicJob job)
        => BuildReport(job, job != RelicJob.None);

    private PrerequisiteReport BuildReport(RelicJob job, bool detected)
    {
        var activeJob = RelicJobs.FromClassJobId(GameState.ActiveClassJobId());
        var isActive = job != RelicJob.None && job == activeJob;
        var level = isActive ? GameState.ActiveJobLevel() : 0;

        var levelReq = BuildLevelRequirement(job, isActive, level);
        var globals = BuildGlobalPrerequisites();
        var materials = BuildMaterials(job);

        var relicQuestName = BaseRelicState.RelicQuestNameFor(job);
        var relicQuestId = BaseRelicState.RelicQuestIdFor(job);
        var liveSeq = BaseRelicState.RelicQuestSequenceFor(job);
        var everComplete = relicQuestId != 0 && GameState.IsQuestComplete(relicQuestId);
        var parts = BuildParts(job, liveSeq, everComplete);

        // Gating: the relic line can begin when every prerequisite quest is complete
        // and the level requirement is satisfied. Material and per-part readiness are
        // reported but do not gate this flag.
        var gatingMet = levelReq.State == RequirementState.Satisfied
            && QuestComplete(BaseRelicData.UnlockQuestName)
            && BaseRelicData.GlobalPrerequisites.All(p => QuestComplete(p.QuestName));

        return new PrerequisiteReport
        {
            Job = job,
            JobWasDetected = detected,
            JobIsActive = isActive,
            JobLevel = level,
            JobLevelRequirement = levelReq,
            GlobalPrerequisites = globals,
            Materials = materials,
            RelicQuestName = relicQuestName,
            RelicQuestId = relicQuestId,
            LiveQuestSequence = liveSeq,
            RelicQuestEverCompleted = everComplete,
            Parts = parts,
            PrerequisitesMet = gatingMet,
        };
    }

    private static CheckedRequirement BuildLevelRequirement(RelicJob job, bool isActive, int level)
    {
        if (job == RelicJob.None)
            return new CheckedRequirement
            {
                Label = "Level 50 on the relic job",
                State = RequirementState.Unknown,
                Detail = "No job detected. Switch to the relic job or set a job override.",
            };
        if (!isActive)
            return new CheckedRequirement
            {
                Label = $"Level 50 {RelicJobs.DisplayName(job)}",
                State = RequirementState.Unknown,
                Detail = "Switch to this job to verify its level (level is only readable for the active job).",
            };
        return new CheckedRequirement
        {
            Label = $"Level 50 {RelicJobs.DisplayName(job)}",
            State = level >= 50 ? RequirementState.Satisfied : RequirementState.Unsatisfied,
            Detail = $"Current level {level}.",
        };
    }

    private static List<CheckedRequirement> BuildGlobalPrerequisites()
    {
        // The one-time quests directly attached to the Zodiac line: the line unlock, then the
        // three content-unlocks the parts require. These are checked and they gate readiness.
        var list = new List<CheckedRequirement>
        {
            QuestRequirement(BaseRelicData.UnlockQuestName, "Unlocks the relic line (one-time, Nedrick Ironheart)"),
        };
        foreach (var p in BaseRelicData.GlobalPrerequisites)
            list.Add(QuestRequirement(p.QuestName, p.Purpose));

        // MSQ finale: informational context ONLY. Per scope it is excluded from the readiness
        // gate (it is a general story gate, not a quest attached to the Zodiac line); shown so a
        // first-timer understands why the line may not yet be acceptable in-game.
        var msqId = BaseRelicCatalog.QuestId(BaseRelicData.MsqGateQuestName);
        list.Add(new CheckedRequirement
        {
            Label = $"{BaseRelicData.MsqGateQuestName} (ARR MSQ finale)",
            State = msqId == 0
                ? RequirementState.Unknown
                : GameState.IsQuestComplete(msqId) ? RequirementState.Satisfied : RequirementState.Unsatisfied,
            Detail = "Informational only -- gates accepting the line in-game, but excluded from Relicable's readiness check.",
        });

        // Informational: the full class/job quest chain to 50 is not individually
        // tracked here, so it is surfaced as Unknown with guidance rather than asserted.
        list.Add(new CheckedRequirement
        {
            Label = "Class and job quests to level 50",
            State = RequirementState.Unknown,
            Detail = "Not individually tracked; ensure your level-50 job quest is complete.",
        });
        return list;
    }

    private static CheckedRequirement QuestRequirement(string questName, string purpose)
    {
        var id = BaseRelicCatalog.QuestId(questName);
        if (id == 0)
            return new CheckedRequirement
            {
                Label = questName,
                State = RequirementState.Unknown,
                Detail = $"Quest id unresolved. {purpose}",
            };
        return new CheckedRequirement
        {
            Label = questName,
            State = GameState.IsQuestComplete(id) ? RequirementState.Satisfied : RequirementState.Unsatisfied,
            Detail = purpose,
        };
    }

    private static bool QuestComplete(string questName)
    {
        var id = BaseRelicCatalog.QuestId(questName);
        return id != 0 && GameState.IsQuestComplete(id);
    }

    private List<CheckedMaterial> BuildMaterials(RelicJob job)
    {
        var result = new List<CheckedMaterial>();
        if (job == RelicJob.None)
            return result;

        foreach (var m in BaseRelicData.MaterialsFor(job))
        {
            var id = BaseRelicCatalog.ItemId(m.ItemName);
            result.Add(new CheckedMaterial
            {
                ItemName = m.ItemName,
                ItemId = id,
                Needed = m.Quantity,
                InInventory = id == 0 ? 0 : GameState.InventoryCount(id),
                OnRetainers = id == 0 ? 0 : _config.RetainerBaseRelicItems.TotalFor(id),
                Source = m.Source,
                SourceDetail = m.SourceDetail,
            });
        }
        return result;
    }

    private static List<CheckedPart> BuildParts(RelicJob job, int liveSeq, bool everComplete)
    {
        var parts = new List<CheckedPart>();
        foreach (var part in BaseRelicData.GlobalParts)
        {
            RequirementState state;
            string detail;

            if (part.CompletedAtSequence > 0 && liveSeq >= part.CompletedAtSequence)
            {
                state = RequirementState.Satisfied;
                detail = $"Quest sequence {liveSeq} reached part threshold {part.CompletedAtSequence}.";
            }
            else if (part.HaveItemName != null && HoldsPartItem(part))
            {
                state = RequirementState.Satisfied;
                detail = $"{part.HaveItemName} held (objective done; pending turn-in).";
            }
            else if (liveSeq > 0)
            {
                state = RequirementState.Unknown;
                // With a calibrated threshold the part is genuinely still open (the quest has not
                // reached it yet), which is a different statement from "we cannot tell"; say which.
                var where = part.CompletedAtSequence > 0
                    ? $"Relic in progress (live sequence {liveSeq}); this part completes at sequence {part.CompletedAtSequence}."
                    : $"Relic in progress (live sequence {liveSeq}); per-part sequence not yet calibrated.";
                detail = part.HaveItemName != null
                    ? $"Relic in progress (live sequence {liveSeq}); {part.HaveItemName} not held (not yet obtained or already turned in)."
                    : where;
            }
            else if (everComplete)
            {
                state = RequirementState.Unknown;
                detail = "No relic currently in progress (relic quest completed before).";
            }
            else
            {
                state = RequirementState.Unsatisfied;
                detail = "Relic quest not started.";
            }

            parts.Add(new CheckedPart
            {
                Part = part.Part,
                Name = part.Name,
                State = state,
                Detail = detail + StepHint(part) + JobObjectiveHint(job, part),
            });
        }
        return parts;
    }

    // Appends the navigation target (label + map coords, with Z when known) and, for the
    // trial parts, whether the duty is already unlocked (just queue it) or still needs
    // its entrance examined. This is what turns each part line into an actionable step.
    private static string StepHint(QuestPart part)
    {
        var bits = new List<string>();

        if (part.Location is { } loc)
        {
            // Invariant culture: "0.0" under a comma-decimal culture prints "13,8, 27,0",
            // which is ambiguous inside the comma-separated coordinate list.
            var coords = loc.HasHeight
                ? $"{Coord(loc.MapX)}, {Coord(loc.MapY)}, Z {Coord(loc.MapZ)}"
                : $"{Coord(loc.MapX)}, {Coord(loc.MapY)}";
            bits.Add($"{loc.Label} ({coords})");
        }

        if (part.DutyName is { } duty)
        {
            var iid = BaseRelicCatalog.DutyInstanceContentId(duty);
            if (iid == 0)
                bits.Add($"duty '{duty}': unlock state unknown");
            else if (GameState.IsDutyUnlocked(iid))
                bits.Add($"duty '{duty}': unlocked -- queue from Duty Finder");
            else
                bits.Add($"duty '{duty}': not unlocked -- examine the entrance to unlock");
        }

        return bits.Count == 0 ? string.Empty : "  |  " + string.Join("; ", bits);
    }

    // Job-specific objectives surfaced on the relevant parts: the Part 1 broken-weapon
    // stronghold and the Part 5 beastmen targets (8 each), so the report shows exactly
    // what to do for the equipped job.
    private static string JobObjectiveHint(RelicJob job, QuestPart part)
    {
        var data = BaseRelicData.For(job);
        if (data == null)
            return string.Empty;

        if (part.Part == 1)
        {
            var bw = data.BrokenWeapon;
            return $"  |  {bw.Label} ({Coord(bw.MapX)}, {Coord(bw.MapY)})";
        }
        if (part.Part == 5)
        {
            var hunt = data.BeastmenHunt;
            var mobs = string.Join(", ", data.Beastmen.Select(b => $"{b.Name} x{b.Count}"));
            return $"  |  {hunt.Label} ({Coord(hunt.MapX)}, {Coord(hunt.MapY)}): {mobs}";
        }
        return string.Empty;
    }

    // ASCII, dot-decimal map coordinate (see StepHint).
    private static string Coord(float v)
        => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // Positive-only possession check for a part's quest item (drop or key item). Holding
    // it proves the part's objective was done (pending turn-in); not holding it does not
    // prove the opposite (it may already be turned in), hence positive-only.
    private static bool HoldsPartItem(QuestPart part)
    {
        if (part.HaveItemName == null)
            return false;
        if (part.ItemIsKeyItem)
        {
            var kid = BaseRelicCatalog.KeyItemId(part.HaveItemName);
            return kid != 0 && GameState.KeyItemCount(kid) > 0;
        }
        var id = BaseRelicCatalog.ItemId(part.HaveItemName);
        return id != 0 && GameState.InventoryCount(id) > 0;
    }
}

// Renders a PrerequisiteReport as plain ASCII lines for the log / chat. (The visual
// configuration window is a later pass; this keeps the foundation testable in-game via
// the /relic prereq command.)
public static class PrerequisiteReportFormatter
{
    public static IReadOnlyList<string> ToLines(PrerequisiteReport r)
    {
        var lines = new List<string>();

        var jobLabel = r.Job == RelicJob.None ? "Unknown job" : RelicJobs.DisplayName(r.Job);
        var src = r.Job == RelicJob.None
            ? "no job detected"
            : r.JobWasDetected ? (r.JobIsActive ? "active job" : "override / not the active job") : "detected";
        lines.Add($"Relicable base-relic readiness -- {jobLabel} ({src})");
        lines.Add($"Prerequisites to begin: {(r.PrerequisitesMet ? "MET" : "NOT MET")}");

        lines.Add("Prerequisites:");
        lines.Add($"  {Box(r.JobLevelRequirement.State)} {r.JobLevelRequirement.Label} -- {r.JobLevelRequirement.Detail}");
        foreach (var g in r.GlobalPrerequisites)
            lines.Add($"  {Box(g.State)} {g.Label} -- {g.Detail}");

        lines.Add("Materials (need / inventory / retainers):");
        if (r.Materials.Count == 0)
            lines.Add("  (no job selected)");
        foreach (var m in r.Materials)
            lines.Add($"  {Box(m.State)} {m.ItemName}  need {m.Needed}, have {m.Total} (inv {m.InInventory} / ret {m.OnRetainers})" +
                      (string.IsNullOrEmpty(m.SourceDetail) ? string.Empty : $"  -- {m.SourceDetail}"));

        var questState = r.LiveQuestSequence > 0
            ? $"ACTIVE (sequence {r.LiveQuestSequence})"
            : r.RelicQuestEverCompleted ? "not active (finished before)" : "not started";
        var questLabel = string.IsNullOrEmpty(r.RelicQuestName) ? "(no job)" : r.RelicQuestName;
        lines.Add($"Relic quest '{questLabel}' (id {r.RelicQuestId}): {questState}");
        lines.Add("Quest parts:");
        foreach (var p in r.Parts)
            lines.Add($"  {Box(p.State)} {p.Part}. {p.Name} -- {p.Detail}");

        return lines;
    }

    private static string Box(RequirementState state) => state switch
    {
        RequirementState.Satisfied => "[x]",
        RequirementState.Unsatisfied => "[ ]",
        _ => "[?]",
    };
}
