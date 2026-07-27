using System;
using System.Collections.Generic;

namespace Relicable.Data;

// Authored "make the rounds" battle-leve mechanics (the game's BattleLeveRound rule): leves whose
// objective is to travel to a series of "Destination" marker objects, each of which springs an ambush
// (SPAWNS an enemy) when you get close; you slay it and move to the next Destination. LeveRunner's
// default fight loop only clears loaded BattleLeveDirector objective enemies, so at the start -- before
// any Destination is reached and any enemy has spawned -- it just holds at the anchor and the leve
// times out.
//
// The canonical example is "Circling the Ceruleum" (Leve 646, Northern Thanalan / Camp Bluefog; the
// Leve[0] slot of RelicNote books 3 and 4): "range the roads and slay whatever threatens the trade
// route" -- run to each "Destination", an enemy (plasma spark / diaphanous doblyn / earth sprite /
// ceruleum bomb) ambushes, kill it, repeat.
//
// Keyed by the leve's English name (resolved at runtime via Sheets.LeveName), matching EscortLevePaths
// / LeveNamedTargets / LeveItemLures / LeveStartOverrides. The value is the Destination marker's
// object-table name (matched case-insensitively by Targeting.FindNearestInteractable).
//
// SEAM (offline-untestable; verify in-game): the marker is named "Destination" and is a targetable
// object, and getting within LeveRunner.DestinationArriveRange of it springs the ambush. Both are a
// data / const value so a mismatch is an edit, not a code change; the 300s leve timeout is the backstop.
public static class LeveDestinations
{
    public static readonly IReadOnlyDictionary<string, string> DestinationNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Leve 646, Northern Thanalan (Camp Bluefog). Run to each "Destination" to spring the ambush.
            ["Circling the Ceruleum"] = "Destination",

            // The other two BattleLeveRound book leves (research-confirmed same "make the rounds"
            // mechanic, "Destination" markers): 652 (Coerthas Central Highlands) and 659 (Mor Dhona).
            ["The Area's a Bit Sketchy"] = "Destination",
            ["Put Your Stomp on It"] = "Destination",
        };

    // The Destination marker name for an accepted leve, or null when the leve is not an authored
    // "rounds" leve (LeveRunner then uses its default nearest-objective fight loop).
    public static string? ForLeveName(string? leveName)
        => !string.IsNullOrEmpty(leveName) && DestinationNames.TryGetValue(leveName!, out var name)
            ? name
            : null;
}
