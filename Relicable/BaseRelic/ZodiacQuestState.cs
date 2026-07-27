using System.Collections.Generic;
using Relicable.Data;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.BaseRelic;

// Reads the player's live position across the WHOLE Zodiac line -- every stage's one-time
// quest, not just the base "A Relic Reborn" quest. Purely observational: it reports state and
// derives a best-effort "current stage"; it does not drive the controller (the controller
// still selects work from the equipped weapon + objective completion). This is what makes
// "/relic questwork" recognize e.g. "Up in Arms" (Atma) or "Rise and Shine" (Zeta), which the
// old base-relic-only report could not see.
public static class ZodiacQuestState
{
    // The stage the player is CURRENTLY working, derived transparently:
    //   1) An accepted line quest (sequence > 0) pins the stage exactly.
    //   2) Otherwise the equipped weapon proves every tier up to its own complete, so the
    //      working stage is the next tier -- except a base/Unfinished/Zenith weapon (Relic
    //      tier) still counts as Relic while the base-relic quest is running. This mirrors the
    //      controller's own weapon-tier + base-relic gate, so display and automation agree.
    //   3) Nothing identifies it -> Relic if a base-relic quest is in progress OR a finished
    //      base relic is parked in the armoury chest / bags awaiting its Zenith trade (the
    //      weapon retains the step without being equipped), else None.
    public static RelicStage CurrentStage()
    {
        var active = ActiveLineQuest();
        if (active.HasValue)
            return active.Value.Stage;

        var weapon = GameState.EquippedRelicStage();
        if (weapon != RelicStage.None)
        {
            if (weapon == RelicStage.Relic && BaseRelicState.ShouldWorkBaseRelic())
                return RelicStage.Relic;
            return NextStage(weapon);
        }

        return BaseRelicState.ShouldWorkBaseRelic() || BaseRelicState.NeedsZenith()
            ? RelicStage.Relic : RelicStage.None;
    }

    // Full-line quest scan for /relic questwork (and /relic quests): every stage's quest with
    // its live status, the equipped weapon's tier, a derived current position, and -- for
    // whichever line quest is currently accepted -- the six work bytes (for calibrating
    // per-part CompletionQuestVariablesFlags the same way base-relic parts were calibrated).
    public static IReadOnlyList<string> LineReport()
    {
        var lines = new List<string>
        {
            "Relicable -- Zodiac line quest scan (all stages):",
            $"  [unlock ] The Weaponsmith of Legend (id {ZodiacQuestRegistry.WeaponsmithOfLegendId}) -- {StatusLabel(ZodiacQuestRegistry.WeaponsmithOfLegendId)}",
        };

        // Relic (base): the weapon quest is per job, resolved for the equipped job.
        var job = BaseRelicState.ActiveRelicJob();
        if (job == RelicJob.None)
        {
            lines.Add("  [Relic  ] base: no relic job equipped -- switch to a relic job to read its 'A Relic Reborn (<weapon>)' quest.");
        }
        else
        {
            var bName = BaseRelicState.RelicQuestNameFor(job);
            var bId = BaseRelicState.RelicQuestIdFor(job);
            var bSeq = BaseRelicState.RelicQuestSequenceFor(job);
            var bStatus = bId != 0 && GameState.IsQuestComplete(bId) ? "complete"
                : bSeq > 0 ? $"ACTIVE (seq {bSeq})" : "not started";
            lines.Add($"  [Relic  ] {bName} (id {bId}) -- {bStatus}");
        }

        // Zenith: a pure item gate, no quest. Report live which held weapons (equipped, in the
        // armoury chest, or in a bag -- all count) still await the Furnace trade.
        var zenithPending = BaseRelicState.ZenithPendingWeapons();
        lines.Add(zenithPending.Count == 0
            ? "  [Zenith ] (no quest -- trade base weapon + 3x Thavnairian Mist at the Furnace by Gerolt)"
            : $"  [Zenith ] AWAITING TRADE: {BaseRelicState.DescribeZenithPending(zenithPending)} -- trade each with Thavnairian Mist ({BaseRelicState.ZenithMistNeeded(zenithPending)} total) at the Furnace by Gerolt");

        // Atma..Zeta from the registry.
        foreach (var q in ZodiacQuestRegistry.Quests)
        {
            if (q.Role == ZodiacQuestRole.LineUnlock)
                continue;
            var tag = q.Role switch
            {
                ZodiacQuestRole.StageSecondary => $"{q.Stage}+",
                ZodiacQuestRole.Subquest => $"{q.Stage} sub",
                ZodiacQuestRole.Finisher => $"{q.Stage} fin",
                _ => q.Stage.ToString(),
            };
            lines.Add($"  [{tag,-8}] {q.Name} (id {q.QuestId}) -- {StatusLabel(q.QuestId)}");
        }

        // Equipped weapon + derived position.
        var weapon = GameState.EquippedRelicStage();
        lines.Add(weapon == RelicStage.None
            ? "Equipped main hand: (not a recognized relic weapon)"
            : $"Equipped main hand: '{GameState.EquippedMainHandName()}' -> proves through the {weapon} stage");
        lines.Add($"-> Current position: {DescribeCurrentPosition()}");
        lines.Add($"   (working stage, weapon/quest-derived: {CurrentStage()})");

        // Calibration payload: the active line quest's work bytes, if one is accepted.
        var activeQuest = ActiveLineQuest();
        if (activeQuest.HasValue)
        {
            var vars = GameState.QuestWorkVariables(activeQuest.Value.QuestId);
            if (vars != null)
            {
                lines.Add($"Work bytes for active quest '{activeQuest.Value.Name}' (masked {activeQuest.Value.QuestId & 0xFFFF}):");
                for (var i = 0; i < vars.Length; i++)
                    lines.Add($"  Variables[{i}] = {vars[i]} (0x{vars[i]:X2})  high={vars[i] >> 4} low={vars[i] & 0x0F}");
            }
            else
            {
                lines.Add("Work bytes unavailable for the active quest (not logged in?).");
            }
        }
        else
        {
            lines.Add("No Zodiac line quest is currently accepted (work-byte calibration needs an accepted quest).");
        }

        return lines;
    }

    // A concise one-liner for the main window: where the player is on the line right now.
    // Same signals as DescribeCurrentPosition (an accepted quest > the Zenith/first-book
    // item-gate seams > the derived stage), trimmed for a single UI line.
    public static string CurrentPositionLine()
    {
        var active = ActiveLineQuest();
        if (active.HasValue)
        {
            var seq = GameState.QuestSequence(active.Value.QuestId);
            return $"{active.Value.Name} ({active.Value.Stage}){(seq == 255 ? " - grinding" : " - in progress")}";
        }

        var stage = CurrentStage();
        if (stage == RelicStage.None)
            return "Not on the relic line";
        if (BaseRelicState.NeedsZenith())
        {
            // Several weapons parked at the stage report their count ("x3"): each one keeps
            // the Zenith step (equipped or in the armoury) until traded at the Furnace.
            var count = BaseRelicState.CountZenithPending(BaseRelicState.ZenithPendingWeapons());
            return count > 1
                ? $"Base relic complete - next: trade for Zenith at the Furnace (x{count} weapons)"
                : "Base relic complete - next: trade for Zenith at the Furnace";
        }
        if (BaseRelicState.NeedsFirstBook())
            return "Atma complete - next: buy the first Animus book";
        return $"{stage} stage in progress";
    }

    // "complete" / "ACTIVE (seq N)" / "not started" for a full Quest-sheet row id.
    private static string StatusLabel(uint questId)
    {
        if (questId == 0)
            return "unresolved id";
        if (GameState.IsQuestComplete(questId))
            return "complete";
        var seq = GameState.QuestSequence(questId);
        return seq > 0 ? $"ACTIVE (seq {seq})" : "not started";
    }

    // A human line describing exactly where the player is, using the most precise signal
    // available (an accepted quest > the Zenith/first-book item-gate seams > the derived stage).
    private static string DescribeCurrentPosition()
    {
        var active = ActiveLineQuest();
        if (active.HasValue)
        {
            var seq = GameState.QuestSequence(active.Value.QuestId);
            // 0xFF (255) is the game's final/turn-in sequence: the umbrella quest parks there for
            // the whole stage grind (e.g. Up in Arms sits at 255 through the 12-Atma farm), so it
            // is a "still working this stage" signal, not an intro step.
            var where = seq == 255 ? "at its final/grind step (seq 255)" : $"at sequence {seq}";
            return $"on quest '{active.Value.Name}' -- {active.Value.Stage} stage, {where}.";
        }

        var stage = CurrentStage();
        if (stage == RelicStage.None)
            return "Zodiac line not detected (no relic weapon and no active relic quest).";
        if (BaseRelicState.NeedsZenith())
        {
            var pending = BaseRelicState.ZenithPendingWeapons();
            if (pending.Count == 0)
                return "Relic stage -- base relic done; next is the Zenith item gate (base weapon + 3x Thavnairian Mist at the Furnace).";
            return $"Relic stage -- base relic done; next is the Zenith item gate ({BaseRelicState.DescribeZenithPending(pending)} + {BaseRelicState.ZenithMistNeeded(pending)}x Thavnairian Mist at the Furnace).";
        }
        if (BaseRelicState.NeedsFirstBook())
            return "Animus stage -- Atma weapon done; next is buying the first 'Trials of the Braves' book.";
        return $"{stage} stage (no line quest accepted right now; on a grind/item step).";
    }

    // The line quest currently accepted (sequence > 0), if any. Base per-job quest first;
    // then the registry, highest stage winning in the (unexpected) case two are active.
    private static (RelicStage Stage, string Name, uint QuestId)? ActiveLineQuest()
    {
        var job = BaseRelicState.ActiveRelicJob();
        if (job != RelicJob.None && BaseRelicState.RelicQuestSequenceFor(job) > 0)
            return (RelicStage.Relic, BaseRelicState.RelicQuestNameFor(job), BaseRelicState.RelicQuestIdFor(job));

        (RelicStage Stage, string Name, uint QuestId)? best = null;
        foreach (var q in ZodiacQuestRegistry.Quests)
        {
            if (q.Role == ZodiacQuestRole.LineUnlock)
                continue;
            if (GameState.QuestSequence(q.QuestId) > 0 && (best == null || (int)q.Stage > (int)best.Value.Stage))
                best = (q.Stage, q.Name, q.QuestId);
        }
        return best;
    }

    private static RelicStage NextStage(RelicStage s) => s switch
    {
        RelicStage.Relic => RelicStage.Atma,
        RelicStage.Atma => RelicStage.Animus,
        RelicStage.Animus => RelicStage.Novus,
        RelicStage.Novus => RelicStage.Nexus,
        RelicStage.Nexus => RelicStage.Braves,
        RelicStage.Braves => RelicStage.Zeta,
        RelicStage.Zeta => RelicStage.Complete,
        _ => s,
    };
}
