using System;
using System.Collections.Generic;
using System.Numerics;

namespace Relicable.Data;

// Authored leve anchor coordinates that OVERRIDE the sheet's Leve.LevelStart -> Level position for
// leves whose sheet point resolves to a bad spot -- e.g. below the walkable floor, so the land/dismount
// probe (Mount.LandAndDismount's PointOnFloor, which searches downward from the given Y) snaps to an
// underground poly and the character lands under the ground and shuttles back and forth.
//
// The value is a REAL walkable standing position (all three components), captured from vnavmesh's own
// player-position readout at a confirmed-good spot -- the same provenance as EscortLevePaths waypoints.
// Keyed by the leve's English name (Sheets.LeveName), so no numeric Leve row id is hardcoded.
public static class LeveStartOverrides
{
    public static readonly IReadOnlyDictionary<string, Vector3> Positions =
        new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
        {
            // Leve 849 (Coerthas Central Highlands). The sheet LevelStart Y=204.05 sits ~6.6y BELOW the
            // real floor (Y=210.66), so the dismount landed underground and shuttled. This is vnav's
            // player position on the good floor (poly 1000004A00001).
            ["An Imp Mobile"] = new Vector3(-615.680f, 210.663f, -359.456f),

            // Leve 868 (Mor Dhona, the wrecked Agrius). The sheet LevelStart Y=-6.22 resolves ~6.2y ABOVE
            // the real ground (Y=-12.45), onto the wreck geometry, so landing/pathing snapped to the
            // wreck instead of the floor. This is vnav's player position on the real floor (poly
            // 100000210014E).
            ["The Awry Salvages"] = new Vector3(128.397f, -12.452f, -472.926f),

            // Leve 853 (Coerthas Central Highlands, Whitebrim). SAME broken anchor as "An Imp Mobile"
            // above: the sheet LevelStart Y=204.046 sits ~6.6y BELOW the real courtyard floor
            // (Y=210.663), so PointOnFloor snapped to an underground poly and the dismount spazzed /
            // kept dropping "even lower". This is the leve's own sheet X/Z with the floor Y from the
            // adjacent An Imp Mobile capture (same flat Whitebrim staging floor, ~5y away, poly
            // 1000004A00001). SEAM: the floor Y is borrowed, not captured at this exact spot -- if it
            // still misbehaves, replace with vnav's player position captured on the ground here.
            ["Yellow Is the New Black"] = new Vector3(-615.127f, 210.663f, -354.752f),

            // Leve 875 (Mor Dhona, Saint Coinach's Find). A DEFEND leve ("guard the artifact store and
            // dispatch the unruly visitors"); the sheet LevelStart (95.399, -4.686, -475.12) sits ~11y
            // off the artifact we defend, so the hold-at-anchor fight held away from where the mobs
            // converge. This is vnav's player position at the CENTER of the location to defend (poly
            // 10000020000B2), captured in-game, so the fight holds on the artifact.
            ["The Museum Is Closed"] = new Vector3(86.878f, -4.935f, -468.179f),
        };

    // The authored anchor for a leve name, or null to use the sheet's LevelStart.
    public static Vector3? ForLeveName(string? leveName)
        => !string.IsNullOrEmpty(leveName) && Positions.TryGetValue(leveName!, out var pos)
            ? pos
            : (Vector3?)null;
}
