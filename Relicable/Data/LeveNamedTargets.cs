using System;
using System.Collections.Generic;

namespace Relicable.Data;

// Authored "assassination" battle-leve targets: leves whose completion is killing ONE specific named
// enemy, which is guarded by adds ("help close by"). LeveRunner's default fight loop clears the
// NEAREST leve objective, so with adjacent or respawning adds it can tunnel the adds and never reach
// the target -- yet only the target's death completes the leve. For these leves the runner prefers the
// named target whenever it is loaded and ignores the (optional) adds.
//
// Keyed by the leve's English name (resolved at runtime via Sheets.LeveName) so no numeric Leve row id
// is hardcoded, matching EscortLevePaths. The value is the target's BNpcName exactly as it appears in
// the object table (matched case-insensitively by Targeting.FindNearestEnemy).
public static class LeveNamedTargets
{
    public static readonly IReadOnlyDictionary<string, string> Targets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Leve 848: seek out and slay the giant Mimas; the "help close by" (adds) need not die.
            ["Someone's Got a Big Mouth"] = "Mimas",

            // Leve 860 (Coerthas / Whitebrim; the game's CompanyLeveSummon rule; Leve[1] of Trials-of-
            // the-Braves book 3): the "frost aevis" SUMMONS adds (dragonfly chaser / red aevis /
            // blizzard biast) as it fights, but only slaying the aevis itself (1/1) completes the leve,
            // so prefer it and ignore the summoned adds rather than risk tunnelling the respawning adds.
            ["If You Put It That Way"] = "frost aevis",

            // Leve 873 (Mor Dhona; the third CompanyLeveSummon book leve). The summoner is the first /
            // highest-level struct enemy "Okeanos the Red" (summons gigas mastiff / beggar sozu /
            // beggar bonze); slaying it completes the leve. Same derivation as the two above.
            ["Who Writes History"] = "Okeanos the Red",
        };

    // The priority kill-target for an accepted leve name, or null when the leve is not an authored
    // assassination leve (LeveRunner then uses its default nearest-objective fight loop).
    public static string? ForLeveName(string? leveName)
        => !string.IsNullOrEmpty(leveName) && Targets.TryGetValue(leveName!, out var target)
            ? target
            : null;
}
