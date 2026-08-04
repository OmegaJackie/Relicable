using System.Collections.Generic;

namespace Relicable.Model;

// The ten jobs that have an A Realm Reborn Zodiac relic line. Summoner and Scholar
// share the Arcanist base class but have distinct relics, which is why the active
// job is read from the equipped soul-crystal job (ClassJob), not the base class.
// 'None' is the "not detected / no override" value.
public enum RelicJob
{
    None = 0,
    Paladin,
    Warrior,
    Dragoon,
    Monk,
    Ninja,
    Bard,
    BlackMage,
    Summoner,
    WhiteMage,
    Scholar,
}

// Maps between the game's ClassJob sheet ids and RelicJob. ClassJob ids are stable
// game data (verified against the ClassJob Excel sheet):
//   1 GLA / 19 PLD, 2 PGL / 20 MNK, 3 MRD / 21 WAR, 4 LNC / 22 DRG,
//   5 ARC / 23 BRD, 6 CNJ / 24 WHM, 7 THM / 25 BLM, 26 ACN, 27 SMN, 28 SCH,
//   29 ROG / 30 NIN.
//
// Arcanist (26) alone is ambiguous (it can become either Summoner or Scholar), so it
// resolves to None; at level 50 the player is on SMN (27) or SCH (28) specifically,
// which resolve cleanly. Every other base class maps to exactly one relic job.
public static class RelicJobs
{
    public static readonly IReadOnlyList<RelicJob> All = new[]
    {
        RelicJob.Paladin, RelicJob.Warrior, RelicJob.Dragoon, RelicJob.Monk, RelicJob.Ninja,
        RelicJob.Bard, RelicJob.BlackMage, RelicJob.Summoner, RelicJob.WhiteMage, RelicJob.Scholar,
    };

    // ClassJob sheet row id -> relic job. Includes both the base class and the job so
    // detection works whether or not the soul crystal is equipped (except Arcanist).
    private static readonly Dictionary<uint, RelicJob> ByClassJobId = new()
    {
        [1] = RelicJob.Paladin, [19] = RelicJob.Paladin,
        [3] = RelicJob.Warrior, [21] = RelicJob.Warrior,
        [4] = RelicJob.Dragoon, [22] = RelicJob.Dragoon,
        [2] = RelicJob.Monk, [20] = RelicJob.Monk,
        [29] = RelicJob.Ninja, [30] = RelicJob.Ninja,
        [5] = RelicJob.Bard, [23] = RelicJob.Bard,
        [7] = RelicJob.BlackMage, [25] = RelicJob.BlackMage,
        [27] = RelicJob.Summoner,
        [6] = RelicJob.WhiteMage, [24] = RelicJob.WhiteMage,
        [28] = RelicJob.Scholar,
        // 26 (Arcanist) intentionally omitted: ambiguous between Summoner and Scholar.
    };

    // The relic jobs a ClassJob id COULD be when it does not resolve to exactly one. Arcanist is
    // the only ambiguous ARR class -- it becomes either Summoner or Scholar -- and it is what the
    // game reports for a level-50 Arcanist standing there without the soul crystal equipped, or on
    // an ACN/SMN/SCH character in any state where the job stone is not read. Callers that have an
    // independent witness (e.g. which "A Relic Reborn (<weapon>)" quest is live) can use this to
    // narrow the pair down to one; nobody should guess between them without one.
    private static readonly Dictionary<uint, RelicJob[]> AmbiguousByClassJobId = new()
    {
        [26] = new[] { RelicJob.Summoner, RelicJob.Scholar },
    };

    // The relic job for a ClassJob id, or None when unknown/ambiguous.
    public static RelicJob FromClassJobId(uint classJobId)
        => ByClassJobId.TryGetValue(classJobId, out var j) ? j : RelicJob.None;

    // The candidate relic jobs for a ClassJob id that FromClassJobId could not resolve. Empty for
    // an id that is simply not a relic class (a Dark Knight has no candidates, and must not be
    // silently resolved into some other job's line just because that job's quest is open).
    public static IReadOnlyList<RelicJob> AmbiguousCandidates(uint classJobId)
        => AmbiguousByClassJobId.TryGetValue(classJobId, out var many)
            ? many
            : System.Array.Empty<RelicJob>();

    // Human-readable name for the UI and logs.
    public static string DisplayName(RelicJob job) => job switch
    {
        RelicJob.Paladin => "Paladin",
        RelicJob.Warrior => "Warrior",
        RelicJob.Dragoon => "Dragoon",
        RelicJob.Monk => "Monk",
        RelicJob.Ninja => "Ninja",
        RelicJob.Bard => "Bard",
        RelicJob.BlackMage => "Black Mage",
        RelicJob.Summoner => "Summoner",
        RelicJob.WhiteMage => "White Mage",
        RelicJob.Scholar => "Scholar",
        _ => "Unknown",
    };
}
