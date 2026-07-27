using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps;

// Drives the live "RelicSphereScroll" addon (the ARR Zodiac Novus materia-infusion
// window) to infuse one materia of a given type+grade into the Sphere Scroll.
//
// The addon is a 28-option list (7 secondary stats x 4 grades). Its AtkValue layout
// (from the LlamaLibrary RebornBuddy driver, validated by the runtime dumps this code
// logs): option name at 31+i, stat string at 59+i, available count at 87+i, and a
// selectable/highlight flag at 115+i, for i in 0..27. Selecting option i is
// FireCallback(2, [Int 0, Int i]); confirming the infusion is FireCallback(1, [Int 2]).
//
// EXPERIMENTAL. Selection is matched by the materia's item NAME (so it is robust to
// the option ordering), and the option must be flagged selectable with count > 0
// before anything is fired -- so it never infuses the wrong materia or one the game
// would reject. Select and confirm are split across two ticks so the selection is
// registered before the infuse. Progress is judged by materia actually consumed.
internal static unsafe class RelicMeld
{
    private const string AddonName = "RelicSphereScroll";

    // AtkValue layout of RelicSphereScroll.
    private const int NameStart = 31;
    private const int CountStart = 87;
    private const int HighlightStart = 115;
    private const int OptionCount = 28;
    private const int MinValues = HighlightStart + OptionCount; // 143

    // Active-stat summary block: [19] = number of stats in use; [20+i] = stat name;
    // [25+i] = "+current/cap" for i in 0..(count-1). Used to read the scroll's real
    // per-stat progress so the route matches what is actually meldable.
    private const int ActiveStatCountIdx = 19;
    private const int ActiveStatNameStart = 20;
    private const int ActiveStatValueStart = 25;
    private const int MaxActiveStats = 5;

    private static long _lastAddonDump;
    private static bool _dumpedValues;

    public static bool TryAttachOne(uint materiaItemId)
        => MateriaCatalog.TryResolve(materiaItemId, out var t, out var g) && TryAttachOne(materiaItemId, t, g);

    public static bool TryAttachOne(uint materiaItemId, MateriaType type, int grade)
    {
        try
        {
            var addon = GetVisibleAddon(AddonName);
            if (addon == null)
            {
                if (Environment.TickCount64 - _lastAddonDump > 2000)
                {
                    _lastAddonDump = Environment.TickCount64;
                    DumpOpenAddons();
                }
                return false;
            }

            if (addon->AtkValuesCount < MinValues)
            {
                if (!_dumpedValues) { _dumpedValues = true; DumpValues(addon); }
                DebugLog.Warn($"RelicSphereScroll has {addon->AtkValuesCount} AtkValues (< {MinValues}); its layout differs from expected. See the dump above.");
                return false;
            }

            var wantName = MateriaCatalog.MateriaName(type, grade);
            var found = FindOption(addon, wantName);
            if (found < 0)
            {
                if (!_dumpedValues) { _dumpedValues = true; DumpValues(addon); }
                DebugLog.Warn($"RelicSphereScroll: '{wantName}' not found among the infusion options (see dump).");
                return false;
            }

            var count = addon->AtkValues[CountStart + found].Int;
            var selectable = addon->AtkValues[HighlightStart + found].Int;
            if (count <= 0 || selectable == 0)
            {
                // Not the next valid grade for that stat, or none held: let the caller
                // try a different route materia. Verbose so it does not spam.
                DebugLog.Verbose($"RelicSphereScroll: '{wantName}' not infusable now (count={count}, selectable={selectable}).");
                return false;
            }

            // Select the option, then confirm the infusion. FireCallback is synchronous,
            // and we only ever fire on an option flagged selectable with count > 0, so
            // this cannot infuse the wrong or an invalid materia.
            FireSelect(addon, found);
            FireInt(addon, 2);
            DebugLog.Info($"RelicSphereScroll: infusing {wantName} (option {found}).");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Auto-infuse failed: {ex.Message}");
            return false;
        }
    }

    private static uint _scrollItemId;

    private static readonly InventoryType[] PlayerBags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    public static bool IsScrollOpen() => GetVisibleAddon(AddonName) != null;

    // Re-opens the infusion window by using the Sphere Scroll item (the game closes the
    // window after each infusion). Returns false if no Sphere Scroll is in the bags.
    public static bool TryOpenScroll()
    {
        try
        {
            var id = FindSphereScrollItemId();
            if (id == 0)
                return false;
            var am = ActionManager.Instance();
            if (am == null)
                return false;
            // Using the Sphere Scroll item opens its infusion window (matches how the
            // plugin uses other items elsewhere).
            am->UseAction(ActionType.Item, id, extraParam: 0xFFFF);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"RelicSphereScroll: could not re-open the scroll: {ex.Message}");
            return false;
        }
    }

    // Finds (and caches) the item id of the "Sphere Scroll: ..." in the player's bags.
    // The cache is validated against the live inventory: after the scroll is handed in
    // at the Novus turn-in (or when a second job's different scroll is farmed in the
    // same session), the old id would otherwise be reused forever and UseAction on an
    // item no longer held is a silent no-op -- the reopen loop then spins to timeout.
    private static uint FindSphereScrollItemId()
    {
        if (_scrollItemId != 0)
        {
            if (GameState.InventoryCount(_scrollItemId) > 0)
                return _scrollItemId;
            _scrollItemId = 0; // consumed or replaced; re-scan the bags
        }
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var bag in PlayerBags)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;
                if (sheet.GetRowOrDefault(s->ItemId) is { } row &&
                    row.Name.ExtractText().StartsWith("Sphere Scroll", StringComparison.OrdinalIgnoreCase))
                {
                    _scrollItemId = s->ItemId;
                    return _scrollItemId;
                }
            }
        }
        return 0;
    }

    // Clicks "Yes" on the infusion confirmation prompt (SelectYesno) if it is open.
    // Each infuse opens this prompt; confirming it actually consumes the materia.
    public static bool TryConfirmYesNo()
    {
        var addon = GetVisibleAddon("SelectYesno");
        if (addon == null)
            return false;
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0; // 0 = Yes, 1 = No
        addon->FireCallback(1, values, true);
        DebugLog.Verbose("RelicSphereScroll: clicked Yes on the infusion prompt.");
        return true;
    }

    // Infuses the first option that is held (count > 0) and selectable, regardless of
    // the planned route. Used as a fallback so materia you already hold still makes
    // progress even if the cheapest route did not pick that stat.
    public static bool TryInfuseHeldSelectable()
    {
        try
        {
            var addon = GetVisibleAddon(AddonName);
            if (addon == null || addon->AtkValuesCount < MinValues)
                return false;
            for (var i = 0; i < OptionCount; i++)
            {
                if (addon->AtkValues[CountStart + i].Int <= 0 || addon->AtkValues[HighlightStart + i].Int == 0)
                    continue;
                FireSelect(addon, i);
                FireInt(addon, 2);
                ref var nameV = ref addon->AtkValues[NameStart + i];
                var name = IsString(nameV.Type) ? nameV.String.ToString() : $"option {i}";
                DebugLog.Info($"RelicSphereScroll: infusing {name} (held + selectable, option {i}).");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Auto-infuse (held) failed: {ex.Message}");
            return false;
        }
    }

    private static int FindOption(AtkUnitBase* addon, string wantName)
    {
        for (var i = 0; i < OptionCount; i++)
        {
            ref var v = ref addon->AtkValues[NameStart + i];
            if (!IsString(v.Type))
                continue;
            var name = v.String.ToString();
            if (!string.IsNullOrEmpty(name) && string.Equals(name, wantName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static void FireSelect(AtkUnitBase* addon, int index)
    {
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        values[1].Type = AtkValueType.Int;
        values[1].Int = index;
        addon->FireCallback(2, values, true);
    }

    private static void FireInt(AtkUnitBase* addon, int command)
    {
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = command;
        addon->FireCallback(1, values, true);
    }

    private static AtkUnitBase* GetVisibleAddon(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name, 1);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->IsVisible ? addon : null;
    }

    // Public entry for the "Find infusion window" button: logs the open windows and,
    // if the infusion window is open, its AtkValues (to verify the layout).
    public static void LogOpenWindows()
    {
        DumpOpenAddons();
        var addon = GetVisibleAddon(AddonName);
        if (addon != null)
            DumpValues(addon);
    }

    // Reads the scroll's total infused count (AtkValue 10) and max (11) from the open
    // window, for completion and progress detection. False if not open.
    public static bool TryReadInfuseTotal(out int current, out int max)
    {
        current = 0;
        max = 0;
        var addon = GetVisibleAddon(AddonName);
        if (addon == null || addon->AtkValuesCount <= 11)
            return false;
        current = addon->AtkValues[10].Int;
        max = addon->AtkValues[11].Int;
        return max > 0;
    }

    // Reads the scroll's real per-stat progress from the open infusion window (the
    // active-stat summary block), so the route can continue each stat from its true
    // current grade instead of from a manually-entered guess. False if not open.
    public static bool TryReadProgress(out System.Collections.Generic.Dictionary<MateriaType, int> progress)
    {
        progress = new System.Collections.Generic.Dictionary<MateriaType, int>();
        try
        {
            var addon = GetVisibleAddon(AddonName);
            if (addon == null || addon->AtkValuesCount <= ActiveStatValueStart + MaxActiveStats)
                return false;

            var num = addon->AtkValues[ActiveStatCountIdx].Int;
            num = Math.Clamp(num, 0, MaxActiveStats);
            for (var i = 0; i < num; i++)
            {
                ref var nameV = ref addon->AtkValues[ActiveStatNameStart + i];
                ref var valV = ref addon->AtkValues[ActiveStatValueStart + i];
                if (!IsString(nameV.Type) || !IsString(valV.Type))
                    continue;
                if (TryStatToType(nameV.String.ToString(), out var type) &&
                    TryParseCurrent(valV.String.ToString(), out var cur))
                    progress[type] = cur;
            }
            return progress.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // Parses the current points from a "+current/cap" string (e.g. "+11/44" -> 11).
    private static bool TryParseCurrent(string s, out int cur)
    {
        cur = 0;
        if (string.IsNullOrEmpty(s))
            return false;
        s = s.Replace("+", string.Empty).Trim();
        var slash = s.IndexOf('/');
        if (slash <= 0)
            return false;
        return int.TryParse(s.Substring(0, slash), out cur);
    }

    private static bool TryStatToType(string statName, out MateriaType type)
    {
        foreach (var t in MateriaCatalog.AllTypes)
            if (string.Equals(MateriaCatalog.Stat(t), statName, StringComparison.OrdinalIgnoreCase))
            {
                type = t;
                return true;
            }
        type = default;
        return false;
    }

    private static bool IsString(AtkValueType type)
        => type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString;

    // Logs every visible non-HUD window name so the infusion window can be identified.
    private static void DumpOpenAddons()
    {
        try
        {
            var rapture = RaptureAtkUnitManager.Instance();
            if (rapture == null)
                return;
            var mgr = (AtkUnitManager*)rapture;
            var sb = new StringBuilder("Auto-meld: open windows (HUD hidden) = ");
            var entries = mgr->AllLoadedUnitsList.Entries;
            int count = mgr->AllLoadedUnitsList.Count;
            for (var i = 0; i < count && i < entries.Length; i++)
            {
                var u = entries[i].Value;
                if (u == null || !u->IsVisible)
                    continue;
                var name = u->NameString;
                if (string.IsNullOrEmpty(name) || name[0] == '_')
                    continue;
                sb.Append(name).Append(' ');
            }
            DebugLog.Warn(sb.ToString());
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Auto-meld: addon enumerate failed: {ex.Message}");
        }
    }

    // Logs the infusion addon's AtkValues (numbers and strings) so the option layout
    // can be verified against the assumed indices.
    private static void DumpValues(AtkUnitBase* addon)
    {
        try
        {
            var n = addon->AtkValuesCount;
            var sb = new StringBuilder($"RelicSphereScroll {n} AtkValues: ");
            for (var i = 0; i < n && i < 160; i++)
            {
                ref var v = ref addon->AtkValues[i];
                sb.Append('[').Append(i).Append(']');
                if (IsString(v.Type))
                    sb.Append('"').Append(v.String.ToString()).Append('"');
                else
                    sb.Append(v.Int);
                sb.Append(' ');
            }
            DebugLog.Warn(sb.ToString());
        }
        catch { /* diagnostic only */ }
    }
}
