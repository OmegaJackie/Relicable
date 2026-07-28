using System;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Relicable.Data;

namespace Relicable.Steps;

// One-shot game-UI actions used by the planner windows: open a map flag, teleport to a zone's
// aetheryte, and open the Duty Finder for a duty. All are best-effort and guarded so a UI
// click can never take down the plugin.
public static unsafe class GameActions
{
    // Open the map with a flag at the given map coordinates in a territory.
    public static void OpenMapFlag(uint territory, float mapX, float mapY)
    {
        if (territory == 0)
            return;
        try
        {
            var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
            var mapId = tt?.Map.RowId ?? 0u;
            if (mapId == 0)
                return;
            Plugin.GameGui.OpenMapWithMapLink(new MapLinkPayload(territory, mapId, mapX, mapY));
        }
        catch (Exception ex) { Diagnostics.DebugLog.Warn($"OpenMapFlag failed: {ex.Message}"); }
    }

    // Teleport to the territory's aetheryte, unless already in that territory.
    public static void TeleportToZone(uint territory)
    {
        if (territory == 0 || Plugin.ClientState.TerritoryType == territory)
            return;
        var aetheryte = Locations.AetheryteForTerritory(territory);
        if (aetheryte == 0)
            return;
        try
        {
            // Routed through Teleporter so a UI click honours the Aetheryte Ticket policy exactly
            // as the engine's own teleport step does. It also gates on Teleporter.SafeToQuery:
            // the native UpdateAetheryteList faults if the world is still loading, and a try/catch
            // cannot catch that.
            Teleporter.Teleport(aetheryte);
        }
        catch (Exception ex) { Diagnostics.DebugLog.Warn($"Teleport failed: {ex.Message}"); }
    }

    // Open the in-game Trials of the Braves relic note book (the RelicNoteBook addon), to whichever
    // book is currently active, via its agent (AgentId.RelicNotebook = 147). Best-effort: does
    // nothing if the agent is unavailable (e.g. no book held / not on the Animus stage). Once open,
    // clicking an enemy/dungeon/FATE/leve entry flags it on the map (Braves.RelicNoteBookHook).
    public static void OpenRelicNoteBook()
    {
        try
        {
            // AgentModule.Instance() is null while UIModule is unavailable, and the try/catch
            // cannot intercept the native access violation a null deref would raise.
            var module = AgentModule.Instance();
            if (module == null)
                return;
            var agent = module->GetAgentByInternalId(AgentId.RelicNotebook);
            if (agent != null)
                agent->Show();
        }
        catch (Exception ex) { Diagnostics.DebugLog.Warn($"OpenRelicNoteBook failed: {ex.Message}"); }
    }

    // Teleport to a SPECIFIC aetheryte the caller has already chosen (e.g. the one nearest a flag).
    // Unlike TeleportToZone this does NOT skip based on the current territory -- the caller decides
    // whether a teleport is warranted, so an in-zone hop to a closer aetheryte is allowed.
    public static void TeleportToAetheryte(uint aetheryteId)
    {
        if (aetheryteId == 0)
            return;
        try
        {
            // Same guard and the same ticket policy as TeleportToZone.
            Teleporter.Teleport(aetheryteId);
        }
        catch (Exception ex) { Diagnostics.DebugLog.Warn($"TeleportToAetheryte failed: {ex.Message}"); }
    }

    // Open the Duty Finder selected to a duty. Does NOT queue it.
    public static void OpenDutyFinder(uint contentFinderConditionId)
    {
        if (contentFinderConditionId == 0)
            return;
        try
        {
            var a = AgentContentsFinder.Instance();
            if (a == null)
                return;
            a->OpenRegularDuty(contentFinderConditionId);
        }
        catch (Exception ex) { Diagnostics.DebugLog.Warn($"OpenDutyFinder failed: {ex.Message}"); }
    }
}
