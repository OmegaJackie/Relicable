using System;
using Dalamud.Memory;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.GenericHelpers;

namespace Relicable.Steps.Interaction;

// Levemete leve UI, ported from Battlevest (Utils.RecursivelyAcceptLeves +
// Battlevest's own GuildLeve addon master -- ECommons has never shipped one).
// The real flow is:
//   interact levemete -> a SelectString category menu appears
//   select the leve category -> the GuildLeve board lists offered leves
//   select the target leve -> JournalDetail.AcceptMap accepts it
//
// The GuildLeve board is driven directly from its AtkValues, with the layout
// verified against Battlevest (current, 2026): entry count at [25], the selected
// leve's name at [1233], entry i's name at [626 + i*2] (level string at 627 + i*2),
// and selection fired as Callback(13, entryIndex, leveRowId).
//
// Driven per-tick by StartLeveExecutor. Each method performs a single UI action, so
// the natural one-tick delay lets the addons settle between steps (Battlevest
// sequences the same actions through its ECommons TaskManager instead).
//
// NOTE: this is offline-untestable (it drives live game addons). Verify in-game.
internal static unsafe class LeveBoard
{
    private const string CategoryAddon = "SelectString";
    private const string BoardAddon = "GuildLeve";

    // AtkValue layout of the GuildLeve board (from Battlevest's GuildLeve master).
    private const int NumEntriesIndex = 25;
    private const int SelectedLeveIndex = 1233;
    private const int EntryNameStart = 626;   // name at 626 + i*2
    private const int SelectCallbackId = 13;

    // Outcome of one AcceptTarget tick, so the executor can distinguish "the accept
    // was fired and needs a server round-trip to register" from "this levemete does
    // not offer the target" (reroll by closing and re-opening the board).
    public enum AcceptResult { NotReady, Selecting, Fired, NotOffered }

    public static bool CategoryOpen()
        => TryGetAddonMaster<AddonMaster.SelectString>(CategoryAddon, out var m) && m.IsAddonReady;

    // Choose the SelectString entry that opens the RIGHT leve board. A levemete lists several
    // categories (Battlecraft, Tradecraft, Grand Company, ...), and the relic book leves are
    // Grand Company leves -- so picking the first "Levequests" entry opened the regional
    // battlecraft board and the GC target was never offered. Prefer, in order: the target leve's
    // own category (from the sheet), then the Grand Company category, then any generic leve
    // category. `preferredCategory` is the target's LeveAssignmentType name (may be empty).
    public static bool SelectCategory(string preferredCategory)
    {
        if (!TryGetAddonMaster<AddonMaster.SelectString>(CategoryAddon, out var m) || !m.IsAddonReady)
            return false;

        // 1) The target leve's own category. Match on the category "key" (the name minus the
        //    "Leves"/"Levequests" suffix) so "Grand Company Leves" (sheet) matches a menu entry
        //    worded "Grand Company Levequests".
        var wantKey = CategoryKey(preferredCategory);
        if (wantKey.Length > 0)
            foreach (var e in m.Entries)
            {
                var k = CategoryKey(e.Text ?? string.Empty);
                if (k.Length > 0 && (k == wantKey || k.Contains(wantKey) || wantKey.Contains(k)))
                {
                    Diagnostics.DebugLog.Verbose($"Leve category: matched '{e.Text}' to target category '{preferredCategory}'");
                    e.Select();
                    return true;
                }
            }

        // 2) The relic book battle leves are Grand Company leves; prefer that explicitly when the
        //    sheet lookup missed (it is a distinct category the generic match below skips over).
        foreach (var e in m.Entries)
            if ((e.Text ?? string.Empty).Contains("Grand Company", StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.DebugLog.Info($"Leve category: category '{preferredCategory}' not matched by wording; picked Grand Company entry '{e.Text}'");
                e.Select();
                return true;
            }

        // 3) Fallback: any generic leve category (locale-safe by matching "Leves"/"Levequests").
        //    Log the menu so a wrong pick (target under a differently-worded tab) is visible.
        foreach (var e in m.Entries)
        {
            var t = e.Text ?? string.Empty;
            if (t.Contains("Leves", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Levequests", StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.DebugLog.Warn($"Leve category: no match for '{preferredCategory}' or Grand Company; " +
                    $"fell back to generic '{t}'. Menu offered: [{EntryList(m)}]");
                e.Select();
                return true;
            }
        }

        Diagnostics.DebugLog.Warn($"Leve category: no leve category entry found for '{preferredCategory}'. Menu offered: [{EntryList(m)}]");
        return false;
    }

    private static string EntryList(AddonMaster.SelectString m)
        => string.Join(" | ", System.Linq.Enumerable.Select(m.Entries, e => e.Text ?? string.Empty));

    // A category's distinguishing key: its name with the "Leves"/"Levequests" suffix and spacing
    // stripped, lowercased, so the sheet name and the menu wording compare equal (e.g. "Grand
    // Company Leves" and "Grand Company Levequests" both key to "grand company").
    private static string CategoryKey(string s)
        => (s ?? string.Empty)
            .Replace("Levequests", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Leves", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();

    private static AtkUnitBase* Board()
    {
        if (TryGetAddonByName<AtkUnitBase>(BoardAddon, out var addon) && IsAddonReady(addon))
            return addon;
        return null;
    }

    public static bool BoardOpen() => Board() != null;

    // The names of every leve currently offered on the board, in order. Used to pick a filler
    // leve to cycle the battle-leve rotation when the target is not offered (battle leves are not
    // all shown at once; completing a different one in the category rotates the offered set).
    public static System.Collections.Generic.List<string> OfferedLeveNames()
    {
        var result = new System.Collections.Generic.List<string>();
        var addon = Board();
        if (addon == null)
            return result;

        var count = (int)addon->AtkValues[NumEntriesIndex].UInt;
        for (var i = 0; i < count; i++)
        {
            var entryName = ReadString(addon, EntryNameStart + i * 2);
            if (string.IsNullOrEmpty(entryName))
                break; // past the populated entries
            result.Add(entryName);
        }
        return result;
    }

    // Reads a string AtkValue (leve names are plain text; decoded as SeString for
    // safety, matching Battlevest). Empty when the value is not a string.
    private static string ReadString(AtkUnitBase* addon, int index)
    {
        if (index >= addon->AtkValuesCount)
            return string.Empty;
        ref var v = ref addon->AtkValues[index];
        if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString))
            return string.Empty;
        var ptr = (nint)v.String.Value;
        return ptr == 0 ? string.Empty : MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue;
    }

    // Accept the specific target leve from the GuildLeve board. Per-tick: if the target
    // is not the current selection, select it (Selecting; JournalDetail populates on the
    // next tick), then AcceptMap once it is selected (Fired -- the caller must allow a
    // server round-trip before the accept shows in QuestManager). NotOffered when the
    // board does not list the target, so the caller can close and reroll.
    public static AcceptResult AcceptTarget(uint leveId)
    {
        var addon = Board();
        if (addon == null)
            return AcceptResult.NotReady;

        var name = Data.Sheets.LeveName(leveId);
        if (string.IsNullOrEmpty(name))
            return AcceptResult.NotReady;

        var count = (int)addon->AtkValues[NumEntriesIndex].UInt;
        for (var i = 0; i < count; i++)
        {
            var entryName = ReadString(addon, EntryNameStart + i * 2);
            if (string.IsNullOrEmpty(entryName))
                break; // past the populated entries
            if (!string.Equals(entryName, name, StringComparison.Ordinal))
                continue;

            // Not the current selection yet: select it (Battlevest's callback shape:
            // 13, entry index, Leve row id) and let JournalDetail populate next tick.
            if (!string.Equals(ReadString(addon, SelectedLeveIndex), name, StringComparison.Ordinal))
            {
                Callback.Fire(addon, true, SelectCallbackId, i, (int)leveId);
                return AcceptResult.Selecting;
            }

            if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var jd) && jd.IsAddonReady)
            {
                jd.AcceptMap();
                return AcceptResult.Fired;
            }
            return AcceptResult.Selecting;
        }

        Diagnostics.DebugLog.Warn($"Leve: target '{name}' ({leveId}) is not offered at this levemete");
        return AcceptResult.NotOffered;
    }

    public static void Close()
    {
        if (TryGetAddonByName<AtkUnitBase>(BoardAddon, out var addon) && IsAddonReady(addon))
            Callback.Fire(addon, true, -1);
    }

    private const string JournalAddon = "JournalDetail";
    // The leve-ACCEPT confirmation window ("New Levequest!") and a stray Yes/No. Neither is a list
    // menu closed by the -1 cancel, yet both keep the character IN the NPC event and so block the
    // queued leve's travel -- the "took the leve but the menu never exits, character stuck" stall.
    private const string JournalResultAddon = "JournalResult";
    private const string YesNoAddon = "SelectYesno";

    // The levemete leve menus that keep us in the NPC event -- which BLOCKS character movement -- so
    // any one left open after an accept stalls the queued leve's travel: the GuildLeve board, the
    // JournalDetail (leve detail) popup, and the levemete's own SelectString category menu (closing
    // it ends the conversation). Order matters for CloseAll: innermost popup first, then the board,
    // then the category. The accept-confirmation popup (JournalResultAddon) is NOT in this list --
    // it closes via its Complete button, not the -1 cancel -- but CloseAll handles it separately.
    private static readonly string[] LeveMenus = { JournalAddon, BoardAddon, CategoryAddon };

    // True while any menu that keeps us in the levemete event is still open (so the caller keeps
    // closing before running the leve). Covers the three list menus PLUS the accept-confirmation
    // popup and a stray Yes/No -- both missed by a list-only check, and the leftover cause of the
    // "menu never exits" stall (the executor would otherwise proceed while still stuck in the event).
    public static bool AnyLeveMenuOpen()
    {
        foreach (var name in LeveMenus)
            if (TryGetAddonByName<AtkUnitBase>(name, out var a) && IsAddonReady(a))
                return true;
        if (TryGetAddonByName<AtkUnitBase>(JournalResultAddon, out var jr) && IsAddonReady(jr))
            return true;
        if (TryGetAddonByName<AtkUnitBase>(YesNoAddon, out var yn) && IsAddonReady(yn))
            return true;
        return false;
    }

    // Dismiss everything that can hold the levemete event open after an accept. Best-effort and
    // idempotent, so the caller retries until AnyLeveMenuOpen() is false. The accept-confirmation
    // popup is clicked shut via its Complete button (the -1 cancel does NOT close it -- the missing
    // piece behind "took the leve but the menu never exits"); a stray Yes/No is confirmed; the list
    // menus close on the -1 cancel callback.
    public static void CloseAll()
    {
        if (TryGetAddonMaster<AddonMaster.JournalResult>(JournalResultAddon, out var jr) && jr.IsAddonReady)
            jr.Complete();
        DialogueMenu.ConfirmYes();

        foreach (var name in LeveMenus)
            if (TryGetAddonByName<AtkUnitBase>(name, out var a) && IsAddonReady(a))
                Callback.Fire(a, true, -1);
    }

    // Drive the "Collect Reward." turn-in that CREDITS a completed leve to the RelicNote book. The book
    // leve-slot bit is set on COLLECTION at the levemete, NOT on clearing the objective in the field
    // (verified: relic guides say leves do not count until you collect the reward, which is why leves
    // can be "banked" uncollected across books). So after a leve's objective is done the reward must be
    // collected or the slot never credits -- the reported "leves not turning in". Best-effort and
    // idempotent: the caller interacts the levemete, then calls this each tick while a menu is open,
    // until the slot credits. In priority order it finalizes an open reward window, confirms a Yes/No,
    // picks "Collect Reward." from the levemete SelectString, or picks the target leve from a per-leve
    // picker. Returns true if it drove a menu this tick (so the caller does not re-interact over it).
    //
    // SEAM (verify in-game): the exact addon chain after "Collect Reward." is not offline-confirmable --
    // it may go straight to the JournalResult reward window or through a SelectString/SelectIconString
    // picker of the pending leves first. This handles both; the caller logs the open menus so a stall
    // reveals the real chain.
    public static bool CollectReward(string? targetLeveName)
    {
        // 1) The reward window: finalize it -- this is what sets the RelicNote leve-slot bit.
        if (TryGetAddonMaster<AddonMaster.JournalResult>(JournalResultAddon, out var jr) && jr.IsAddonReady)
        {
            jr.Complete();
            return true;
        }
        // 2) A confirmation Yes/No that can appear in the collection flow.
        if (DialogueMenu.IsOpen(YesNoAddon) && DialogueMenu.ConfirmYes())
            return true;
        // 3) The levemete's main SelectString: open the collection with "Collect Reward." (the entry is
        //    present only while there are completed, unclaimed leves). If that entry is absent, this
        //    SelectString is a per-leve picker inside the collection flow -> pick the completed target.
        if (TryGetAddonMaster<AddonMaster.SelectString>(CategoryAddon, out var ss) && ss.IsAddonReady)
        {
            if (DialogueMenu.SelectByTextSafe(CategoryAddon, "Collect Reward"))
                return true;
            if (!string.IsNullOrEmpty(targetLeveName) && DialogueMenu.SelectByTextSafe(CategoryAddon, targetLeveName!))
                return true;
        }
        // 4) A SelectIconString picker of the pending leves: pick the completed target.
        if (!string.IsNullOrEmpty(targetLeveName)
            && TryGetAddonMaster<AddonMaster.SelectIconString>("SelectIconString", out var ic) && ic.IsAddonReady
            && DialogueMenu.SelectByTextSafe("SelectIconString", targetLeveName!))
            return true;

        return false;
    }
}
