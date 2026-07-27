using System;
using System.Collections.Generic;

namespace Relicable.Data;

// Authored "defend the charge" battle-leve mechanics (the game's CompanyLeveProtection rule, objective
// "Defeat the enemies while protecting your charge"). Unlike a plain kill leve, a Protection leve FAILS
// the instant its protected CHARGE -- a stationary allied OBJECT the enemies converge on and attack --
// is destroyed; there is no "clear everything at the anchor" completion. LeveRunner's default RunFight
// holds at the leve start anchor, which for these leves is NOT where the charge sits, so it never
// intercepts the attackers, the charge dies, and the leve loops (re-accepted, re-run, never credited).
//
// The fix (RunProtection) holds ON the charge -- acquired LIVE from the object table by name -- so every
// converging attacker comes into melee + line-of-sight range as it arrives, mirroring the dev's
// anchor-on-the-artifact fix for the sibling defend leve 875 "The Museum Is Closed" but without needing
// a hand-captured deck position per leve.
//
// Keyed by the leve's English name (resolved at runtime via Sheets.LeveName), matching EscortLevePaths /
// LeveNamedTargets / LeveItemLures / LeveDestinations / LeveStartOverrides. The value is the CHARGE's
// object-table name (its BNpcName, matched case-insensitively by Targeting.FindNamed).
//
// NOT included: 870 "Get off Our Lake" is CompanyLeveInterception ("defeat as many enemies as possible
// in the time"), NOT Protection -- it has no charge and must stay on the default RunFight.
//
// SEAM (offline-untestable; verify in-game): the charge's object-table name matches the value below, and
// vnavmesh can actually path onto the charge's spot (868's "research document" sits ~6y up on the Agrius
// wreck, above the floor landing anchor). Charge names are datamined (BNpcName 1795 / 2242 / 2243); a
// mismatch is a data edit, not a code change, and RunProtection degrades to the plain anchor hold (no
// worse than the old behaviour) when the charge is not found. The 300s leve timeout is the backstop.
public static class LeveProtection
{
    public static readonly IReadOnlyDictionary<string, string> Charges =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 868, Mor Dhona (the wrecked Agrius). Protect the salvaged "research document" (BNpcName
            // 1795) from a group of gigas / hippogryphs while it is examined.
            ["The Awry Salvages"] = "research document",

            // 855, Coerthas Central Highlands. Protect the fallen "soldiers' effects" (BNpcName 2242).
            ["The Bloodhounds of Coerthas"] = "soldiers' effects",

            // 865, Mor Dhona. Secure the crashed "airship wreckage" (BNpcName 2243); this leve also
            // spawns FRIENDLY allied defenders linked to the same director (already excluded by the
            // hostile-only objective finder), so the charge here is the wreckage object, not an ally.
            ["Go Home to Mama"] = "airship wreckage",
        };

    // The protected charge's name for an accepted leve, or null when the leve is not an authored
    // Protection leve (LeveRunner then uses its default nearest-objective fight loop).
    public static string? ForLeveName(string? leveName)
        => !string.IsNullOrEmpty(leveName) && Charges.TryGetValue(leveName!, out var name)
            ? name
            : null;
}
