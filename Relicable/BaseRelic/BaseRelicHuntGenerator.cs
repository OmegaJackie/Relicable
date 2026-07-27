using System.Collections.Generic;
using System.Linq;
using Relicable.Data;
using Relicable.Model;

namespace Relicable.BaseRelic;

// Generates the base-relic (A Relic Reborn) duty and hunt objectives, one set per job, as
// Relic-stage objectives the controller runs in quest order:
//   p03 Chimera (A Relic Reborn: The Chimera, one-time trial) -> AutoDuty
//   p04 Amdapor Keep (dungeon) -> AutoDuty
//   p05 Beastmen hunt (24 kills) -> teleport + KillTarget steps
//   p06 Hydra (A Relic Reborn: The Hydra, one-time trial) -> AutoDuty
//   p07-09 Ifrit / Garuda / Titan (Hard) -> AutoDuty
//
// The base-relic quest is ONE collection phase: it advances on its own as you complete the
// content (kills, duty clears, item drops) and ends with a single turn-in at Gerolt -- it is
// NOT a report-to-Gerolt after each part. So each generated objective just DOES its content;
// the game tracks progress and advances the quest sequence. A duty's TerritoryType (for
// AutoDuty) is resolved from its ContentFinderCondition name (case-insensitive); an unresolved
// name is skipped rather than fatal.
//
// The beastmen hunt counts kills locally (the base-relic quest has no per-mob RelicNote
// counter) -- see KillTargetExecutor and the BACKLOG seam. The controller filters Relic-stage
// objectives to the equipped job (RelicObjective.Job), so only the active job's set runs.
public static class BaseRelicHuntGenerator
{
    // questPathJobs: jobs whose hand-authored quest-path JSON (Data/questpaths) already covers the
    // start-of-line sequences (accept / broken weapon / report). The generated Part 0-2 start block
    // is skipped for those so its ActiveAtSequence pathStep is not duplicated; Parts 3-10 are still
    // generated for every job (a quest-path file, e.g. Bard/1125, only supplies the early sequences).
    public static IReadOnlyList<RelicObjective> Generate(IReadOnlySet<RelicJob>? questPathJobs = null)
    {
        var result = new List<RelicObjective>();

        foreach (var job in RelicJobs.All)
        {
            var data = BaseRelicData.For(job);
            if (data == null || data.Beastmen.Count == 0)
                continue;

            // Parts 0-2 (accept the quest -> recover the broken weapon -> report to Gerolt),
            // sequence-driven, so an UN-STARTED relic on a supported ARR class is routed to the
            // broken-weapon location as its very first step -- instead of the engine jumping ahead to
            // the Chimera (part 3, whose ActiveFromSequence is 0, so it was otherwise eligible even at
            // sequence 0 before the quest was accepted). Skipped for a job whose quest-path JSON
            // already supplies these early sequences (Bard/1125), to avoid a duplicate ActiveAtSequence
            // pathStep; that job still gets Parts 3-10 below.
            if (questPathJobs == null || !questPathJobs.Contains(job))
                AddBrokenWeaponStart(result, job, data);

            // Quest order: the pre-hunt duties (Chimera, Amdapor Keep), then the beastmen hunt,
            // then the post-hunt trials. Emitting them in this order means a fresh relic runs
            // top to bottom; a relic already past a part skips it (quest-authoritative).
            foreach (var duty in Duties.Where(d => d.Part < 5))
                AddDutyObjective(result, job, duty);

            AddBeastmenObjective(result, job, data);

            foreach (var duty in Duties.Where(d => d.Part > 5))
                AddDutyObjective(result, job, duty);

            // Between-trial Gerolt turn-ins. The base-relic quest parks at these sequences until you
            // report to / deliver items to Gerolt before the next trial can begin. Verified across all
            // 10 jobs' quest text (all Gerolt): deliver the Alumina Salts after the Chimera (seq 7 -> 8),
            // report to Gerolt after the Amdapor Glyph trade to Rowena (seq 9 -> 10), report the beastman
            // hunt (seq 11 -> 12), report the Hydra (13 -> 14), hand over the unfinished relic (14 -> 15),
            // and deliver the three primal drops (18 -> 19). Each is a teleport + interact, gated to its
            // exact sequence.
            //
            // The Alumina Salts hand-over (seq 7) BLOCKS Amdapor Keep (part 4, gated to seq 8): reported
            // live, after the Chimera the run tried to enter Amdapor Keep, but the quest parks at seq 7 to
            // hand the Alumina Salts to Gerolt first. Amdapor Keep's ActiveFromSequence (BaseRelicData
            // part 4) now holds it until this turn-in advances the quest to seq 8.
            //
            // The seq-9 Gerolt report follows the Amdapor Glyph trade to Rowena: reported live, after
            // Amdapor Keep the quest parks (seq 8) for the Rowena glyph trade (advancing to seq 9, still a
            // manual SEAM -- Rowena's shop id is not authored), and then needs a report to Gerolt that the
            // run did not auto-route to. This turn-in supplies it, advancing seq 9 -> 10 (where the
            // beastman hunt, ActiveFromSequence 10, then becomes active). SEAM: the seq-9 placement is
            // constrained (Amdapor at 8, beastmen verified at 10) but wants an in-game confirm.
            // Part 2's tail: the melded class weapon is delivered to Gerolt at sequence 5, the journal
            // step immediately before the Chimera. Obtaining the weapon (seq 3) and melding the two
            // Grade III materia (seq 4) cannot be automated -- they are surfaced as the annotated
            // class-weapon step instead (Data/ClassWeaponStep, Windows/ClassWeaponPanel) -- but the
            // hand-over is the same teleport + interact every other Gerolt turn-in uses, so it is
            // driven here rather than leaving the run parked at 5 with nothing eligible.
            AddGeroltTurnIn(result, job, 5, "deliver the melded class weapon");
            AddGeroltTurnIn(result, job, 7, "deliver the Alumina Salts, report the Chimera");
            AddGeroltTurnIn(result, job, 9, "report to Gerolt after the Amdapor Glyph trade (Rowena)");
            AddGeroltTurnIn(result, job, 11, "report the beastman hunt");
            AddGeroltTurnIn(result, job, 13, "report the Hydra");
            AddGeroltTurnIn(result, job, 14, "hand over the unfinished relic");
            AddGeroltTurnIn(result, job, 18, "deliver the primal drops (ember, gale, ore)");

            // The FINAL step (seq 255): buy the quenching oil from Auriana, then turn it in to Gerolt.
            AddOilTurnIn(result, job);
        }

        return result;
    }

    // Parts 0-2 of "A Relic Reborn (<weapon>)", as sequence-driven objectives that follow the game's
    // live quest sequence (the qstxiv model the controller runs by ActiveAtSequence == live sequence):
    //   seq 0  accept the quest from Gerolt (Hyrstmill, North Shroud)
    //   seq 1  recover the broken weapon from the job's beastman stronghold (BaseRelicData.BrokenWeapon)
    //   seq 2  report the broken weapon back to Gerolt
    // The game then advances to Part 2 (class weapon) and the generated trial objectives take over.
    // This generalises the hand-authored Bard path (1125) to EVERY job from BaseRelicData, so an
    // un-started relic is routed to the broken-weapon location as its first step rather than the engine
    // selecting the Chimera (part 3) at sequence 0.
    //
    // SEAMS (offline-derived, verify in-game -- the same posture the Bard 1125 path documents):
    //   * The broken weapon is recovered by OPENING the stronghold coffer, addressed by the generic
    //     name "Treasure Coffer" with no DataId. InteractObjectExecutor's finder is name-driven and
    //     ObjectKind-tolerant, so it locates the coffer near the stronghold anchor and walks onto it.
    //     If a job's part-1 object is named differently, author that job's quest-path JSON with the
    //     exact id/name (it then supersedes this generated block via questPathJobs).
    //   * Sequence 1 = broken weapon and 2 = report are the qstxiv sequence numbers proven by the Bard
    //     path. They are driven by ActiveAtSequence == the live quest sequence, so the checklist's
    //     "Parts 1/2 GlobalParts sequence uncalibrated" note does not affect them.
    private static void AddBrokenWeaponStart(List<RelicObjective> result, RelicJob job, JobRelicData data)
    {
        var geroltAetheryte = Locations.AetheryteForTerritory(BaseRelicData.GeroltTerritory);

        // seq 0: accept the relic quest from Gerolt. TextAdvance carries the accept dialogue.
        result.Add(BuildStartObjective(job, 0, "accept",
            $"{RelicJobs.DisplayName(job)}: accept A Relic Reborn ({data.RelicWeaponName}) (Gerolt, Hyrstmill)",
            GeroltSteps(geroltAetheryte)));

        // seq 1: recover the broken weapon from the beastman stronghold. Teleport to the zone, then
        // find + open the coffer (the executor streams it in near the anchor and walks fully onto it).
        var bw = data.BrokenWeapon;
        var brokenSteps = new List<StepData>();
        var bwAetheryte = Locations.AetheryteForTerritory(bw.TerritoryTypeId);
        if (bwAetheryte != 0)
            brokenSteps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = bwAetheryte });
        brokenSteps.Add(new StepData
        {
            Type = StepType.InteractObject,
            TargetName = "Treasure Coffer",
            Position = Data.MapCoords.MapToWorld(bw.TerritoryTypeId, bw.MapX, bw.MapY, bw.MapZ),
        });
        result.Add(BuildStartObjective(job, 1, "broken-weapon",
            $"{RelicJobs.DisplayName(job)}: recover the broken {data.RelicWeaponName} ({bw.Label})",
            brokenSteps));

        // seq 2: report the broken weapon back to Gerolt (advances the quest to Part 2).
        result.Add(BuildStartObjective(job, 2, "report-broken-weapon",
            $"{RelicJobs.DisplayName(job)}: report the broken {data.RelicWeaponName} (Gerolt, Hyrstmill)",
            GeroltSteps(geroltAetheryte)));
    }

    // Teleport to Gerolt's zone (when an aetheryte resolves) then interact with him; TextAdvance
    // carries the accept / turn-in dialogue. Mirrors the between-trial Gerolt turn-ins below.
    private static List<StepData> GeroltSteps(uint geroltAetheryte)
    {
        var steps = new List<StepData>();
        if (geroltAetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = geroltAetheryte });
        steps.Add(new StepData
        {
            Type = StepType.InteractNpc,
            NpcDataId = BaseRelicData.GeroltDataId,
            Position = BaseRelicData.GeroltPosition,
        });
        return steps;
    }

    // A sequence-driven start-of-line objective (ActiveAtSequence == CompleteAtSequence == seq), matching
    // how QuestPathLoader shapes its per-sequence objectives so the controller treats both identically.
    private static RelicObjective BuildStartObjective(RelicJob job, int seq, string idSuffix, string displayName,
        List<StepData> steps)
        => new()
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-seq{seq:00}-{idSuffix}",
            DisplayName = displayName,
            Steps = steps,
            ActiveAtSequence = seq,
            CompleteAtSequence = seq,
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        };

    // The FINAL base-relic step (quest sequence 255): buy a Radz-at-Han Quenching Oil from Auriana
    // (Revenant's Toll, 15 Poetics) and turn it in to Gerolt (Hyrstmill) for the finished relic. Gated
    // to the quest's terminal sequence (255, where the tracker parks the relic on the "oil" line); it
    // completes when the QUEST itself completes (IsPartCompleteByQuest's quest-done branch), which is
    // why CompleteAtSequence stays 0 -- 255 is not a "passed" threshold, it is the step we run AT.
    // Steps: teleport to Mor Dhona -> buy the oil (BuyRadzOil) -> teleport to Gerolt's zone -> turn in
    // (InteractNpc + TextAdvance). SEAM: Auriana's exchange wording and the final Gerolt turn-in window
    // are offline-unverifiable; best-effort with logging + safe-fail (verify in-game).
    private static void AddOilTurnIn(List<RelicObjective> result, RelicJob job)
    {
        var steps = new List<StepData>();
        if (AnimusBookData.MorDhonaAetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = AnimusBookData.MorDhonaAetheryte });
        steps.Add(new StepData { Type = StepType.BuyRadzOil });
        var geroltAeth = Locations.AetheryteForTerritory(BaseRelicData.GeroltTerritory);
        if (geroltAeth != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = geroltAeth });
        steps.Add(new StepData
        {
            Type = StepType.InteractNpc,
            NpcDataId = BaseRelicData.GeroltDataId,
            Position = BaseRelicData.GeroltPosition,
        });

        result.Add(new RelicObjective
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-oil-turnin",
            DisplayName = $"{RelicJobs.DisplayName(job)}: buy the quenching oil (Auriana), finish at Gerolt",
            Steps = steps,
            ActiveFromSequence = 255,
            CompleteAtSequence = 0, // completes only when the quest itself completes (the final turn-in)
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        });
    }

    // A between-trial turn-in to Gerolt (Hyrstmill, North Shroud): teleport to his zone, then Interact;
    // TextAdvance carries the dialogue and the item hand-over. Gated to its EXACT quest sequence (active
    // = seq, complete = seq + 1), so it runs only while the quest is parked at that turn-in and completes
    // the moment the quest advances past it (IsPartCompleteByQuest). If TextAdvance is not carrying the
    // hand-in, the interaction still runs and the run stops with the pending-turn-in guidance, no worse
    // than before. The seq-14 hand-over of the equipped unfinished relic relies on the game's own
    // handover UI (which offers the equipped item); verify in-game.
    private static void AddGeroltTurnIn(List<RelicObjective> result, RelicJob job, int activeSeq, string label)
    {
        var steps = new List<StepData>();
        var aetheryte = Locations.AetheryteForTerritory(BaseRelicData.GeroltTerritory);
        if (aetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aetheryte });
        steps.Add(new StepData
        {
            Type = StepType.InteractNpc,
            NpcDataId = BaseRelicData.GeroltDataId,
            Position = BaseRelicData.GeroltPosition,
        });

        result.Add(new RelicObjective
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-turnin-seq{activeSeq}",
            DisplayName = $"{RelicJobs.DisplayName(job)}: {label} (Gerolt, Hyrstmill)",
            Steps = steps,
            ActiveFromSequence = activeSeq,
            CompleteAtSequence = activeSeq + 1,
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        });
    }

    // The Part 5 beastmen hunt: teleport to the stronghold aetheryte, then three KillTarget
    // steps (8 of each beastman) at the world-converted spawn coords. The quest credits the
    // kills and advances on its own; no Gerolt report is needed.
    private static void AddBeastmenObjective(List<RelicObjective> result, RelicJob job, JobRelicData data)
    {
        var territory = data.BeastmenHunt.TerritoryTypeId;
        var aetheryte = Locations.AetheryteForTerritory(territory);

        var steps = new List<StepData>();
        // Equip the relic first so the beastmen kills credit toward the quest.
        steps.Add(new StepData { Type = StepType.EnsureRelicEquipped });
        if (aetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aetheryte });

        // Cumulative quest-counter target: mob 1 done at total 8, mob 2 at 16, mob 3 at 24.
        // The step completes on the SUM of the three quest counters reaching this, which is
        // mapping-free (no need to know which nibble is which mob), race-free (the sum is
        // conserved regardless of which type a kill credits or when) and restart-proof (the
        // counters are read from the game, not an in-memory tally Start() would zero).
        var cumulative = 0;
        foreach (var mob in data.Beastmen)
        {
            cumulative += mob.Count;
            steps.Add(new StepData
            {
                Type = StepType.KillTarget,
                TargetName = mob.Name,
                Count = mob.Count,
                Position = MapCoords.MapToWorld(territory, mob.MapX, mob.MapY, mob.MapZ),
                UseQuestKillCounter = true,
                QuestCounterTarget = cumulative,
            });
        }

        result.Add(new RelicObjective
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-p05-beastmen",
            DisplayName = $"{RelicJobs.DisplayName(job)}: beastmen hunt ({data.BeastmenHunt.Label})",
            TargetName = data.Beastmen[0].Name,
            // The hunt's zone, so the main window's objective click can flag + travel to the
            // authored spawn. RelicObjective.Territory is otherwise only set by the book generator.
            Territory = territory,
            Steps = steps,
            ActiveFromSequence = BaseRelicData.ActiveFromSequenceFor(5),
            CompleteAtSequence = BaseRelicData.CompletedAtSequenceFor(5),
            CompletionQuestVariablesFlags = BaseRelicData.CompletionFlagsFor(5).ToList(),
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        });
    }

    // A duty objective (Chimera, Amdapor Keep, Hydra, primals): AutoDuty queues and clears the
    // duty, the quest credits the clear/drop and advances on its own. The duty's TerritoryType
    // (for AutoDuty) is resolved from its ContentFinderCondition name; unresolved -> skipped.
    private static void AddDutyObjective(List<RelicObjective> result, RelicJob job,
        (int Part, string IdSuffix, string Label, string Cfc, bool OneTime) duty)
    {
        var dutyTerritory = BaseRelicCatalog.DutyTerritoryId(duty.Cfc);
        if (dutyTerritory == 0)
            return;

        var steps = new List<StepData>();
        // ONLY the Hydra (part 6) is cleared with the unfinished relic equipped (its quest step reads
        // "with the unfinished <weapon> equipped ... complete the trial A Relic Reborn: The Hydra").
        // The Chimera (3) and Amdapor Keep (4) happen BEFORE the relic is forged; the Hard primals --
        // Ifrit (7), Garuda (8), Titan (9) -- happen AFTER you HAND the unfinished relic to Gerolt
        // (seq 14, "Give the unfinished <weapon> to Gerolt"), so you no longer hold it and their quest
        // steps read "as a <job>", not "with the relic equipped". Requiring the relic on the primals
        // therefore fails forever ("Relic weapon is not equipped and none was found ... to equip").
        // Verified across all 10 jobs' quest text. (The beastman hunt, part 5, adds its own equip step
        // in AddBeastmenObjective.)
        if (duty.Part == 6)
            steps.Add(new StepData { Type = StepType.EnsureRelicEquipped });
        // Unsynced so AutoDuty can solo the old relic trials/dungeons (synced they will not pop for a
        // single player, and AutoDuty never starts).
        steps.Add(new StepData { Type = StepType.EnterDuty, TerritoryType = dutyTerritory, Loops = 1, Unsynced = true });

        result.Add(new RelicObjective
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-p{duty.Part:00}-{duty.IdSuffix}",
            DisplayName = $"{RelicJobs.DisplayName(job)}: {duty.Label}",
            Steps = steps,
            ActiveFromSequence = BaseRelicData.ActiveFromSequenceFor(duty.Part),
            CompleteAtSequence = BaseRelicData.CompletedAtSequenceFor(duty.Part),
            CompletionQuestVariablesFlags = BaseRelicData.CompletionFlagsFor(duty.Part).ToList(),
            // No one-time-duty guard: the relic trials (the Chimera, the Hydra) ARE re-enterable per
            // relic (the prereq report shows them "unlocked -- queue from Duty Finder"), so an
            // "ever cleared" check (IsInstanceContentCompleted) wrongly skips them on a SECOND relic.
            // Per-relic completion is the live quest sequence (CompleteAtSequence) plus the in-session
            // ran flag, the same as the repeatable duties.
            OneTimeDutyContentId = 0u,
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        });
    }

    // The base-relic duties, run via AutoDuty. Parts 3-4 run before the beastmen hunt (p05);
    // parts 6-9 after. The OneTime column just labels the relic trials (Chimera, Hydra); they are
    // re-enterable per relic, so completion is the live quest sequence, not "ever cleared". The
    // quest advances on content completion (no per-duty Gerolt report).
    // SEAMS (verify in-game): each duty must be UNLOCKED for AutoDuty to queue it, and the single
    // collection turn-in to Gerolt plus the oil purchase from Auriana (part 10) are not yet automated.
    private static readonly (int Part, string IdSuffix, string Label, string Cfc, bool OneTime)[] Duties =
    {
        (3, "chimera", "Chimera (A Relic Reborn: The Chimera)", "A Relic Reborn: The Chimera", true),
        (4, "amdaporkeep", "Amdapor Keep", "Amdapor Keep", false),
        (6, "hydra", "Hydra (A Relic Reborn: The Hydra)", "A Relic Reborn: The Hydra", true),
        (7, "ifrit", "Ifrit (The Bowl of Embers (Hard))", "The Bowl of Embers (Hard)", false),
        (8, "garuda", "Garuda (The Howling Eye (Hard))", "The Howling Eye (Hard)", false),
        (9, "titan", "Titan (The Navel (Hard))", "The Navel (Hard)", false),
    };
}
