using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;

namespace Relicable.Steps;

// The planner's "click a location" flow: set the map flag, teleport to the zone, then once you
// have arrived and the zone has settled, fly to the flag via vnavmesh's "/vnav flyflag". The fly
// is queued and driven from the plugin update (not the window draw), so it still happens if the
// planner window is closed while the teleport cast / loading screen is in progress.
public static class LocationNavigator
{
    private const long TimeoutMs = 60_000; // give up if the teleport never lands
    private const long SettleMs = 1_500;   // let the zone settle after the loading screen

    // Set once by the plugin constructor so the flight gate can honour AllowFlight.
    public static Configuration? Config { get; set; }

    private static uint _pendingTerritory;
    private static long _startTicks;
    private static long _arrivedTicks;

    // Set the flag, teleport to the zone, and queue a fly-to-flag for once you arrive.
    public static void Go(uint territory, float mapX, float mapY)
    {
        if (territory == 0)
            return;
        GameActions.OpenMapFlag(territory, mapX, mapY);
        GameActions.TeleportToZone(territory);
        _pendingTerritory = territory;
        _startTicks = Environment.TickCount64;
        _arrivedTicks = 0;
    }

    // Same as Go, but from a WORLD position rather than map coordinates: AgentMap.SetFlagMapMarker
    // takes world X/Z directly (MapFlag.SetAndOpen), so a fixed NPC whose world spot is known but whose
    // map coordinate is not authored (e.g. Jalzahn) can be click-to-travelled without a world->map
    // conversion. Flags + opens the map, teleports to the zone's aetheryte, then flies to the flag.
    public static void GoWorld(uint territory, Vector3 world)
    {
        if (territory == 0)
            return;
        var mapId = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.RowId ?? 0u;
        MapFlag.SetAndOpen(territory, mapId, world);
        GameActions.TeleportToZone(territory);
        _pendingTerritory = territory;
        _startTicks = Environment.TickCount64;
        _arrivedTicks = 0;
    }

    // Ticked each frame from the plugin update. Runs "/vnav flyflag" once, after arrival.
    public static void Tick()
    {
        if (_pendingTerritory == 0)
            return;

        if (Environment.TickCount64 - _startTicks > TimeoutMs)
        {
            _pendingTerritory = 0;
            return;
        }

        // Wait until we are actually in the target zone and out of the loading transition.
        if (Plugin.ClientState.TerritoryType != _pendingTerritory
            || Plugin.ObjectTable.LocalPlayer == null
            || Plugin.Condition[ConditionFlag.BetweenAreas]
            || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            _arrivedTicks = 0;
            return;
        }

        // Let the zone settle, then fly to the flag once and clear the pending state.
        if (_arrivedTicks == 0)
            _arrivedTicks = Environment.TickCount64;
        if (Environment.TickCount64 - _arrivedTicks < SettleMs)
            return;

        // Fly only where the game permits it (and AllowFlight is on): /vnav flyflag in
        // a zone without flight unlocked routes through a flight volume the client does
        // not have and sends the character out of bounds -- the same hazard the
        // MoveCloseTo sites gate via Steps.Flight. CanFly is loaded by now (we are in
        // the target zone, past the settle window).
        var fly = Config?.AllowFlight != false && Flight.CanFlyHere();
        var cmd = fly ? "/vnav flyflag" : "/vnav moveflag";
        Plugin.Commands.ProcessCommand(cmd);
        Diagnostics.DebugLog.Info($"Arrived in territory {_pendingTerritory}; heading to the flag ({cmd}).");
        _pendingTerritory = 0;
    }
}
