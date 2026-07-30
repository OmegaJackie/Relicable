using System;
using System.Numerics;
using Lumina.Excel.Sheets;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Steps;

namespace Relicable.Braves;

// Book click-to-travel helper: click a Trials of the Braves book entry -> flag the target + teleport.
// Given the active book id and the clicked tab/slot (from RelicNoteBookHook), resolves the objective's
// location from the SAME authored data the automation uses -- BraveBookPositions world coords, Locations
// levemete/aetheryte -- then drops the in-game map flag and teleports to the zone. Dungeon entries open
// the Duty Finder instead (there is no open-world spot to flag).
public static class BraveBookNavigator
{
    // Tab indices in the RelicNoteBook addon's category list (CategoryList->SelectedItemIndex).
    public const int TabEnemies = 0;
    public const int TabDungeons = 1;
    public const int TabFates = 2;
    public const int TabLeves = 3;

    // Flag (and optionally teleport to) the objective at (tab, slot) of the given book.
    public static void Go(uint bookId, int tab, int slot, bool teleport)
    {
        try
        {
            if (Plugin.DataManager.GetExcelSheet<RelicNote>().GetRowOrDefault(bookId) is not { } note)
                return;

            switch (tab)
            {
                case TabEnemies:
                {
                    var id = SlotId(note.MonsterNoteTargetCommon, slot);
                    if (id == 0)
                        return;
                    FlagAndTeleport(MonsterTerritory(id), BraveBookPositions.MonsterWorld(id), teleport, $"enemy {id}");
                    break;
                }
                case TabFates:
                {
                    var id = SlotId(note.Fate, slot);
                    if (id == 0)
                        return;
                    FlagAndTeleport(BraveBookPositions.FateTerritory(id), BraveBookPositions.FateWorld(id), teleport, $"FATE {id}");
                    break;
                }
                case TabLeves:
                {
                    var id = SlotId(note.Leve, slot);
                    if (id == 0)
                        return;
                    if (Locations.LeveLevemete(id) is { } lm)
                        FlagAndTeleport(lm.Territory, lm.Pos, teleport, $"leve {id}");
                    else
                        DebugLog.Warn($"Book click: leve {id} has no levemete location");
                    break;
                }
                case TabDungeons:
                {
                    var id = SlotId(note.MonsterNoteTargetNM, slot);
                    if (id == 0)
                        return;
                    var cfc = DungeonCfcForTerritory(MonsterTerritory(id));
                    if (cfc != 0)
                        GameActions.OpenDutyFinder(cfc);
                    else
                        DebugLog.Warn($"Book click: dungeon slot {slot} (NM {id}) did not resolve to a duty");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Book click navigate failed: {ex.Message}");
        }
    }

    // RowId of a book collection slot, or 0 when out of range / empty.
    private static uint SlotId<T>(Lumina.Excel.Collection<Lumina.Excel.RowRef<T>> col, int slot)
        where T : struct, Lumina.Excel.IExcelRow<T>
        => slot >= 0 && slot < col.Count ? col[slot].RowId : 0u;

    // Prefer the authored BraveBookPositions territory (always correct), then derive it from the sheets.
    private static uint MonsterTerritory(uint monsterNoteTargetId)
    {
        var t = BraveBookPositions.MonsterTerritory(monsterNoteTargetId);
        return t != 0 ? t : Locations.MonsterTerritory(monsterNoteTargetId);
    }

    // Yalms the aetheryte must beat the on-foot distance by before an IN-ZONE teleport is worth it:
    // teleporting costs a cast + a short zone hop, so a near-tie should just walk.
    private const float TeleportMargin = 30f;

    private static void FlagAndTeleport(uint territory, Vector3? world, bool teleport, string label)
    {
        if (territory == 0 || world is not { } w)
        {
            DebugLog.Warn($"Book click: {label} has no authored location; cannot flag");
            return;
        }
        var mapId = MapIdForTerritory(territory);
        // Open the map window AND drop the flag on a book-entry click (a
        // silent flag with no map is easy to miss). vnavmesh's FlagToPoint still reads this flag.
        if (mapId != 0)
            MapFlag.SetAndOpen(territory, mapId, w);
        if (teleport)
            TeleportTowardFlag(territory, mapId, w);
        DebugLog.Info($"Book click: flagged {label} in territory {territory}");
    }

    // Teleport toward the flagged objective, but distance-aware:
    //   - a DIFFERENT zone: always teleport to the aetheryte NEAREST the flag (you cannot walk there).
    //   - the SAME zone: only teleport if that nearest aetheryte is meaningfully closer to the flag
    //     than you already are; otherwise just travel on foot from where you stand (no pointless hop).
    private static void TeleportTowardFlag(uint territory, uint mapId, Vector3 flagWorld)
    {
        var nearest = Locations.NearestAetheryteToWorld(territory, mapId, flagWorld);
        if (nearest is not { } aeth)
        {
            // No resolvable aetheryte position: fall back to the plain zone teleport, which itself
            // skips when we are already in the target territory.
            GameActions.TeleportToZone(territory);
            return;
        }

        if (Plugin.ClientState.TerritoryType == territory)
        {
            // Already in the flag's zone: compare walking from here vs teleporting to the nearest
            // aetheryte and walking from there (flat XZ distance, a good proxy for travel time).
            if (Plugin.ObjectTable.LocalPlayer?.Position is not { } me)
                return; // in the zone but no player yet: stay put and let the player/automation walk
            var onFoot = FlatDistance(me, flagWorld);
            var fromAetheryte = FlatDistance(aeth.World, flagWorld);
            if (fromAetheryte + TeleportMargin >= onFoot)
            {
                DebugLog.Info($"Book click: already in zone, closer on foot ({onFoot:0}y vs {fromAetheryte:0}y from the aetheryte); not teleporting");
                return;
            }
            DebugLog.Info($"Book click: teleporting to the nearer aetheryte ({fromAetheryte:0}y from the flag vs {onFoot:0}y on foot)");
        }

        GameActions.TeleportToAetheryte(aeth.AetheryteId);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
        => Vector3.Distance(new Vector3(a.X, 0f, a.Z), new Vector3(b.X, 0f, b.Z));

    private static uint MapIdForTerritory(uint territory) => Locations.MapForTerritory(territory);

    // A dungeon (ContentType 2) ContentFinderCondition for a territory, or 0. Used to open the Duty
    // Finder for a book's dungeon slot, which has no open-world location to flag.
    private static uint DungeonCfcForTerritory(uint territory)
    {
        if (territory == 0)
            return 0u;
        try
        {
            foreach (var cfc in Plugin.DataManager.GetExcelSheet<ContentFinderCondition>())
                if (cfc.ContentType.RowId == 2 && cfc.TerritoryType.RowId == territory)
                    return cfc.RowId;
        }
        catch { /* unresolved */ }
        return 0u;
    }
}
