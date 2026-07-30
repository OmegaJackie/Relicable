using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Relicable.Diagnostics;

namespace Relicable.Steps;

// Keeps the gear set you are actually wearing pointed at the CURRENT relic.
//
// Every stage of the line replaces the weapon with a new item id: the Zenith is consumed to make
// the Atma, the Atma to make the Animus, and so on. A gear set records items by id, so the moment
// an upgrade lands, the set the player uses still names a weapon that no longer exists -- the next
// "/gearset change" comes up with the main hand unfilled, and on a Paladin the Holy Shield goes the
// same way. That is invisible until they switch jobs and find themselves half-equipped.
//
// So after an upgrade this rewrites the active gear set with what is on the character now. It is
// deliberately timid about it, because a gear set is the player's data and UpdateGearset writes
// EVERY slot:
//
//   * the set must be the one currently selected, and be for the job we are on;
//   * the main hand must actually hold a known relic;
//   * that relic must differ from what the set records (otherwise there is nothing to do);
//   * and every slot OTHER than the two hands must already match the set. That last check is what
//     makes this safe -- it means the only thing the write can change is the weapon. Wearing
//     anything else off-set (a glamour test, a swapped accessory) simply skips the sync rather
//     than silently baking it in.
//
// Off by one setting (Configuration.SyncGearsetToLatestRelic) for anyone who would rather manage
// their own sets.
internal static unsafe class RelicGearsetSync
{
    private const long PollMs = 2000;

    // Item ids carry +1,000,000 in a gear set when the entry is the HQ version; the inventory keeps
    // HQ as a separate flag. Normalise both sides before comparing.
    private const uint HqOffset = 1_000_000;

    private static long _lastPoll;

    // The last write we attempted. A successful one stops repeating on its own (the set then names
    // the weapon, and Sync returns early), so this exists only to stop a write the game DECLINES
    // from being retried every two seconds forever. Keyed by character as well as gear set, so
    // logging into an alt cannot inherit the latch.
    private static (ulong Owner, int Gearset, uint Weapon) _attempted = (0, -1, 0);

    public static void Tick(Configuration config)
    {
        if (!config.SyncGearsetToLatestRelic)
            return;
        var now = Environment.TickCount64;
        if (now - _lastPoll < PollMs)
            return;
        _lastPoll = now;

        try { Sync(); }
        catch (Exception ex) { DebugLog.Verbose($"Gear set sync skipped: {ex.Message}"); }
    }

    private static void Sync()
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } player)
            return;

        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return;
        var index = module->CurrentGearsetIndex;
        if (index < 0 || !module->IsValidGearset(index))
            return;
        var entry = module->GetGearset(index);
        if (entry == null)
            return;

        // Only ever touch a set for the job we are standing in. A stale CurrentGearsetIndex after a
        // job change would otherwise write this job's gear into another job's set.
        if (entry->ClassJob != player.ClassJob.RowId)
            return;

        var worn = GameState.EquippedWeaponItemId(0);
        if (worn == 0 || !GameState.IsRelicWeaponId(worn))
            return;

        var items = entry->Items;
        if (items.Length < 2)
            return;
        if (Normalise(items[0].ItemId) == worn)
            return; // the set already names this weapon

        if (!OnlyHandsDiffer(items))
            return;

        var key = (GameState.OwnerContentId(), index, worn);
        if (_attempted == key)
            return; // already attempted, and the game did not take it
        _attempted = key;

        module->UpdateGearset(index);
        DebugLog.Info($"Gear set '{entry->NameString}' still named the previous relic; updated it to " +
                      $"'{GameState.ItemName(worn)}' so switching to it keeps equipping the current one.");
    }

    // True when the character matches the gear set in every slot except the two weapon slots, i.e.
    // rewriting the set can only change the weapon. Slots the set leaves empty are ignored: an
    // unfilled entry is not a mismatch, it is just a slot the set does not manage.
    private static bool OnlyHandsDiffer(Span<RaptureGearsetModule.GearsetItem> items)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        var eq = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (eq == null)
            return false;

        for (var i = 2; i < items.Length && i < eq->Size; i++)
        {
            var setId = Normalise(items[i].ItemId);
            if (setId == 0)
                continue;
            var slot = eq->GetInventorySlot(i);
            if (slot == null || slot->ItemId != setId)
                return false;
        }
        return true;
    }

    private static uint Normalise(uint itemId) => itemId >= HqOffset ? itemId - HqOffset : itemId;
}
