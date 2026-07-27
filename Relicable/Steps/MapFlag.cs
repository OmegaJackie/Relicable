using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Relicable.Steps;

// Places the in-game map flag programmatically, as the book click helper does when
// the user clicks a Trials of the Braves entry.
//
// Uses AgentMap.SetFlagMapMarker, which sets the same flag a manual placement would
// (without opening the map window). vnavmesh's Query.Mesh.FlagToPoint then resolves
// that flag to a navmesh point, so dropping the flag both shows the user where the
// objective is and gives the navigation layer a destination.
public static unsafe class MapFlag
{
    // Drop a flag at a world position in the player's current zone. Returns false if
    // the map agent is unavailable or no zone is loaded.
    public static bool Set(Vector3 world)
    {
        var map = AgentMap.Instance();
        if (map == null || map->CurrentTerritoryId == 0)
            return false;
        return Set(map->CurrentTerritoryId, map->CurrentMapId, world);
    }

    // Drop a flag at a world position in an explicit territory/map.
    public static bool Set(uint territoryId, uint mapId, Vector3 world)
    {
        var map = AgentMap.Instance();
        if (map == null)
            return false;
        try
        {
            map->SetFlagMapMarker(territoryId, mapId, world);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Drop the flag AND pop the map window open on it -- the visible "click a book entry" behaviour
    // (SetFlagMapMarker alone only sets the flag silently, without opening the map).
    // Returns false if the map agent is unavailable.
    public static bool SetAndOpen(uint territoryId, uint mapId, Vector3 world)
    {
        var map = AgentMap.Instance();
        if (map == null)
            return false;
        try
        {
            map->SetFlagMapMarker(territoryId, mapId, world);
            map->OpenMapByMapId(mapId, territoryId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Removes the in-game map flag (the single flag-marker slot). Mirrors clearing a flag
    // by hand: it zeroes AgentMap's FlagMarkerCount, so TryGetFlag then reports no flag.
    // The same one-liner GatherBuddy / SimpleMapTracker use. Used after a treasure coffer
    // is looted so the spent flag does not linger and get misread as a fresh treasure
    // (this executor -- and other steps -- treat "a flag exists" as an objective to run).
    // Safe to call when no flag is set.
    public static void Clear()
    {
        var map = AgentMap.Instance();
        if (map == null)
            return;
        map->FlagMarkerCount = 0;
    }

    // Reads the current in-game map flag directly from AgentMap, including its
    // territory and map ids. Unlike vnavmesh's FlagToPoint (which only resolves a flag
    // in the currently loaded zone), this returns a flag in ANY zone, so a treasure
    // map flag in a different territory can still be acted on. XFloat/YFloat are world
    // X/Z. Returns false when no flag is set.
    public static bool TryGetFlag(out uint territoryId, out uint mapId, out Vector3 world)
    {
        territoryId = 0;
        mapId = 0;
        world = default;
        var map = AgentMap.Instance();
        if (map == null || map->FlagMarkerCount == 0)
            return false;
        var f = map->FlagMapMarkers[0];
        territoryId = f.TerritoryId;
        mapId = f.MapId;
        world = new Vector3(f.XFloat, 0f, f.YFloat);
        return true;
    }
}
