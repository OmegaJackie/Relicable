using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Resolves the AutoDuty "DutyMode" appropriate for a duty's TerritoryType, so the
// farm queues a dungeon as Regular and a trial as Trial without the user having to
// match the mode to the content by hand. AutoDuty only honours its Unsynced flag for
// the Regular / Trial / Raid modes, so getting this right is what lets an unsynced
// solo farm actually queue.
//
// ContentType ids (verified via the ContentFinderCondition sheet): 2 = Dungeon,
// 4 = Trial, 5 = Raid. Anything else falls back to Regular.
public static class DutyInfo
{
    private static readonly Dictionary<uint, string> Cache = new();

    public static string DutyModeForTerritory(uint territory)
    {
        if (territory == 0)
            return "Regular";
        if (Cache.TryGetValue(territory, out var cached))
            return cached;

        var mode = "Regular";
        try
        {
            foreach (var cfc in Plugin.DataManager.GetExcelSheet<ContentFinderCondition>())
            {
                if (cfc.TerritoryType.RowId != territory)
                    continue;
                mode = cfc.ContentType.RowId switch
                {
                    4 => "Trial",
                    5 => "Raid",
                    _ => "Regular",
                };
                break;
            }
        }
        catch { /* leave the Regular default */ }

        Cache[territory] = mode;
        return mode;
    }
}
