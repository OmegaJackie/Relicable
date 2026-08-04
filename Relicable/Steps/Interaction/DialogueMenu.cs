using System.Collections.Generic;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.GenericHelpers;

namespace Relicable.Steps.Interaction;

// Helper for selecting an entry in the list-style dialogue addons that gate
// quest/leve/relic flows: SelectString (text list) and SelectIconString (icon +
// text list, used by levemetes and some relic NPCs). TextAdvance handles plain
// Talk and most Yes/No prompts; these list menus require an explicit choice,
// which is what this provides.
//
// Verified shape against current FFXIVClientStructs: addons are AtkUnitBase, and
// a list selection is a FireCallback with a single Int value equal to the entry
// index.
internal static unsafe class DialogueMenu
{
    public static bool IsOpen(string addonName) => GetVisible(addonName) != null;

    // Click "Yes" on a SelectYesno confirmation (e.g. "Open this treasure coffer?").
    // TextAdvance does not auto-confirm every Yes/No prompt, so callers that need a
    // specific confirmation use this. Returns false if no SelectYesno is open.
    //
    // SelectYesNo requires the callback fired with updateState = true; the plain
    // list-style FireCallback used for SelectString does NOT register the Yes click
    // (the box just stays open). 0 = Yes.
    public static bool ConfirmYes()
    {
        var addon = GetVisible("SelectYesno");
        if (addon == null)
            return false;
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0; // Yes
        addon->FireCallback(1, values, true);
        return true;
    }

    // Click "Hand Over" on the quest item-delivery window (the "Request" addon), the window a quest
    // turn-in opens to take items off you. TextAdvance carries the surrounding dialogue but does not
    // press this button, so before 1.5.8.9 the window simply sat there: the conversation ended, the
    // quest never advanced, and the run reported a turn-in that had not happened (see
    // StepData.AdvancesQuestFromSequence). Confirmed live on the sequence-14 hand-over -- the failure
    // log's captured addon chain read exactly "menus open -> Request;".
    //
    // Returns true only when the click actually fired. The enabled check is the safety property that
    // makes this callable on a timer: the game leaves Hand Over DISABLED until the requested items are
    // in the window's slots, so a turn-in we cannot satisfy (the item is not in the bags, or is still
    // equipped) clicks nothing rather than handing over the wrong thing. The button is looked up
    // through ECommons' verified AddonMaster.Request (component id 14) and null-checked first --
    // its own IsHandOverEnabled dereferences the button without one.
    public static bool HandOverRequest()
    {
        var addon = GetVisible("Request");
        if (addon == null)
            return false;
        var master = new AddonMaster.Request(addon);
        if (master.HandOverButton == null || !master.IsHandOverEnabled)
            return false;
        master.HandOver();
        return true;
    }

    // Select the entry at zero-based index. Returns false if the addon is not
    // currently open, so callers can poll until it is.
    public static bool Select(string addonName, int index)
    {
        var addon = GetVisible(addonName);
        if (addon == null)
            return false;

        FireSelect(addon, index);
        return true;
    }

    // Select the entry whose label contains needle (case-insensitive). The entry
    // labels are read from the addon's AtkValues string entries, in order, and the
    // ordinal of the match is used as the callback index. Returns false if the
    // addon is not open or no entry matched.
    //
    // Caveat: this assumes the Nth string AtkValue corresponds to callback index N,
    // which holds for SelectString and the leve SelectIconString lists. If a future
    // addon interleaves non-entry strings, prefer Select(index) with a known index.
    public static bool SelectByText(string addonName, string needle)
    {
        var addon = GetVisible(addonName);
        if (addon == null || string.IsNullOrEmpty(needle))
            return false;

        var ordinal = 0;
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            ref var value = ref addon->AtkValues[i];
            if (!IsString(value.Type))
                continue;

            var label = value.String.ToString();
            if (!string.IsNullOrEmpty(label))
            {
                if (label.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
                {
                    FireSelect(addon, ordinal);
                    return true;
                }
                ordinal++;
            }
        }
        return false;
    }

    // Select the entry whose label contains needle (case-insensitive) using ECommons' AddonMaster,
    // which reads the real list entries and fires the correct callback ENTRY INDEX. This avoids the
    // raw string-ordinal hazard of SelectByText: a leading prompt line (or any non-entry string
    // AtkValue) shifts the ordinal, and a SelectIconString's callback index does not map to the
    // string ordinal at all -- both documented misfires that BuyRelicBookExecutor was migrated off.
    // As a bonus AddonMaster.Entries excludes the prompt, so a needle can never match a header/prompt
    // line. Falls back to the ordinal SelectByText for any addon AddonMaster does not model or when it
    // is not ready. Returns true when an entry was selected.
    public static bool SelectByTextSafe(string addonName, string needle)
    {
        if (string.IsNullOrEmpty(needle))
            return false;

        if (addonName == "SelectString"
            && TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var s) && s.IsAddonReady)
        {
            foreach (var e in s.Entries)
                if ((e.Text ?? string.Empty).Contains(needle, System.StringComparison.OrdinalIgnoreCase))
                {
                    e.Select();
                    return true;
                }
            return false;
        }

        if (addonName == "SelectIconString"
            && TryGetAddonMaster<AddonMaster.SelectIconString>("SelectIconString", out var ic) && ic.IsAddonReady)
        {
            foreach (var e in ic.Entries)
                if ((e.Text ?? string.Empty).Contains(needle, System.StringComparison.OrdinalIgnoreCase))
                {
                    e.Select();
                    return true;
                }
            return false;
        }

        // The AddonMaster path did not match, so a SelectString/SelectIconString is open but not
        // ready yet. Report failure so the caller retries next tick: the ordinal SelectByText
        // fallback is exactly the misfire this method exists to avoid (a SelectIconString's
        // callback index does not map to the string ordinal at all).
        if (addonName is "SelectString" or "SelectIconString")
            return false;

        return SelectByText(addonName, needle);
    }

    // The addon's real SELECTABLE entry labels, in menu order. Read via AddonMaster, whose Entries
    // exclude the prompt/header line -- unlike the raw AtkValue string scan in ListEntries, where the
    // prompt is just another string and shifts every ordinal after it. Use this whenever the caller
    // needs to reason about the choices themselves (rank them, walk them in turn) rather than match a
    // single known word; pair it with SelectByTextSafe, which resolves a label back to the right
    // callback index. Empty when the addon is not open or not ready yet.
    public static List<string> EntryTexts(string addonName)
    {
        var result = new List<string>();
        if (addonName == "SelectString"
            && TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var s) && s.IsAddonReady)
        {
            foreach (var e in s.Entries)
                if (!string.IsNullOrWhiteSpace(e.Text))
                    result.Add(e.Text);
            return result;
        }
        if (addonName == "SelectIconString"
            && TryGetAddonMaster<AddonMaster.SelectIconString>("SelectIconString", out var ic) && ic.IsAddonReady)
        {
            foreach (var e in ic.Entries)
                if (!string.IsNullOrWhiteSpace(e.Text))
                    result.Add(e.Text);
            return result;
        }
        return result;
    }

    private static void FireSelect(AtkUnitBase* addon, int index)
    {
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = index;
        // updateState = true: some list addons (e.g. Remon's "select a weapon" SelectString) ignore
        // a selection fired without the UI-state update and just stay open. Firing with true matches
        // a real mouse click and registers across all list addons, the same as SelectYesno's Yes.
        addon->FireCallback(1, values, true);
    }

    // Fire the "close" callback on a list addon (e.g. GuildLeve), matching
    // Battlevest's Callback.Fire(addon, close: true, -1).
    public static void FireClose(string addonName)
    {
        var addon = GetVisible(addonName);
        if (addon == null)
            return;
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = -1;
        addon->FireCallback(1, values, true);
    }

    private static bool IsString(AtkValueType type)
        => type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString;

    // List the menu's string entries with their selection ordinals, so a caller
    // can see what is offered (e.g. which leves a levemete is currently showing).
    public static List<(int Index, string Label)> ListEntries(string addonName)
    {
        var result = new List<(int, string)>();
        var addon = GetVisible(addonName);
        if (addon == null)
            return result;

        var ordinal = 0;
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            ref var value = ref addon->AtkValues[i];
            if (!IsString(value.Type))
                continue;
            var label = value.String.ToString();
            if (!string.IsNullOrEmpty(label))
            {
                result.Add((ordinal, label));
                ordinal++;
            }
        }
        return result;
    }

    // Candidate list/shop/dialog addons an NPC interaction can open. Used to report which
    // menu is actually up when a selection is not being picked up, and to avoid
    // re-interacting (which would toggle an open menu shut).
    private static readonly string[] MenuAddons =
    {
        "SelectString", "SelectIconString", "SelectYesno", "SelectOk",
        "ShopExchangeCurrency", "ShopExchangeItem", "Shop", "InclusionShop",
        "ShopCardDialog", "FreeShop", "Talk", "JournalResult", "Request",
    };

    // True if any known menu/dialog/shop addon is currently visible.
    public static bool AnyOpen()
    {
        foreach (var n in MenuAddons)
            if (IsOpen(n))
                return true;
        return false;
    }

    // A compact signature of the currently-open menus (addon names + list-entry labels),
    // so a caller can log each DISTINCT menu once as an NPC flow advances through submenus,
    // instead of only logging the first menu. Empty when nothing is open.
    public static string OpenSignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var n in MenuAddons)
        {
            if (!IsOpen(n))
                continue;
            sb.Append(n).Append(';');
            if (n is "SelectString" or "SelectIconString")
                foreach (var e in ListEntries(n))
                    sb.Append(e.Label).Append('|');
        }
        return sb.ToString();
    }

    // Log which menu addons are currently visible, with list entries for the selection
    // lists, to identify the addon and labels an NPC uses when a pick is not registering.
    public static void LogOpenMenus(string context)
    {
        var any = false;
        foreach (var n in MenuAddons)
        {
            if (!IsOpen(n))
                continue;
            any = true;
            if (n is "SelectString" or "SelectIconString")
            {
                var entries = ListEntries(n);
                var joined = entries.Count > 0
                    ? string.Join(" | ", entries.ConvertAll(e => e.Index + ":" + e.Label))
                    : "(no string entries)";
                Plugin.Log.Information($"Relicable: [{context}] '{n}' -> {joined}");
            }
            else
            {
                Plugin.Log.Information($"Relicable: [{context}] '{n}' open");
            }
        }
        if (!any)
            Plugin.Log.Information($"Relicable: [{context}] no known menu addon visible");
    }

    private static AtkUnitBase* GetVisible(string addonName)
    {
        // GetAddonByName returns a readonly AtkUnitBasePtr wrapper; take its raw
        // Address to reach the FFXIVClientStructs AtkUnitBase.
        var ptr = Plugin.GameGui.GetAddonByName(addonName, 1);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->IsVisible ? addon : null;
    }
}
