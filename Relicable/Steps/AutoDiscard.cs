using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using ECommons;                     // ContainsAny / GetText helpers (GenericHelpers)
using ECommons.EzSharedDataManager; // EzSharedData.TryGet ("YesAlready.StopRequests")
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Diagnostics;
using static ECommons.GenericHelpers; // TryGetAddonMaster
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace Relicable.Steps;

// Silent inventory cleanup. A long unattended relic grind fills the bags with mob drops, and a
// full inventory stops loot dead, so this deletes the clutter as it accumulates -- with NO
// confirmation dialog and nothing for the player to answer.
//
// HOW IT DISCARDS, and why there is no prompt: the item is dropped through
// InventoryManager.DiscardItem(InventoryType, slot), the game's own discard call. The prompt
// people associate with discarding belongs to the UI path (AgentInventoryContext.DiscardItem,
// which shows a SelectYesno and then calls into this same function), so going straight to the
// InventoryManager never raises one. TryConfirmDiscard below is only a safety net: if some item
// class does put a confirm up, it is answered automatically -- and it will ONLY answer a prompt
// that names the exact item we just discarded, inside a few seconds of issuing it, so it can
// never say Yes to an unrelated question. YesAlready is asked to stand down for that window, the
// same handshake LeveReturn uses, so it cannot race us and click No.
//
// DISCARDING IS PERMANENT, so the safety rules are the bulk of this file:
//   * off by default, and (by default) inert unless the automation is actually running;
//   * only the four player bags -- never the armoury, key items, crystals or currency, none of
//     which are even scanned;
//   * a hard rule set nothing can override (below), which alone excludes every relic material;
//   * a protected id set seeded from the loaded objectives, so any item the relic line counts is
//     safe automatically -- there is no hand-maintained list to fall out of date;
//   * the user's own never-discard list, which wins over everything including the discard list.
//
// The configuration window renders Preview() -- exactly what would be deleted, right now -- so
// the consequence of switching this on is visible before it is switched on.
internal static unsafe class AutoDiscard
{
    // One item per interval. Slow enough that a mis-set filter cannot empty a bag before it is
    // noticed, fast enough to keep up with a grind (a bag's worth in under a minute).
    private const long DiscardIntervalMs = 700;
    // How long after issuing a discard we will answer a confirm that names that item.
    private const long ConfirmWindowMs = 2500;
    // A discard the game refuses leaves the item in place; after this many attempts on the same
    // item id, stop trying for the session rather than spinning on it forever.
    private const int MaxAttemptsPerItem = 3;

    private const string YesAlreadyStopKey = "YesAlready.StopRequests";

    // The four player bags. Deliberately ONLY these: the armoury (gear), key items, crystals and
    // currency live in their own containers and are therefore unreachable from here by
    // construction, not by a check that could be got wrong.
    private static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    private static Configuration? _config;
    private static Func<bool>? _automationRunning;
    private static Func<IEnumerable<uint>>? _protectedSource;

    // Item ids the relic line itself uses. Resolved lazily (the Item sheet and the data catalogues
    // are not necessarily ready at plugin construction) and latched only once non-empty, so an
    // early call cannot freeze in an empty set -- the same latch trap MateriaCatalog documents.
    // Both lazy resolvers below are asked from inside a per-slot loop, so an UNRESOLVED one must be
    // rate-limited: without this, "not ready yet" would re-scan every objective (and the whole
    // materia catalogue) once per bag slot, every frame the configuration window is open.
    private const long ResolveRetryMs = 2000;
    private static long _protectedRetryAt;
    private static long _materiaRetryAt;
    private static HashSet<uint>? _protected;
    // The game's own Materia UI category, resolved from a materia id we already know rather than a
    // hardcoded row or an English name, so EVERY materia is protected -- not just the grades the
    // Novus planner happens to enumerate.
    private static uint _materiaCategory;
    private static bool _materiaCategoryResolved;

    private static long _nextDiscardAt;
    private static long _confirmUntil;
    private static string _confirmItemName = string.Empty;
    private static bool _suppressed;

    // Refused-discard tracking, so a rejected item is retired instead of retried forever. Keyed on
    // the SLOT as well as the item, and that is load-bearing: several stacks of one drop are
    // normal, so counting strikes per item id alone would read three successful discards of three
    // Boar Hide stacks as three failures and retire the item with one stack still in the bag. The
    // same (bag, slot, item) coming back round is the only thing that actually means "refused" --
    // a successful discard empties that slot.
    private static InventoryType _lastAttemptBag;
    private static ushort _lastAttemptSlot;
    private static uint _lastAttemptItemId;
    private static int _lastAttemptCount;
    private static readonly HashSet<uint> Refused = new();

    // Surfaced in the configuration window so the feature is never silently doing nothing.
    public static int DiscardedThisSession { get; private set; }
    public static string LastAction { get; private set; } = string.Empty;

    // Called once at plugin start. protectedSource is evaluated lazily (see _protected).
    public static void Configure(Configuration config, Func<bool> automationRunning,
        Func<IEnumerable<uint>> protectedSource)
    {
        _config = config;
        _automationRunning = automationRunning;
        _protectedSource = protectedSource;
    }

    // Driven every framework frame from Plugin.OnUpdate, like LeveReturn: it is not tied to the
    // controller's state machine, it just self-gates.
    public static void Tick()
    {
        var cfg = _config;
        if (cfg is null || !cfg.AutoDiscardDrops)
        {
            if (_suppressed)
                Release();
            return;
        }

        var now = Environment.TickCount64;

        // A discard we issued may have raised a confirm. Answer it, holding YesAlready off ONLY
        // while a prompt is actually up inside that window -- a blanket suppression across a whole
        // discard sweep would leave YesAlready standing down through unrelated parts of the run.
        if (now <= _confirmUntil && Interaction.DialogueMenu.IsOpen("SelectYesno"))
        {
            Suppress();
            if (TryConfirmDiscard())
            {
                _confirmUntil = 0;
                Release();
            }
        }
        else if (_suppressed)
        {
            Release();
        }

        if (cfg.AutoDiscardOnlyWhileRunning && _automationRunning?.Invoke() != true)
            return;
        if (now < _nextDiscardAt)
            return;

        // Never act mid-conversation, mid-zone, dead, or with any menu up. The menu gate also
        // covers our own confirm above (a SelectYesno makes AnyOpen true), so a pending prompt is
        // always answered before another discard is issued.
        if (!Interaction.EventConditions.Free)
            return;
        if (Plugin.Condition[ConditionFlag.Unconscious])
            return;
        if (Interaction.DialogueMenu.AnyOpen())
            return;

        if (!TryFindDiscardable(cfg, out var bag, out var slot, out var itemId, out var quantity))
            return;

        _nextDiscardAt = now + DiscardIntervalMs;

        // Refusal guard: the SAME slot still holding the SAME item means the previous discard did
        // not take (the game declined it). Retire it for the session rather than looping.
        if (itemId == _lastAttemptItemId && bag == _lastAttemptBag && slot == _lastAttemptSlot)
        {
            // The count below is optimistic (we cannot know a discard took until the slot is next
            // scanned), so a repeat is also the proof that the last increment was wrong: undo it,
            // and the reported total stays a count of items actually gone.
            if (DiscardedThisSession > 0)
                DiscardedThisSession--;
            if (++_lastAttemptCount >= MaxAttemptsPerItem)
            {
                Refused.Add(itemId);
                LastAction = $"Could not discard {GameState.ItemName(itemId)}; skipping it for this session.";
                DebugLog.Warn("Auto-discard: " + LastAction);
                _lastAttemptItemId = 0;
                _lastAttemptCount = 0;
                return;
            }
        }
        else
        {
            _lastAttemptBag = bag;
            _lastAttemptSlot = slot;
            _lastAttemptItemId = itemId;
            _lastAttemptCount = 1;
        }

        var im = InventoryManager.Instance();
        if (im == null)
            return;

        var name = GameState.ItemName(itemId);
        _confirmItemName = name;
        _confirmUntil = now + ConfirmWindowMs;

        var rc = im->DiscardItem(bag, slot);
        DiscardedThisSession++;
        LastAction = $"Discarded {quantity}x {name}";
        DebugLog.Info($"Auto-discard: {quantity}x {name} (item {itemId}) from {bag} slot {slot} [result {rc}]");
    }

    // Answer a confirm that names the item we just discarded. Text-matched and time-boxed on
    // purpose: an unconditional Yes here would agree to whatever else happened to be asking.
    private static bool TryConfirmDiscard()
    {
        if (string.IsNullOrEmpty(_confirmItemName))
            return false;
        if (!TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var m) || !m.IsAddonReady)
            return false;
        if (!(m.Text ?? string.Empty).Contains(_confirmItemName, StringComparison.OrdinalIgnoreCase))
            return false;
        m.Yes();
        DebugLog.Verbose($"Auto-discard: confirmed the discard prompt for {_confirmItemName}");
        return true;
    }

    // First discardable stack found in the bags. One per call: the caller acts on it and comes back
    // next interval, so the scan always sees the post-discard state.
    private static bool TryFindDiscardable(Configuration cfg, out InventoryType bag, out ushort slot,
        out uint itemId, out int quantity)
    {
        bag = default;
        slot = 0;
        itemId = 0;
        quantity = 0;

        var im = InventoryManager.Instance();
        if (im == null)
            return false;

        foreach (var b in Bags)
        {
            var c = im->GetInventoryContainer(b);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;
                if (Refused.Contains(s->ItemId))
                    continue;
                if (!PassesHardRules(s, s->ItemId, cfg) || !MatchesMode(s->ItemId, cfg))
                    continue;
                bag = b;
                slot = (ushort)i;
                itemId = s->ItemId;
                quantity = s->Quantity;
                return true;
            }
        }
        return false;
    }

    // ---- The rules ----
    //
    // Hard rules first: things that are never discarded no matter which mode is on or what is on
    // the discard list. Between them they already exclude every relic material (all of which are
    // untradeable), all gear, everything usable, and anything the game itself marks undiscardable.
    private static bool PassesHardRules(InventoryItem* s, uint itemId, Configuration cfg)
    {
        if (itemId == 0)
            return false;
        if (cfg.NeverDiscardItemIds.Contains(itemId))
            return false;

        // Fail SAFE while the protected set is still being built: an unresolved set would quietly
        // downgrade every "is this a relic material?" question to "no" and let one through. An
        // empty set means not-ready, never "nothing is protected".
        var guarded = ProtectedIds();
        if (guarded.Count == 0 || guarded.Contains(itemId))
            return false;
        if (GameState.IsRelicWeaponId(itemId))
            return false;

        // Live stack state.
        if (s->IsHighQuality())
            return false;                                   // HQ is never clutter
        if (s->IsCollectable())
            return false;
        if ((s->Flags & InventoryItem.ItemFlags.Relic) != 0) // the game's own relic bit
            return false;
        if (s->GetMateriaCount() > 0)                       // something is melded into it
            return false;

        LuminaItem row;
        try
        {
            if (Plugin.DataManager.GetExcelSheet<LuminaItem>().GetRowOrDefault(itemId) is not { } r)
                return false;
            row = r;
        }
        catch
        {
            return false;                                    // cannot read it -> do not touch it
        }

        if (row.IsIndisposable)
            return false;                                    // the game forbids discarding this
        if (row.IsUnique || row.IsUntradable)
            return false;                                    // covers every relic material
        if (row.IsCollectable || row.AlwaysCollectable)
            return false;
        if (row.EquipSlotCategory.RowId != 0)
            return false;                                    // never gear or a weapon
        if (row.ItemAction.RowId != 0)
            return false;                                    // never usable: maps, minions, food, tickets
        if (row.MateriaSlotCount != 0)
            return false;                                    // meldable, so not clutter
        if (MateriaCategory() != 0 && row.ItemUICategory.RowId == MateriaCategory())
            return false;                                    // every materia, not just known grades

        return true;
    }

    // Mode on top of the hard rules (which the caller has already applied -- keeping them separate
    // lets the preview report "safe but not selected" without evaluating the rules twice). The
    // explicit discard list wins in either mode; the rule-driven mode additionally takes ordinary
    // white, stackable, cheap materials -- mob-drop clutter.
    private static bool MatchesMode(uint itemId, Configuration cfg)
    {
        if (cfg.DiscardItemIds.Contains(itemId))
            return true;
        if (cfg.AutoDiscardMode != Configuration.DiscardMode.LowValueMaterials)
            return false;

        try
        {
            if (Plugin.DataManager.GetExcelSheet<LuminaItem>().GetRowOrDefault(itemId) is not { } row)
                return false;
            if (row.Rarity > 1)
                return false;                                // green and above are never "clutter"
            if (row.StackSize <= 1)
                return false;                                // one-offs are not drop clutter
            return row.PriceLow <= (uint)Math.Max(0, cfg.AutoDiscardMaxVendorPrice);
        }
        catch
        {
            return false;
        }
    }

    // ---- Protected ids ----
    private static readonly HashSet<uint> NoIds = new();

    private static HashSet<uint> ProtectedIds()
    {
        if (_protected is { Count: > 0 })
            return _protected;
        var now = Environment.TickCount64;
        if (now < _protectedRetryAt)
            return NoIds;
        _protectedRetryAt = now + ResolveRetryMs;

        var set = new HashSet<uint>();
        try
        {
            if (_protectedSource?.Invoke() is { } ids)
                foreach (var id in ids)
                    if (id != 0)
                        set.Add(id);
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Auto-discard: could not build the protected item set: {ex.Message}");
        }
        // Latch only when it actually resolved; an empty set means "not ready", so retry next call
        // rather than freezing an empty guard in place.
        if (set.Count > 0)
            _protected = set;
        return set;
    }

    private static uint MateriaCategory()
    {
        if (_materiaCategoryResolved)
            return _materiaCategory;
        var now = Environment.TickCount64;
        if (now < _materiaRetryAt)
            return 0;
        _materiaRetryAt = now + ResolveRetryMs;
        try
        {
            foreach (var id in Data.MateriaCatalog.AllMateriaItemIds())
            {
                if (id == 0)
                    continue;
                if (Plugin.DataManager.GetExcelSheet<LuminaItem>().GetRowOrDefault(id) is not { } row)
                    continue;
                _materiaCategory = row.ItemUICategory.RowId;
                if (_materiaCategory != 0)
                {
                    _materiaCategoryResolved = true;
                    return _materiaCategory;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Auto-discard: could not resolve the materia category: {ex.Message}");
        }
        return _materiaCategory; // 0 = unresolved; retried until the catalogue is ready
    }

    // ---- YesAlready handshake (identical to LeveReturn's) ----
    private static void Suppress()
    {
        if (EzSharedData.TryGet<HashSet<string>>(YesAlreadyStopKey, out var stop))
        {
            stop.Add(Plugin.PluginInterface.InternalName);
            _suppressed = true;
        }
    }

    public static void Release()
    {
        if (EzSharedData.TryGet<HashSet<string>>(YesAlreadyStopKey, out var stop))
            stop.Remove(Plugin.PluginInterface.InternalName);
        _suppressed = false;
    }

    // ---- Preview (configuration window) ----
    //
    // Every distinct stack in the bags with the verdict the rules would give it right now, so the
    // window can show exactly what enabling this would delete -- and offer the two list buttons on
    // the items that matter. Safe means "the hard rules allow it", i.e. a legitimate candidate for
    // the discard list even when the current mode would leave it alone.
    public readonly record struct BagEntry(uint ItemId, string Name, int Quantity, uint VendorPrice,
        bool WouldDiscard, bool Safe);

    public static List<BagEntry> Preview()
    {
        var result = new List<BagEntry>();
        var cfg = _config;
        if (cfg == null)
            return result;

        var im = InventoryManager.Instance();
        if (im == null)
            return result;

        // Aggregate by item id: the bags hold one stack per slot and a screenful of duplicate rows
        // would bury the thing the reader is looking for.
        var byItem = new Dictionary<uint, BagEntry>();
        foreach (var b in Bags)
        {
            var c = im->GetInventoryContainer(b);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;
                var id = s->ItemId;
                var safe = PassesHardRules(s, id, cfg);
                var would = safe && !Refused.Contains(id) && MatchesMode(id, cfg);
                uint price = 0;
                try { price = Plugin.DataManager.GetExcelSheet<LuminaItem>().GetRowOrDefault(id)?.PriceLow ?? 0; }
                catch { /* price is decoration; a failed read just shows 0 */ }

                // Merge permissively: HQ and NQ of one item share an id but not a verdict, so a row
                // must read "discard" if ANY of its stacks would go -- otherwise an HQ stack seen
                // first would report the item as safe while the engine deletes the NQ one.
                if (byItem.TryGetValue(id, out var prev))
                    byItem[id] = prev with
                    {
                        Quantity = prev.Quantity + s->Quantity,
                        WouldDiscard = prev.WouldDiscard || would,
                        Safe = prev.Safe || safe,
                    };
                else
                    byItem[id] = new BagEntry(id, GameState.ItemName(id), s->Quantity, price, would, safe);
            }
        }

        result.AddRange(byItem.Values);
        result.Sort((a, b) => a.WouldDiscard == b.WouldDiscard
            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : (a.WouldDiscard ? -1 : 1));
        return result;
    }

    // A session-refused item can be retried after the user changes the lists.
    public static void ClearRefused() => Refused.Clear();
}
