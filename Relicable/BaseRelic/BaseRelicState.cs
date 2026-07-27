using System.Collections.Generic;
using Relicable.Data;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.BaseRelic;

// Live "where is the player in the relic line" checks that gate the controller and UI.
//
// The base-relic quest is PER JOB: its journal title is "A Relic Reborn (<weapon>)"
// (e.g. "A Relic Reborn (Curtana)", "A Relic Reborn (Gae Bolg)"), not a generic
// "A Relic Reborn". Detection therefore resolves the quest for a specific job's weapon,
// and the controller uses the currently equipped job.
public static class BaseRelicState
{
    // The relic job for the currently equipped job (None if not on a relic job).
    public static RelicJob ActiveRelicJob()
        => RelicJobs.FromClassJobId(GameState.ActiveClassJobId());

    // The per-job relic quest title, e.g. "A Relic Reborn (Curtana)".
    public static string RelicQuestNameFor(RelicJob job)
        => BaseRelicData.RelicQuestNameFor(job);

    // The resolved relic-quest row id for a job (0 if the title did not resolve).
    public static uint RelicQuestIdFor(RelicJob job)
    {
        var name = RelicQuestNameFor(job);
        return string.IsNullOrEmpty(name) ? 0u : BaseRelicCatalog.QuestId(name);
    }

    // The live quest sequence for a job's relic quest (0 when not currently active).
    public static int RelicQuestSequenceFor(RelicJob job)
    {
        var qid = RelicQuestIdFor(job);
        if (qid != 0)
            return GameState.QuestSequence(qid);
        // Fallback (sheet names that omit the weapon suffix): the highest sequence among
        // all "A Relic Reborn" quest rows currently active.
        var best = 0;
        foreach (var id in BaseRelicCatalog.RelicQuestRowIdList())
        {
            var s = GameState.QuestSequence(id);
            if (s > best)
                best = s;
        }
        return best;
    }

    // True when ANY base-relic quest row is currently active. The robust primary signal
    // for "the player is doing a base relic", independent of per-job title resolution.
    public static bool IsAnyRelicQuestActive()
    {
        foreach (var id in BaseRelicCatalog.RelicQuestRowIdList())
            if (GameState.QuestSequence(id) > 0)
                return true;
        return false;
    }

    // True while the player is actively on this job's base-relic quest (sequence > 0).
    // Stays true throughout Parts 5-10 -- while the unfinished relic is equipped for the
    // beastmen hunt and trials -- even though that equipped weapon can make
    // EquippedRelicStage read "Relic". This is what distinguishes "mid base-relic quest"
    // from "base relic finished".
    public static bool IsBaseRelicInProgress(RelicJob job) => RelicQuestSequenceFor(job) > 0;

    // True once this job's base relic has been finished (its quest completed before).
    // Fail-open: if the title cannot be resolved (id 0), assume obtained so an existing
    // relic user is never wrongly blocked.
    public static bool IsBaseRelicObtained(RelicJob job)
    {
        var qid = RelicQuestIdFor(job);
        if (qid == 0)
            return true;
        return GameState.IsQuestComplete(qid);
    }

    // True when the Zodiac relic line has been unlocked -- Nedrick Ironheart's one-time
    // "The Weaponsmith of Legend" (BaseRelicData.UnlockQuestName) is complete -- so "A Relic Reborn"
    // can actually be ACCEPTED from Gerolt. When false, running the seq-0 accept step (InteractNpc
    // Gerolt) just talks to Gerolt with no quest to accept: the step completes on the dialogue closing,
    // the quest sequence never leaves 0, and the run would idle at Gerolt with no feedback -- so the
    // controller stops with guidance instead. Fail-open (true) when the quest name does not resolve, so
    // a lookup miss never wrongly blocks a legitimate start.
    public static bool RelicLineUnlocked()
    {
        var id = BaseRelicCatalog.QuestId(BaseRelicData.UnlockQuestName);
        return id == 0 || GameState.IsQuestComplete(id);
    }

    // The controller gate: work the base-relic (Relic) stage rather than Atma+ when the
    // equipped job's relic quest is in progress, OR has never been finished. An equipped
    // unfinished relic must not advance the engine to Atma while its quest is still open.
    // Returns false when not on a relic job (let normal selection run).
    public static bool ShouldWorkBaseRelic()
    {
        // Robust primary signal: any "A Relic Reborn" quest is currently active.
        if (IsAnyRelicQuestActive())
            return true;
        var job = ActiveRelicJob();
        if (job == RelicJob.None)
            return false;
        return !IsBaseRelicObtained(job);
    }

    // The relic stage to treat as "current" for display / reporting. The equipped weapon is
    // authoritative when a recognized relic weapon is on (EquippedRelicStage); otherwise the
    // stage resolves to the FIRST stage (Relic) from the active "A Relic Reborn" quest id
    // (ShouldWorkBaseRelic) OR from a finished base relic parked in the armoury chest / bags
    // awaiting its Zenith trade (NeedsZenith), so a player with nothing equipped to identify it
    // is still shown as being on the Relic stage rather than "none detected". None only when
    // no relic weapon is held anywhere and no base-relic quest is active/unfinished.
    public static RelicStage EffectiveStage()
    {
        var equipped = GameState.EquippedRelicStage();
        if (equipped != RelicStage.None)
            return equipped;
        return ShouldWorkBaseRelic() || NeedsZenith() ? RelicStage.Relic : RelicStage.None;
    }

    // True when at least one FINISHED base relic (A Relic Reborn complete) has not had the
    // Zenith upgrade applied yet -- the next step after the first stage. Zenith is a PURE
    // ITEM GATE with no quest (trade the base relic + 3x Thavnairian Mist at the Furnace beside
    // Gerolt, Hyrstmill / North Shroud), so it is detected from the weapons themselves: any
    // bare base relic held in the hands, the armoury chest, or a bag counts -- the weapon does
    // not need to be equipped, and it retains this step until it is traded at the Furnace.
    // The "Unfinished <weapon>" (still mid A Relic Reborn) and "<weapon> Zenith" (already
    // upgraded) forms are different item ids and never match (RelicWeaponStages.IsBareBaseRelic).
    public static bool NeedsZenith() => GameState.ZenithPendingWeapons().Count > 0;

    // The finished base relics awaiting the Zenith trade, as item id -> held count, scanned
    // across the hands, the armoury chest, and the bags (GameState.ZenithPendingWeapons).
    public static Dictionary<uint, int> ZenithPendingWeapons()
        => GameState.ZenithPendingWeapons();

    // Total weapons awaiting the Zenith trade, for the x2/x3 style reporting when several
    // sit at the same stage.
    public static int CountZenithPending(IReadOnlyDictionary<uint, int> pending)
    {
        var total = 0;
        foreach (var kv in pending)
            total += kv.Value;
        return total;
    }

    // Total Thavnairian Mist the pending set needs. Every pending weapon is its OWN Furnace
    // trade (SpecialShop 1769484: no entry yields two items): solo main hands cost 3 mists
    // each; the Paladin pair is two entries, Curtana 2 + Holy Shield 1 (3 for the full set).
    public static int ZenithMistNeeded(IReadOnlyDictionary<uint, int> pending)
    {
        var total = 0;
        foreach (var kv in pending)
            total += RelicWeaponStages.ZenithMistCost(kv.Key) * kv.Value;
        return total;
    }

    // The EQUIPPED-hands-only Zenith check: true when the weapon currently in the main or off
    // hand is a bare (finished, untraded) base relic. This is the gate automation such as the
    // CBT Atma delegation keys on -- an alt job's pending relic parked in the armoury must NOT
    // interrupt a run on the equipped weapon; the inventory-wide NeedsZenith is for guidance.
    public static bool EquippedNeedsZenith() => GameState.EquippedZenithPending();

    // "Curtana, Bravura x2" style list of the pending weapons for the UI and reports;
    // a count is only appended when several of the same weapon share the stage.
    public static string DescribeZenithPending(IReadOnlyDictionary<uint, int> pending)
    {
        var parts = new List<string>();
        foreach (var kv in pending)
        {
            var name = GameState.ItemName(kv.Key);
            if (string.IsNullOrEmpty(name))
                name = $"item {kv.Key}";
            parts.Add(kv.Value > 1 ? $"{name} x{kv.Value}" : name);
        }
        return string.Join(", ", parts);
    }

    // True when the Atma weapon (il100) is equipped but no Trials of the Braves book is active yet.
    // The Animus stage's first step is buying the first book from G'Jusana (Mor Dhona); the controller
    // auto-buys it on Start, and the main window surfaces it as the next step.
    public static bool NeedsFirstBook()
        => GameState.EquippedRelicStage() == RelicStage.Atma && GameState.ActiveRelicNoteId() == 0;

    // True when the "current" stage is the base relic resolved from the QUEST id rather than
    // from an equipped weapon (nothing equipped identifies it). Lets the UI say so explicitly.
    public static bool StageResolvedFromQuest()
        => GameState.EquippedRelicStage() == RelicStage.None && ShouldWorkBaseRelic();

    // Quest-aware completion for a base-relic (Relic-stage) objective: true when the
    // relic quest for its job is finished, or the live sequence has advanced past the
    // objective's part. This lets the controller skip a part the player did manually (or
    // that the quest has already passed), not only one the engine ran itself (the
    // AllStepsDone procedural flag). Returns false for non-base-relic objectives.
    public static bool IsPartCompleteByQuest(RelicObjective o)
    {
        if (o.Stage != RelicStage.Relic || o.Job == RelicJob.None)
            return false;
        var qid = RelicQuestIdFor(o.Job);
        if (qid == 0)
            return false;
        var seq = GameState.QuestSequence(qid);
        // Whole base relic finished for this job (quest complete and not currently active).
        if (seq == 0 && GameState.IsQuestComplete(qid))
            return true;
        // Questionable-style precise verification (when calibrated): the relic quest is active
        // and its live work bytes match this objective's completion flags. This is the exact
        // nibble-compare Questionable uses, and it resolves parts whose sequence threshold is an
        // uncalibrated seam (Parts 1, 2, 10). It is ORed with -- never replaces -- the sequence
        // gate below, so a miscalibrated flag cannot regress the working sequence behaviour.
        if (seq > 0 && QuestWorkUtils.HasCompletionFlags(o.CompletionQuestVariablesFlags))
        {
            var vars = GameState.QuestWorkVariables(qid);
            if (vars != null && QuestWorkUtils.MatchesQuestWork(vars, o.CompletionQuestVariablesFlags))
                return true;
        }
        // The live sequence has reached/passed this part's completion sequence. Uses >= to match the
        // prereq report ("reached threshold N" at seq == N); strictly-greater disagreed with the
        // report, leaving a part shown done in the report but still re-running in the controller.
        return o.CompleteAtSequence > 0 && seq >= o.CompleteAtSequence;
    }
}
