using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Derives navigation anchors from Lumina where the game data allows it:
//   - territory -> a teleportable aetheryte (Aetheryte sheet)
//   - place name -> territory (TerritoryType sheet)
//   - a leve's levemete (territory, position, NPC) from Leve.LevelLevemete -> Level
//   - a monster-note target's zone -> territory (MonsterNoteTarget.PlaceNameZone)
//
// What is NOT derivable (and therefore needs a hand-authored coordinate table, see
// BraveBookPositions): exact FATE positions (Fate.Location is an EventRange
// instance id, not coordinates) and exact monster spawn positions (MonsterNoteTarget
// only stores zone/location place names). Those remain manual / data-authored.
public static class Locations
{
    private static Dictionary<uint, uint>? _territoryAetheryte;
    private static Dictionary<uint, uint>? _placeNameTerritory;

    // A teleportable aetheryte row id for a territory, or 0 if none.
    public static uint AetheryteForTerritory(uint territory)
    {
        EnsureMaps();
        return territory != 0 && _territoryAetheryte!.TryGetValue(territory, out var a) ? a : 0;
    }

    // Human-readable label for a teleport destination: the aetheryte's own place name plus the zone
    // it sits in, e.g. "Bentbranch Meadows (Central Shroud)". Pure Lumina (never touches Telepo, which
    // faults while the world loads), so it is safe to build a tooltip with every frame; memoised
    // because the sheets do not change. Empty when the row is missing.
    public static string AetheryteLabel(uint aetheryteId)
    {
        if (aetheryteId == 0)
            return string.Empty;
        if (_aetheryteLabels.TryGetValue(aetheryteId, out var cached))
            return cached;

        var label = string.Empty;
        try
        {
            if (Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId) is { } a)
            {
                var spot = a.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var zone = a.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                label = spot.Length > 0 && zone.Length > 0 && spot != zone
                    ? $"{spot} ({zone})"
                    : spot.Length > 0 ? spot : zone;
            }
        }
        catch { /* leave empty; the caller falls back to a generic label */ }

        _aetheryteLabels[aetheryteId] = label;
        return label;
    }

    private static readonly Dictionary<uint, string> _aetheryteLabels = new();

    // The teleport aetheryte in `territory` whose world position is nearest to `worldTarget`, and
    // that aetheryte's world position (X, 0, Z). Aetheryte coordinates are not stored on the
    // Aetheryte row; they come from the MapMarker sheet (DataType 3, DataKey = aetheryte row) the
    // same way Lifestream locates them, run through the SAME map->world transform
    // BraveBookPositions uses (MapLinkPayload raw / 1000) so the result shares Relicable's world
    // space and distances compare directly. Null when the territory has no teleportable aetheryte or
    // its map marker cannot be resolved.
    public static (uint AetheryteId, Vector3 World)? NearestAetheryteToWorld(uint territory, uint mapId, Vector3 worldTarget)
    {
        if (territory == 0 || mapId == 0)
            return null;
        try
        {
            var map = Plugin.DataManager.GetExcelSheet<Map>().GetRowOrDefault(mapId);
            var scale = map?.SizeFactor ?? 100;
            var markers = Plugin.DataManager.GetSubrowExcelSheet<MapMarker>();

            (uint Id, Vector3 World)? best = null;
            var bestDist = float.MaxValue;
            foreach (var a in Plugin.DataManager.GetExcelSheet<Aetheryte>())
            {
                if (!a.IsAetheryte || a.Territory.RowId != territory)
                    continue;

                // The aetheryte's on-map marker (DataType 3 == aetheryte, keyed by its row id).
                MapMarker marker = default;
                var found = false;
                foreach (var sub in markers)
                {
                    foreach (var m in sub)
                    {
                        if (m.DataType == 3 && m.DataKey.RowId == a.RowId)
                        {
                            marker = m;
                            found = true;
                            break;
                        }
                    }
                    if (found)
                        break;
                }
                if (!found)
                    continue;

                var aMapX = ConvertRawPositionToMapCoordinate(marker.X, scale);
                var aMapY = ConvertRawPositionToMapCoordinate(marker.Y, scale);
                var link = new MapLinkPayload(territory, mapId, aMapX, aMapY);
                var world = new Vector3(link.RawX / 1000f, 0f, link.RawY / 1000f);

                var d = Vector3.DistanceSquared(
                    new Vector3(world.X, 0f, world.Z),
                    new Vector3(worldTarget.X, 0f, worldTarget.Z));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = (a.RowId, world);
                }
            }
            return best;
        }
        catch { return null; }
    }

    // Raw-map-marker -> in-game map coordinate conversion. The MapMarker X/Y are stored
    // in map-pixel units; this yields the human map coordinate (the value shown in-game), which
    // MapLinkPayload then turns into a world position.
    private static float ConvertRawPositionToMapCoordinate(int pos, float scale)
    {
        var c = scale / 100.0f;
        var scaledPos = pos * c / 1000.0f;
        return 41.0f / c * ((scaledPos + 1024.0f) / 2048.0f) + 1.0f;
    }

    // The territory whose zone place-name matches, or 0.
    public static uint TerritoryForPlaceName(uint placeName)
    {
        EnsureMaps();
        return placeName != 0 && _placeNameTerritory!.TryGetValue(placeName, out var t) ? t : 0;
    }

    // The leve's category name (LeveAssignmentType, e.g. "Grand Company Leves",
    // "Battlecraft Leves"), used to select the correct tab in the levemete's category menu.
    // The Trials of the Braves book leves are Grand Company leves, which sit under a different
    // category than the generic regional battlecraft leves. Empty when unresolved.
    public static string LeveCategoryName(uint leveId)
    {
        if (leveId == 0)
            return string.Empty;
        try
        {
            var leve = Plugin.DataManager.GetExcelSheet<Leve>().GetRowOrDefault(leveId);
            if (leve is not { } l)
                return string.Empty;
            return l.LeveAssignmentType.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // Resolve a leve's row id from its display name, scoped to a specific levemete (its
    // LevelLevemete NPC), so a name shown on the GuildLeve board maps back to a leve id we can
    // accept. Scoping to the levemete keeps names unambiguous (the same leve name can recur across
    // grand companies / zones). Returns 0 when no offered-at-this-levemete leve matches.
    public static uint LeveIdByNameAtLevemete(string name, uint levemeteDataId)
    {
        if (string.IsNullOrEmpty(name) || levemeteDataId == 0)
            return 0;
        try
        {
            foreach (var leve in Plugin.DataManager.GetExcelSheet<Leve>())
            {
                if (leve.RowId == 0 || leve.LevelLevemete.ValueNullable is not { } lvl)
                    continue;
                if (lvl.Object.RowId != levemeteDataId)
                    continue;
                if (string.Equals(leve.Name.ExtractText(), name, System.StringComparison.Ordinal))
                    return leve.RowId;
            }
        }
        catch { /* unresolved */ }
        return 0;
    }

    // Levemete location for a leve: territory, world position, and NPC data id.
    public static (uint Territory, Vector3 Pos, uint NpcId)? LeveLevemete(uint leveId)
    {
        if (leveId == 0)
            return null;
        try
        {
            var leve = Plugin.DataManager.GetExcelSheet<Leve>().GetRowOrDefault(leveId);
            if (leve is not { } l || l.LevelLevemete.ValueNullable is not { } lvl)
                return null;
            return (lvl.Territory.RowId, new Vector3(lvl.X, lvl.Y, lvl.Z), lvl.Object.RowId);
        }
        catch { return null; }
    }

    // Territory of a monster-note target's primary zone, or 0.
    public static uint MonsterTerritory(uint monsterNoteTargetId)
    {
        if (monsterNoteTargetId == 0)
            return 0;
        try
        {
            var m = Plugin.DataManager.GetExcelSheet<MonsterNoteTarget>().GetRowOrDefault(monsterNoteTargetId);
            if (m is not { } mt)
                return 0;
            var zone = mt.PlaceNameZone;
            return zone.Count > 0 ? TerritoryForPlaceName(zone[0].RowId) : 0u;
        }
        catch { return 0; }
    }

    private static void EnsureMaps()
    {
        if (_territoryAetheryte != null)
            return;

        var ta = new Dictionary<uint, uint>();
        var pt = new Dictionary<uint, uint>();
        try
        {
            foreach (var a in Plugin.DataManager.GetExcelSheet<Aetheryte>())
            {
                if (!a.IsAetheryte)
                    continue;
                var terr = a.Territory.RowId;
                if (terr != 0 && !ta.ContainsKey(terr))
                    ta[terr] = a.RowId;
            }
            foreach (var t in Plugin.DataManager.GetExcelSheet<TerritoryType>())
            {
                var pn = t.PlaceName.RowId;
                if (pn != 0 && !pt.ContainsKey(pn))
                    pt[pn] = t.RowId;
            }
        }
        catch { /* leave whatever was gathered */ }

        _territoryAetheryte = ta;
        _placeNameTerritory = pt;
    }
}
