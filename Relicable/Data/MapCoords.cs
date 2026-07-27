using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Converts the in-game map coordinates the wiki lists (and that the user supplies)
// into world coordinates for navigation. This is the inverse of Dalamud's verified
// forward formula (Dalamud.Utility.MapUtil):
//
//   mapCoord = 0.02*offset + 2048/scale + 0.02*world + 1
//
// solved for world:
//
//   world = 50*(mapCoord - 1) - offset - 102400/scale
//
// Axis mapping (the game swaps Y and Z relative to the map display):
//   map X      -> world X        (uses Map.OffsetX)
//   map Y      -> world Z        (uses Map.OffsetY)
//   map Z (the "Z:" height)  -> world Y = height*100 + zOffset
//
// scale/offsets come from the territory's Map sheet row (SizeFactor, OffsetX,
// OffsetY); most ARR overworld zones use scale 100 and zero offsets, but the sheet
// is read so the conversion is correct everywhere. World Y is left at 0 when no
// height is supplied so the navigation layer can snap it to the navmesh
// (NavmeshIpc.NearestPoint), which is how KillTargetExecutor already consumes it.
public static class MapCoords
{
    private static readonly Dictionary<uint, (uint Scale, int OffX, int OffY)> Cache = new();

    // Convert a territory's map (x, y[, z height]) to a world position.
    public static Vector3 MapToWorld(uint territoryTypeId, float mapX, float mapY, float mapZ = 0f)
    {
        var (scale, offX, offY) = ParamsFor(territoryTypeId);
        var worldX = InvertXZ(mapX, scale, offX);
        var worldZ = InvertXZ(mapY, scale, offY);
        var worldY = mapZ != 0f ? mapZ * 100f : 0f; // 0 -> resolve via navmesh
        return new Vector3(worldX, worldY, worldZ);
    }

    private static float InvertXZ(float mapCoord, uint scale, int offset)
    {
        if (scale == 0)
            scale = 100;
        return 50f * (mapCoord - 1f) - offset - 102400f / scale;
    }

    // SizeFactor / OffsetX / OffsetY for a territory's map, cached. Defaults to
    // (100, 0, 0) when the row is unavailable, which is correct for most ARR zones.
    private static (uint, int, int) ParamsFor(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return (100, 0, 0);
        if (Cache.TryGetValue(territoryTypeId, out var cached))
            return cached;

        var result = (100u, 0, 0);
        try
        {
            var terr = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryTypeId);
            if (terr?.Map.ValueNullable is { } map)
            {
                var scale = map.SizeFactor == 0 ? (uint)100 : map.SizeFactor;
                result = (scale, map.OffsetX, map.OffsetY);
            }
        }
        catch { /* keep the (100,0,0) default */ }

        Cache[territoryTypeId] = result;
        return result;
    }
}
