using System.Collections.Generic;
using Relicable.Model;

namespace Relicable.Data;

// The role a quest plays inside a Zodiac stage.
public enum ZodiacQuestRole
{
    LineUnlock,     // one-time unlock for the whole line (introduces Gerolt)
    StageMain,      // the one-time quest that opens a stage
    StageSecondary, // an additional intro quest for the same stage
    Subquest,       // a required sub-quest branching off a stage's main quest
    Finisher,       // a first-weapon-only finisher for a stage
}

// One Zodiac-line quest with its verified Quest-sheet row id. QuestId is the FULL row id
// (it includes the 0x10000 "quest" base): pass it straight to GameState.QuestSequence /
// IsQuestComplete / QuestWorkVariables, which mask it to the ushort the game uses.
public sealed record ZodiacQuest(
    RelicStage Stage,
    string Name,
    uint QuestId,
    ZodiacQuestRole Role,
    string Note = "")
{
    public ushort MaskedId => (ushort)(QuestId & 0xFFFF);
}

// The complete set of one-time quests that make up the ARR Zodiac relic line, so the plugin
// can read the player's live position at ANY stage -- not only the base "A Relic Reborn"
// quest. Two stages have no single row id here and are handled from other sources:
//   * Relic (base): the weapon quest is PER JOB ("A Relic Reborn (<weapon>)"), so it is
//     resolved for the equipped job by BaseRelicState (RelicQuestIdFor / RelicQuestSequenceFor).
//   * Zenith: a pure item gate (base weapon + 3x Thavnairian Mist at the Furnace) with no quest.
//
// All ids are verified against Quest.csv (ffxiv-kb game-data/relics/zodiac.json, all ids
// independently re-verified by exact-id read-back + name match, 2026-07-09).
public static class ZodiacQuestRegistry
{
    // Line unlock (introduces Gerolt); one-time, gates accepting any relic weapon quest.
    public const uint WeaponsmithOfLegendId = 66241; // masked 705

    public static readonly IReadOnlyList<ZodiacQuest> Quests = new[]
    {
        new ZodiacQuest(RelicStage.Relic, "The Weaponsmith of Legend", WeaponsmithOfLegendId, ZodiacQuestRole.LineUnlock,
            "Line unlock (introduces Gerolt); the per-job 'A Relic Reborn (<weapon>)' quest then forges the base weapon."),

        new ZodiacQuest(RelicStage.Atma, "Up in Arms", 66971, ZodiacQuestRole.StageMain,
            "Introduces Jalzahn; then farm 12 Atma from FATEs with the Zenith weapon equipped."),

        new ZodiacQuest(RelicStage.Animus, "Trials of the Braves", 66972, ZodiacQuestRole.StageMain,
            "Introduces G'Jusana; then complete 9 'Trials of the Braves' books."),

        new ZodiacQuest(RelicStage.Novus, "Celestial Radiance", 66998, ZodiacQuestRole.StageMain,
            "Introduces Hubairtin; then infuse 75 materia into a Sphere Scroll."),
        new ZodiacQuest(RelicStage.Novus, "Star Light, Star Bright", 67000, ZodiacQuestRole.StageSecondary,
            "Second Novus intro quest (also Hubairtin)."),

        new ZodiacQuest(RelicStage.Nexus, "Mmmmmm, Soulglazed Relics", 65742, ZodiacQuestRole.StageMain,
            "Jalzahn soulglazes the Novus weapon; then farm Light until full."),

        new ZodiacQuest(RelicStage.Braves, "Wherefore Art Thou, Zodiac", 65892, ZodiacQuestRole.StageMain,
            "il125 Zodiac Braves umbrella; then 4 sub-quests plus material gathering."),
        new ZodiacQuest(RelicStage.Braves, "A Ponze of Flesh", 65893, ZodiacQuestRole.Subquest),
        new ZodiacQuest(RelicStage.Braves, "Labor of Love", 65894, ZodiacQuestRole.Subquest),
        new ZodiacQuest(RelicStage.Braves, "Method in His Malice", 65895, ZodiacQuestRole.Subquest),
        new ZodiacQuest(RelicStage.Braves, "A Treasured Mother", 65896, ZodiacQuestRole.Subquest),
        new ZodiacQuest(RelicStage.Braves, "His Dark Materia", 65897, ZodiacQuestRole.Finisher,
            "First-weapon-only finisher at Gerolt; later weapons use Jalzahn 'Zodiac Weapon Recreation'."),

        new ZodiacQuest(RelicStage.Zeta, "Rise and Shine", 66096, ZodiacQuestRole.StageMain,
            "Introduces Remon in Swiftperch; then awaken 12 Mahatma via duties."),
    };

    // The stage-opening (main) quest for a stage, or null when the stage has none in the
    // registry (Relic's base is per-job; Zenith is an item gate).
    public static ZodiacQuest? MainFor(RelicStage stage)
    {
        foreach (var q in Quests)
            if (q.Stage == stage && q.Role == ZodiacQuestRole.StageMain)
                return q;
        return null;
    }
}
