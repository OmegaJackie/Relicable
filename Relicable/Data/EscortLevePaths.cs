using System;
using System.Collections.Generic;
using System.Numerics;

namespace Relicable.Data;

// Authored escort-leve routes: the ordered world waypoints the player walks while
// guiding a leve's escort NPC (target it, /beckon it to follow, clear the ambushes
// that spawn en route). A battle leve of the escort type never completes from the
// fight loop that LeveRunner uses for kill leves -- the objective is to lead the NPC
// to a destination, not to clear a spawn.
//
// The game sheets do NOT contain these paths (a leve objective is an EventRange /
// script, not a coordinate list) -- the same gap BraveBookPositions fills for monster
// and FATE coordinates. So they are transcribed here from a manual run of the leve,
// captured off vnavmesh's player-position field.
//
// Keyed by the leve's English name (resolved from the accepted leve at runtime via
// Sheets.LeveName) so no numeric Leve row id has to be hardcoded here; the id varies
// by patch data and is not needed once we have the name. Waypoints are the game world
// Vector3 (Y = height) exactly as vnavmesh reports them.
public static class EscortLevePaths
{
    // One escort route: the NPC to target-and-beckon, and the points to walk in order.
    public sealed record EscortRoute(string EscortNpcName, IReadOnlyList<Vector3> Waypoints);

    public static readonly IReadOnlyDictionary<string, EscortRoute> Routes =
        new Dictionary<string, EscortRoute>(StringComparer.OrdinalIgnoreCase)
        {
            // Guide the "Mine Hound" to the mine entrance while killing enemies along
            // the way. Manually captured, points 1..9 in walk order.
            ["Someone's in the Doghouse"] = new EscortRoute(
                EscortNpcName: "Mine Hound",
                Waypoints: new[]
                {
                    new Vector3(76.459f, 15.185f, 245.274f),
                    new Vector3(56.580f, 14.890f, 255.491f),
                    new Vector3(35.802f, 11.838f, 264.833f),
                    new Vector3(13.930f,  5.647f, 276.442f),
                    new Vector3( 9.265f,  4.113f, 299.169f),
                    new Vector3( 7.110f,  2.169f, 323.247f),
                    new Vector3( 5.807f,  2.000f, 347.862f),
                    new Vector3(12.536f,  3.706f, 369.652f),
                    new Vector3(22.879f,  3.985f, 391.557f),
                }),

            // Guide the lost "Snowshoe Mouse" across the wintry plains of Whitebrim (Coerthas). Leve
            // 654 "Pets Are Family Too" is a retrieve/escort leve: target the mouse and /beckon it to
            // follow along the route while clearing anything that aggros. Points 1..12 captured in walk
            // order off vnavmesh's player-position field during a manual run (target + /beckon each).
            ["Pets Are Family Too"] = new EscortRoute(
                EscortNpcName: "Snowshoe Mouse",
                Waypoints: new[]
                {
                    new Vector3(-295.747f, 261.244f, -122.411f),
                    new Vector3(-312.330f, 256.036f, -123.312f),
                    new Vector3(-330.994f, 251.298f, -127.064f),
                    new Vector3(-347.307f, 246.956f, -129.116f),
                    new Vector3(-363.084f, 238.306f, -143.362f),
                    new Vector3(-373.313f, 231.260f, -151.310f),
                    new Vector3(-388.992f, 226.456f, -153.865f),
                    new Vector3(-407.768f, 221.792f, -161.345f),
                    new Vector3(-427.133f, 219.596f, -169.342f),
                    new Vector3(-444.557f, 218.403f, -176.538f),
                    new Vector3(-465.825f, 210.878f, -184.552f),
                    new Vector3(-481.630f, 211.062f, -199.436f),
                }),
        };

    // The escort route for an accepted leve name, or null when the leve is not an
    // authored escort (LeveRunner then falls back to its kill-leve fight loop).
    public static EscortRoute? ForLeveName(string? leveName)
        => !string.IsNullOrEmpty(leveName) && Routes.TryGetValue(leveName!, out var route)
            ? route
            : null;
}
