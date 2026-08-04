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

            // The between-content NPC steps. The quest parks at each of these until you talk to the
            // right person, and no later trial can start until it does, so every one is driven here as
            // a teleport + interact gated to its exact sequence. The full journal-to-sequence map lives
            // in BaseRelicData.GlobalParts; the entries below are the ones that are pure conversation:
            //
            //    3  deliver the melded class weapon to Gerolt  -> the Chimera (4)
            //    5  deliver the Alumina Salts to Gerolt        -> Rowena (6)
            //    6  SPEAK WITH ROWENA at Revenant's Toll       -> Amdapor Keep (7)
            //    8  deliver the Amdapor Glyph to ROWENA        -> the tome copy (9)
            //    9  deliver the tome copy to Gerolt            -> the beastman hunt (10)
            //   11  report the beastman hunt to Gerolt         -> the Hydra (12)
            //   13  report the Hydra to Gerolt                 -> the hand-over (14)
            //   14  hand the unfinished relic to Gerolt        -> the primals (15-17)
            //   18  deliver the three primal drops to Gerolt   -> the oil (19)
            //
            // The two ROWENA steps (6 and 8) were missing entirely before 1.5.2.1, which left the run
            // parked with nothing eligible the moment the Alumina Salts were handed over -- and, because
            // the sequence table had been shifted to close the gap, trying to queue the Chimera while
            // the journal actually read "Speak with Rowena". They are the same teleport + interact as
            // the Gerolt turn-ins, just at a different NPC.
            //
            // Sequence 3 (the class weapon) is driven here too. Obtaining the weapon and melding the
            // two Grade III materia cannot be automated -- they are surfaced as the annotated
            // class-weapon step instead (Data/ClassWeaponStep, Windows/ClassWeaponPanel) -- but the
            // hand-over itself is an ordinary turn-in, so the run does not sit at 3 with nothing
            // eligible once the player has the melded weapon in the bag.
            AddGeroltTurnIn(result, job, 3, "deliver the melded class weapon");
            AddGeroltTurnIn(result, job, 5, "deliver the Alumina Salts, report the Chimera");
            AddRowenaTurnIn(result, job, 6, "speak with Rowena about the relic's hero");
            AddRowenaTurnIn(result, job, 8, "deliver the Amdapor Glyph to Rowena");
            // Sequence 9 is where Gerolt forges the UNFINISHED relic and hands it over, and sequence
            // 10 -- the very next step -- is "arm yourself with the unfinished <weapon> and slay...".
            // So the turn-in equips it on the spot (equipRelicAfter) instead of leaving it in the bag
            // for the hunt objective to notice: the hunt is a long trip to a stronghold, and arriving
            // there to discover the weapon was never equipped costs the whole trip. The hunt keeps its
            // own equip step as a backstop for a player who starts mid-hunt.
            AddGeroltTurnIn(result, job, 9, "deliver the hero's tome copy, equip the unfinished relic",
                equipRelicAfter: true);
            // Sequence 11 reports the beastman hunt -- and Gerolt WANTS THE WEAPON BACK to look at
            // it, so this is a hand-over, not the pure conversation the sequence map reads like. A
            // hand-over UI never lists an EQUIPPED item, and the hunt at sequence 10 necessarily
            // ends with the unfinished relic in your hands (its kills only credit while it is on),
            // so without taking it off first the turn-in offers nothing and the run parks at 11.
            // Reported live, straight off the beastman hunt.
            //
            // Costs nothing if Gerolt turns out not to take it: UnequipRelicFirst puts back
            // anything the conversation did not consume (InteractNpcExecutor.RestoreRelicWeapons),
            // and the Hydra at sequence 12 -- the only trial fought with the relic equipped -- adds
            // its own EnsureRelicEquipped step regardless.
            AddGeroltTurnIn(result, job, 11, "report the beastman hunt", unequipRelicFirst: true);
            AddGeroltTurnIn(result, job, 13, "report the Hydra");
            // Sequence 14 hands the unfinished relic BACK to Gerolt, and the hand-over UI does not
            // list equipped items -- so it has to come off first (StepData.UnequipRelicFirst, which
            // also restores it if the turn-in does not happen and keeps the stage read from reading
            // "no relic" meanwhile).
            AddGeroltTurnIn(result, job, 14, "hand over the unfinished relic", unequipRelicFirst: true);
            AddGeroltTurnIn(result, job, 18, "deliver the primal drops (ember, gale, ore)");

            // The FINAL step (seq 19): buy the quenching oil from Auriana, then turn it in to Gerolt.
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
    //   * The broken weapon is recovered by OPENING the stronghold coffer. Since 1.5.8.5 the object
    //     is addressed by its OWN game-sheet row -- DataId, name and exact world position, from
    //     BaseRelicData.BrokenWeaponCoffer -- rather than by the generic name "Treasure Coffer" at a
    //     transcribed map coordinate. The derivation (EObj.Data -> the relic quest, EObjName, and the
    //     Level row keyed on the object) reproduces the hand-authored Bard path's coffer exactly, so
    //     the Bard row is the check on the other nine.
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
        //
        // The anchor is the coffer's own Level-sheet position (BaseRelicData.BrokenWeaponCoffer),
        // NOT the transcribed map coordinate, and it carries its real height so nothing has to be
        // floor-probed. That matters because InteractObjectExecutor can only see objects within
        // SearchRadius (100y): five of the ten transcribed anchors were further than that from the
        // real coffer -- Summoner by 181y -- so the run arrived at a spot where the coffer never
        // entered the finder and the step timed out having opened nothing. The MapStop is kept for
        // the prerequisite report's display hint only.
        var bw = data.BrokenWeapon;
        var coffer = data.BrokenWeaponCoffer;
        var bwTerritory = coffer.IsAuthored ? coffer.TerritoryTypeId : bw.TerritoryTypeId;
        var bwAnchor = coffer.IsAuthored
            ? coffer.World
            : Data.MapCoords.MapToWorld(bw.TerritoryTypeId, bw.MapX, bw.MapY, bw.MapZ);

        var brokenSteps = new List<StepData>();
        // NEAREST aetheryte to the coffer, not just "an" aetheryte in the zone. Sapsa Spawning
        // Grounds (Ninja, Scholar) sits in Western La Noscea, which has two -- exactly the case
        // AetheryteForTerritory documents itself as getting wrong -- and with the coffer's exact
        // position now known there is no reason to guess. Falls back to the by-territory pick when
        // the marker lookup cannot resolve.
        var bwAetheryte = Locations.NearestAetheryteToWorld(
            bwTerritory, Locations.MapForTerritory(bwTerritory), bwAnchor)?.AetheryteId ?? 0u;
        if (bwAetheryte == 0)
            bwAetheryte = Locations.AetheryteForTerritory(bwTerritory);
        if (bwAetheryte != 0)
            brokenSteps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = bwAetheryte });
        brokenSteps.Add(new StepData
        {
            Type = StepType.InteractObject,
            // The object name is per-job data now: Ninja's Yoshimitsu object is a "Banded Chest",
            // so the old hard-coded "Treasure Coffer" could never match it by name -- and with no
            // DataId authored either, that step had nothing to find at all.
            TargetName = coffer.IsAuthored ? coffer.ObjectName : "Treasure Coffer",
            // The DataId outranks the name in WorldObject.FindNearest, which is what separates this
            // job's coffer from the other job's identically-named one in the same stronghold.
            TargetDataId = coffer.DataId,
            Position = bwAnchor,
        });
        result.Add(BuildStartObjective(job, 1, "broken-weapon",
            $"{RelicJobs.DisplayName(job)}: recover the broken {data.RelicWeaponName} ({bw.Label})",
            brokenSteps));

        // seq 2: report the broken weapon back to Gerolt (advances the quest to Part 2). This one is
        // a hand-over, so it verifies the quest actually moved -- the accept at seq 0 does not need
        // to, because the controller runs its own accept watchdog for that sequence.
        result.Add(BuildStartObjective(job, 2, "report-broken-weapon",
            $"{RelicJobs.DisplayName(job)}: report the broken {data.RelicWeaponName} (Gerolt, Hyrstmill)",
            GeroltSteps(geroltAetheryte, advancesQuestFromSequence: 2)));
    }

    // Teleport to Gerolt's zone (when an aetheryte resolves) then interact with him; TextAdvance
    // carries the accept / turn-in dialogue. Mirrors the between-trial Gerolt turn-ins below.
    private static List<StepData> GeroltSteps(uint geroltAetheryte, int advancesQuestFromSequence = 0)
    {
        var steps = new List<StepData>();
        if (geroltAetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = geroltAetheryte });
        steps.Add(new StepData
        {
            Type = StepType.InteractNpc,
            NpcDataId = BaseRelicData.GeroltDataId,
            Position = BaseRelicData.GeroltPosition,
            AdvancesQuestFromSequence = advancesQuestFromSequence,
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

    // The FINAL base-relic step (quest sequence 19): buy a Radz-at-Han Quenching Oil from Auriana
    // (Revenant's Toll, 15 Poetics) and turn it in to Gerolt (Hyrstmill) for the finished relic. It
    // completes when the QUEST itself completes (IsPartCompleteByQuest's quest-done branch), which is
    // why CompleteAtSequence stays 0 -- 19 is not a "passed" threshold, it is the step we run AT.
    //
    // The gate is a LOWER bound (>= 19), not the exact 255 it used to be, because the last journal
    // entry is 19 and only some quests report the terminal step as 0xFF. Either value satisfies >= 19,
    // so this runs whichever convention the quest uses; the old exact-255 gate would simply never fire
    // if the quest parks at 19. Nothing else is eligible that late -- every other objective completes
    // at 18 or earlier -- so the loose bound costs nothing.
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
            // The oil is DELIVERED here, so this is a hand-over like every other: it needs the
            // Request window driven, and it needs verifying. Sequence 19 is the last journal entry,
            // so success shows up as the quest COMPLETING -- the verification treats a sequence of 0
            // (a finished, or unreadable, quest) as credited, which is what covers that.
            AdvancesQuestFromSequence = 19,
        });
        // EQUIP the finished relic. Gerolt hands it over UNEQUIPPED, and the engine reads which stage
        // a character is on from the weapon in their hands -- so until this runs the line looks
        // finished-but-invisible: no relic equipped reads as no relic progress, and selection falls
        // through to whatever sorts first, which is another job's base relic. Reported live on Bard:
        // "just got the Artemis Bow, but it went back to buy another quenching oil and the objective
        // says Monk". GameState.HighestHeldRelicStage now covers the gap defensively; equipping it
        // here is the actual fix, and it is what the player wants anyway (Zenith is next, and that
        // trade wants the weapon in hand to be found).
        steps.Add(new StepData { Type = StepType.EnsureRelicEquipped });

        result.Add(new RelicObjective
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-oil-turnin",
            DisplayName = $"{RelicJobs.DisplayName(job)}: buy the quenching oil (Auriana), finish at Gerolt",
            Steps = steps,
            ActiveFromSequence = BaseRelicData.ActiveFromSequenceFor(10),
            CompleteAtSequence = 0, // completes only when the quest itself completes (the final turn-in)
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        });
    }

    // A between-content turn-in to Gerolt (Hyrstmill, North Shroud): teleport to his zone, then Interact;
    // TextAdvance carries the dialogue and the item hand-over. Gated to its EXACT quest sequence (active
    // = seq, complete = seq + 1), so it runs only while the quest is parked at that turn-in and completes
    // the moment the quest advances past it (IsPartCompleteByQuest). If TextAdvance is not carrying the
    // hand-in, the interaction still runs and the run stops with the pending-turn-in guidance, no worse
    // than before. The seq-14 hand-over of the equipped unfinished relic relies on the game's own
    // handover UI (which offers the equipped item); verify in-game.
    private static void AddGeroltTurnIn(List<RelicObjective> result, RelicJob job, int activeSeq, string label,
        bool unequipRelicFirst = false, bool equipRelicAfter = false)
        => AddNpcTurnIn(result, job, activeSeq, label, "Gerolt, Hyrstmill",
            BaseRelicData.GeroltDataId, BaseRelicData.GeroltTerritory, BaseRelicData.GeroltPosition,
            unequipRelicFirst, equipRelicAfter);

    // The two ROWENA steps (sequences 6 and 8): Gerolt sends you to her at Revenant's Toll for the
    // hero's tome, and it is Rowena -- not Gerolt -- who takes the Amdapor Glyph. Identical shape to
    // the Gerolt turn-ins, just a different zone and NPC; both are plain conversations that TextAdvance
    // carries, and the glyph hand-over is a quest delivery in dialogue, not a shop exchange.
    private static void AddRowenaTurnIn(List<RelicObjective> result, RelicJob job, int activeSeq, string label)
        => AddNpcTurnIn(result, job, activeSeq, label, "Rowena, Revenant's Toll",
            BaseRelicData.RowenaDataId, BaseRelicData.RowenaTerritory,
            MapCoords.MapToWorld(BaseRelicData.RowenaTerritory, BaseRelicData.RowenaMapX, BaseRelicData.RowenaMapY));

    private static void AddNpcTurnIn(List<RelicObjective> result, RelicJob job, int activeSeq, string label,
        string where, uint npcDataId, uint territory, System.Numerics.Vector3 position,
        bool unequipRelicFirst = false, bool equipRelicAfter = false)
    {
        var steps = new List<StepData>();
        var aetheryte = Locations.AetheryteForTerritory(territory);
        if (aetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aetheryte });
        steps.Add(new StepData
        {
            Type = StepType.InteractNpc,
            NpcDataId = npcDataId,
            Position = position,
            UnequipRelicFirst = unequipRelicFirst,
            // Every one of these steps exists to move the quest off activeSeq. Saying so lets the
            // executor check that it did, instead of accepting "the conversation ended" as proof --
            // see StepData.AdvancesQuestFromSequence for what accepting it cost.
            AdvancesQuestFromSequence = activeSeq,
        });
        // Equip what the turn-in just handed over (the unfinished relic at sequence 9), so the next
        // objective does not travel to the hunt with it sitting in a bag.
        if (equipRelicAfter)
            steps.Add(new StepData { Type = StepType.EnsureRelicEquipped });

        result.Add(new RelicObjective
        {
            Stage = RelicStage.Relic,
            Job = job,
            Id = $"relic-{job}-turnin-seq{activeSeq}",
            DisplayName = $"{RelicJobs.DisplayName(job)}: {label} ({where})",
            Steps = steps,
            ActiveFromSequence = activeSeq,
            CompleteAtSequence = activeSeq + 1,
            Completion = new CompletionCondition { Kind = CompletionKind.AllStepsDone },
        });
    }

    // The Part 5 beastmen hunt: teleport to the stronghold aetheryte, then ONE KillTarget step that
    // accepts all three beastman types. The quest credits the kills and advances on its own; no
    // Gerolt report is needed.
    //
    // One step, not three. The journal asks for eight of each of three types, and the three spawn
    // groups are INTERMINGLED across a single stronghold -- so running them as three sequential
    // single-name steps meant clearing one type while walking past the other two, then walking the
    // same ground again for the second, and again for the third. Reported as the hunt taking
    // significantly longer than it should. With one step the executor takes whichever wanted type is
    // NEAREST, so the stronghold is cleared in roughly one pass; it retires a type once that type is
    // capped (KillTargetExecutor tracks this per name) so the last few kills still go to the types
    // that actually still credit.
    //
    // Completion is unchanged and still the SUM of the three quest counters (24), which is
    // mapping-free (no need to know which nibble is which mob), race-free (the sum is conserved
    // regardless of which type a kill credits or when) and restart-proof (the counters are read from
    // the game, not an in-memory tally Start() would zero).
    private static void AddBeastmenObjective(List<RelicObjective> result, RelicJob job, JobRelicData data)
    {
        var territory = data.BeastmenHunt.TerritoryTypeId;
        var aetheryte = Locations.AetheryteForTerritory(territory);

        var steps = new List<StepData>();
        // Equip the relic BEFORE travelling: the kills only credit while the unfinished weapon is
        // in hand, and finding that out after a cross-zone trip costs the whole trip.
        steps.Add(new StepData { Type = StepType.EnsureRelicEquipped });
        if (aetheryte != 0)
            steps.Add(new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aetheryte });
        // ...and again immediately before the first swing. The check above is separated from the
        // fighting by a teleport and a cross-zone ride, and anything that takes the weapon off in
        // between -- a turn-in path that restores gear, a manual swap, a resumed run that re-enters
        // mid-objective -- is invisible to it. Twenty-four kills that credit nothing look exactly
        // like twenty-four kills that do, so there is no later signal to catch it. This costs
        // nothing when the weapon is already right: EnsureRelicEquipped completes on the first tick
        // without moving an item.
        steps.Add(new StepData { Type = StepType.EnsureRelicEquipped });

        var total = 0;
        foreach (var mob in data.Beastmen)
            total += mob.Count;

        steps.Add(new StepData
        {
            Type = StepType.KillTarget,
            // TargetName stays the first type, for the log lines and the objective's map flag; the
            // SET below is what the acquire actually matches on.
            TargetName = data.Beastmen[0].Name,
            TargetNames = data.Beastmen.Select(m => m.Name).ToList(),
            Count = total,
            // The stronghold anchor rather than a per-mob coordinate: with all three types wanted at
            // once there is one place to travel to, and the executor's outward search covers the
            // spread from there (the per-type coordinates only ever differed by a few yalms inside
            // the same camp anyway).
            Position = MapCoords.MapToWorld(territory, data.BeastmenHunt.MapX, data.BeastmenHunt.MapY,
                data.BeastmenHunt.MapZ),
            UseQuestKillCounter = true,
            QuestCounterTarget = total,
        });

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
