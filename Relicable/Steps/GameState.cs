using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Relicable.Data;
using Relicable.Model;

namespace Relicable.Steps;

// Facade over verified FFXIVClientStructs accessors. Checked against current
// FFXIVClientStructs (aers/main):
//
//   RelicNote.Instance()                                  -> RelicNote*
//   RelicNote.RelicNoteId                                 (active book id, byte)
//   RelicNote.GetMonsterProgress(int index)               -> byte (0..3 kills)
//   RelicNote.IsDungeonComplete/IsFateComplete/IsLeveComplete(int)
//   RelicNote.IsMonsterNoteTarget(Character*)             -> bool (see Targeting)
//   InventoryManager.Instance()->GetInventoryItemCount(uint, ...) -> int
//   FateManager.Instance()->GetCurrentFateId()/SyncedFateId/TryGetFatePosition
//
// All accessors null-check the singleton because it is null at the title screen.
public static unsafe class GameState
{
    // ---- Relic note (Trials of the Braves / Animus book) ----
    // There is only ever one active relic note; its id is RelicNoteId. Progress
    // is read per slot, so objectives reference a slot index, not a "book number".

    public static byte ActiveRelicNoteId()
    {
        var n = RelicNote.Instance();
        return n == null ? (byte)0 : n->RelicNoteId;
    }

    public static int MonsterProgress(int slot)
    {
        var n = RelicNote.Instance();
        return n == null ? 0 : n->GetMonsterProgress(slot);
    }

    public static bool IsDungeonComplete(int slot)
    {
        var n = RelicNote.Instance();
        return n != null && n->IsDungeonComplete(slot);
    }

    public static bool IsFateComplete(int slot)
    {
        var n = RelicNote.Instance();
        return n != null && n->IsFateComplete(slot);
    }

    public static bool IsLeveComplete(int slot)
    {
        var n = RelicNote.Instance();
        return n != null && n->IsLeveComplete(slot);
    }

    // The active book's slots that are still incomplete per the game's own RelicNote memory, as
    // human-readable labels (e.g. "dungeon slot 0"). Empty means the active book is fully done --
    // or that there is no active book / a read failed (fail safe: never fabricate an "incomplete"
    // that would wrongly block advancing). Reads the RelicNote SHEET to learn which slots the
    // active book actually populates, then checks each populated slot against live memory. This is
    // the authoritative "is the book finished" view, independent of whether Relicable managed to
    // GENERATE a runnable objective for every slot -- so a slot it could not build (e.g. a dungeon
    // whose TerritoryType did not resolve) cannot masquerade as complete and trigger a doomed
    // "buy the next book". A monster slot needs 3 kills (all RelicNote MonsterCount rows are 3).
    public static IReadOnlyList<string> IncompleteActiveBookSlots()
    {
        var result = new List<string>();
        var active = ActiveRelicNoteId();
        if (active == 0)
            return result;
        try
        {
            if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.RelicNote>()
                    .GetRowOrDefault(active) is not { } note)
                return result;

            var common = note.MonsterNoteTargetCommon;
            for (var i = 0; i < common.Count; i++)
                if (common[i].IsValid && common[i].RowId != 0 && MonsterProgress(i) < 3)
                    result.Add($"monster slot {i}");

            var nm = note.MonsterNoteTargetNM;
            for (var i = 0; i < nm.Count; i++)
                if (nm[i].IsValid && nm[i].RowId != 0 && !IsDungeonComplete(i))
                    result.Add($"dungeon slot {i}");

            var fates = note.Fate;
            for (var i = 0; i < fates.Count; i++)
                if (fates[i].IsValid && fates[i].RowId != 0 && !IsFateComplete(i))
                    result.Add($"FATE slot {i}");

            var leves = note.Leve;
            for (var i = 0; i < leves.Count; i++)
                if (leves[i].IsValid && leves[i].RowId != 0 && !IsLeveComplete(i))
                    result.Add($"leve slot {i}");
        }
        catch
        {
            result.Clear(); // read error -> report "unknown" (empty), not a false incomplete
        }
        return result;
    }

    // ---- Inventory (Atma, Sphere Scroll, materia) ----

    public static int InventoryCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId);
    }

    // Searches the market board for an item by NAME -- exactly as typing the name into
    // the board's search box and pressing Enter -- by driving the live "ItemSearch"
    // addon (the market board search window). This build's FFXIVClientStructs exposes no
    // search-by-id API on AgentItemSearch, and ItemFinderModule.SearchForItem is the
    // inventory "find item" highlight, NOT the market board, so the addon is driven
    // directly. Only fires when the market board search window is open, so the caller can
    // fall back (e.g. copy the name to the clipboard). Returns true only when it fired.
    public static bool TrySearchMarketBoard(string itemName)
    {
        if (string.IsNullOrEmpty(itemName))
            return false;
        var ptr = Plugin.GameGui.GetAddonByName("ItemSearch", 1);
        if (ptr.IsNull)
            return false;
        if (!((AtkUnitBase*)ptr.Address)->IsVisible)
            return false;

        var addon = (AddonItemSearch*)ptr.Address;
        if (addon->SearchTextInput == null)
            return false;

        // The window may be on the Wishlist / Favorites / a category tab (Mode != Normal)
        // -- that is why a bare RunSearch showed the wishlist. Switch to Normal
        // (text-search) mode first, then set the name and run: the same as selecting the
        // search tab, typing the name, and pressing Enter.
        addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, -1);
        addon->SearchTextInput->SetText(itemName);
        addon->SearchText.SetString(itemName);
        addon->SearchText2.SetString(itemName);
        addon->RunSearch(false);
        return true;
    }

    // Count of a key item (EventItem) held in the Key Items container. Key items are a
    // separate container from the normal bags, so GetInventoryItemCount does not see
    // them; iterate the container directly. Used for the relic treasure maps, which
    // live in Key Items rather than the normal inventory.
    public static int KeyItemCount(uint eventItemId)
    {
        if (eventItemId == 0)
            return 0;
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var c = im->GetInventoryContainer(InventoryType.KeyItems);
        if (c == null)
            return 0;
        var total = 0;
        for (var i = 0; i < c->Size; i++)
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && slot->ItemId == eventItemId)
                total += (int)slot->Quantity;
        }
        return total;
    }

    // ---- Retainers (Novus materia sourcing) ----
    //
    // AutoRetainer's IPC does not expose item-level retainer inventory, so materia
    // counts are read straight from game memory while a retainer is open at the
    // summoning bell: the game loads the active retainer's bags into the
    // RetainerPage1..7 containers. The controller scans them on the frames a retainer
    // is open and caches the result (see Configuration.RetainerMateria).

    // The logged-in character's content id, used to key the retainer cache per
    // character so multi-box setups do not collide. Read from PlayerState because
    // current Dalamud's IClientState no longer exposes LocalContentId.
    public static ulong OwnerContentId()
    {
        var ps = PlayerState.Instance();
        return ps == null ? 0UL : ps->ContentId;
    }

    private static readonly InventoryType[] RetainerBags =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    };

    // True when a retainer's inventory is currently loaded (bell open on a retainer).
    public static bool IsRetainerInventoryOpen()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        var c = im->GetInventoryContainer(InventoryType.RetainerPage1);
        return c != null && c->IsLoaded;
    }

    // The currently open retainer's id and name, or false if none is open.
    public static bool TryGetActiveRetainer(out ulong id, out string name)
    {
        id = 0;
        name = string.Empty;
        var rm = RetainerManager.Instance();
        if (rm == null)
            return false;
        var r = rm->GetActiveRetainer();
        if (r == null || r->RetainerId == 0)
            return false;
        id = r->RetainerId;
        name = r->NameString;
        return true;
    }

    // Sum, by item id, how many of the wanted items the open retainer holds across its
    // seven bag pages. Returns an empty map if no retainer is open. Generic over any
    // item-id set (the Novus materia catalog or the base-relic material catalog).
    public static Dictionary<uint, int> ScanOpenRetainerItems(IReadOnlyCollection<uint> itemIds)
    {
        var found = new Dictionary<uint, int>();
        var im = InventoryManager.Instance();
        if (im == null || !IsRetainerInventoryOpen())
            return found;

        var wanted = new HashSet<uint>(itemIds);
        foreach (var bag in RetainerBags)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                if (wanted.Contains(slot->ItemId))
                    found[slot->ItemId] = found.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }
        return found;
    }

    // Back-compat alias for the Novus materia scanner; identical to ScanOpenRetainerItems.
    public static Dictionary<uint, int> ScanOpenRetainerMateria(IReadOnlyCollection<uint> materiaIds)
        => ScanOpenRetainerItems(materiaIds);

    // Finds the first bag slot in the OPEN retainer that holds one of the wanted item ids,
    // returning its page + slot so the caller can retrieve that stack. False if none / no
    // retainer open. Used by the Novus auto-fetch to pull route materia.
    public static bool TryFindRetainerSlot(IReadOnlyCollection<uint> wantedItemIds,
        out InventoryType page, out ushort slot, out uint itemId)
    {
        page = default;
        slot = 0;
        itemId = 0;
        var im = InventoryManager.Instance();
        if (im == null || !IsRetainerInventoryOpen())
            return false;

        var wanted = new HashSet<uint>(wantedItemIds);
        foreach (var bag in RetainerBags)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && s->Quantity > 0 && wanted.Contains(s->ItemId))
                {
                    page = bag;
                    slot = (ushort)i;
                    itemId = s->ItemId;
                    return true;
                }
            }
        }
        return false;
    }

    // ---- Leves ----

    public static int LeveAllowances()
    {
        var q = QuestManager.Instance();
        return q == null ? 0 : q->NumLeveAllowances;
    }

    // Whether a specific levequest (Leve sheet row) has ever been completed.
    public static bool IsLevequestComplete(uint leveId)
    {
        var q = QuestManager.Instance();
        return q != null && q->IsLevequestComplete((ushort)leveId);
    }

    // Whether a specific levequest is currently accepted/active.
    public static bool IsLeveAccepted(uint leveId)
    {
        var q = QuestManager.Instance();
        return q != null && q->GetLeveQuestById((ushort)leveId) != null;
    }

    // The leve ids currently accepted (active). Used to discover the id of a
    // freshly accepted filler leve by diffing against a pre-accept snapshot.
    public static List<ushort> ActiveLeveIds()
    {
        var list = new List<ushort>();
        var q = QuestManager.Instance();
        if (q == null)
            return list;
        foreach (ref readonly var lw in q->LeveQuests)
            if (lw.LeveId != 0)
                list.Add(lw.LeveId);
        return list;
    }

    // ---- Quests (prerequisite + per-part progress) ----
    //
    // IsQuestComplete and GetQuestSequence are static QuestManager member functions
    // (verified against current FFXIVClientStructs); each masks the full Quest-sheet
    // row id to the ushort the game uses. GetQuestSequence returns 0 when the quest is
    // not currently active, even if it was completed previously.

    public static bool IsQuestComplete(uint questId)
        => questId != 0 && QuestManager.IsQuestComplete(questId);

    public static int QuestSequence(uint questId)
        => questId == 0 ? 0 : QuestManager.GetQuestSequence(questId);

    // The six per-quest "work" bytes (QuestWork.Variables) the game uses to track
    // sub-step progress within a sequence. Reading them lets Relicable verify a base-relic
    // step the exact way Questionable does (nibble-compare via QuestWorkUtils), rather than
    // only watching the coarse whole-quest sequence. Returns null when the quest is not
    // currently ACCEPTED (no QuestWork row) or not logged in. Pass the FULL Quest-sheet id;
    // it is masked to the ushort GetQuestById expects (the 0x10000 masking trap).
    public static byte[]? QuestWorkVariables(uint questId)
    {
        if (questId == 0)
            return null;
        var qm = QuestManager.Instance();
        if (qm == null)
            return null;
        var qw = qm->GetQuestById((ushort)(questId & 0xFFFF));
        if (qw == null)
            return null;

        var span = qw->Variables; // FixedSizeArray6<byte> -> Span<byte>, length 6
        var vars = new byte[6];
        for (var i = 0; i < vars.Length && i < span.Length; i++)
            vars[i] = span[i];
        return vars;
    }

    // ---- Duties (trial/dungeon unlock and completion state) ----
    //
    // IsInstanceContentUnlocked / IsInstanceContentCompleted are static UIState member
    // functions (verified against current FFXIVClientStructs). The id is the
    // InstanceContent row id, resolved from a ContentFinderCondition name in
    // BaseRelicCatalog. Used to decide whether the relic trials still need their
    // entrance examined or can simply be queued.

    public static bool IsDutyUnlocked(uint instanceContentId)
        => instanceContentId != 0 && UIState.IsInstanceContentUnlocked(instanceContentId);

    public static bool IsDutyCompleted(uint instanceContentId)
        => instanceContentId != 0 && UIState.IsInstanceContentCompleted(instanceContentId);

    // Leave the current instanced duty immediately, WITHOUT the "Leave Duty?" confirmation, via the
    // game's own EventFramework.LeaveCurrentContent(forced: true). Relicable drives the leave itself
    // (rather than letting AutoDuty walk out on the boss kill) so it can hold in the instance until a
    // RelicNote dungeon-slot credit lands -- a too-fast exit permanently loses that credit. Returns
    // true when the leave was issued; false (retry next tick) when the game currently disallows it.
    public static unsafe bool LeaveDuty()
    {
        try
        {
            if (!EventFramework.CanLeaveCurrentContent())
                return false;
            EventFramework.LeaveCurrentContent(true);
            return true;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Warning($"Relicable: LeaveDuty failed: {ex.Message}");
            return false;
        }
    }

    // Whether the game currently allows leaving the instance (diagnostic: lets the hold-for-credit
    // loop log WHY a leave is not taking -- a persistent false here means the game is refusing the
    // leave, e.g. a boss-death / duty-complete transition, not a Relicable logic bug).
    public static unsafe bool CanLeaveDuty()
    {
        try { return EventFramework.CanLeaveCurrentContent(); }
        catch { return false; }
    }

    // ---- Player / active job ----

    // The equipped soul-crystal job (ClassJob sheet row id), or 0 if not logged in.
    // Reading the job (not the base class) already disambiguates Arcanist into
    // Summoner or Scholar whenever a job stone is equipped.
    public static uint ActiveClassJobId()
    {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p == null ? 0u : p.ClassJob.RowId;
    }

    // Level on the currently active job, or 0 if not logged in.
    public static int ActiveJobLevel()
    {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p == null ? 0 : p.Level;
    }

    // ---- FATEs (Atma, Nexus) ----

    public static ushort CurrentFateId()
    {
        var fm = FateManager.Instance();
        return fm == null ? (ushort)0 : fm->GetCurrentFateId();
    }

    public static ushort SyncedFateId()
    {
        var fm = FateManager.Instance();
        return fm == null ? (ushort)0 : fm->SyncedFateId;
    }

    // True when level-synced to the FATE we are currently in.
    public static bool IsSyncedToCurrentFate()
    {
        var fm = FateManager.Instance();
        if (fm == null)
            return false;
        var cur = fm->GetCurrentFateId();
        return cur != 0 && fm->SyncedFateId == cur;
    }

    public static bool TryGetFatePosition(ushort fateId, out System.Numerics.Vector3 pos)
    {
        pos = default;
        var fm = FateManager.Instance();
        if (fm == null)
            return false;
        fixed (System.Numerics.Vector3* p = &pos)
            return fm->TryGetFatePosition(fateId, p);
    }

    // ---- Relic item (equipped main-hand id) ----

    // The equipped main-hand item's display name, for distinguishing relic tiers by name (the
    // bare base relic "Curtana" from "Unfinished Curtana" mid-quest or "Curtana Zenith"). Empty
    // when nothing is equipped.
    public static string EquippedMainHandName()
    {
        var id = EquippedRelicItemId();
        if (id == 0)
            return string.Empty;
        return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            .GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
    }

    // The display name of any item id (Lumina Item sheet), or empty if it does not resolve.
    // Used by the Atma tracker to label each of the twelve atmas.
    public static string ItemName(uint itemId)
    {
        if (itemId == 0)
            return string.Empty;
        return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            .GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;
    }

    // The equipped main-hand item id, used to detect a successful UpgradeRelic.
    // Read from the equipped-items container slot 0 (main hand).
    public static uint EquippedRelicItemId() => EquippedWeaponItemId(0);

    // The item id in an equipped WEAPON slot: 0 = main hand, 1 = off hand (the Paladin's Holy
    // Shield, the only relic that occupies it). 0 when the slot is empty or unreadable.
    public static uint EquippedWeaponItemId(ushort equipSlot)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return 0;
        var slot = c->GetInventorySlot(equipSlot);
        return slot == null ? 0u : slot->ItemId;
    }

    // ---- Relic stage (inferred from the equipped relic weapon's upgrade tier) ----
    //
    // The weapon a player holds is the authoritative record of how far the relic line
    // has progressed: each upgrade hands back a new item id, so the equipped tier proves
    // which stages are already finished -- independent of any re-armable inventory count.
    // The controller uses this so an endless lower-stage farm cannot park Auto selection
    // below the player's real progress (the "Nexus seen as Novus" symptom).
    //
    // "Completed-through" semantics: a Novus weapon proves Novus done (the Light/Nexus
    // farm then runs on that same weapon); a Nexus weapon proves Nexus done (the Braves
    // stage is next); an il125 "Zodiac Braves" weapon proves the Braves stage done and is
    // where the Zeta Mahatma is then charged; a final il135 Zeta weapon proves it all done.
    public static RelicStage EquippedRelicStage()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return RelicStage.None;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return RelicStage.None;

        var best = RelicStage.None;
        for (var i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand (Paladin's Holy Shield)
        {
            var slot = c->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;
            var stage = StageOfEquippedItem(slot->ItemId);
            if ((int)stage > (int)best)
                best = stage;
        }
        return best;
    }

    // Classify a single equipped item id into the stage it proves complete. The Novus, Braves,
    // and final Zeta weapons use their own verified id tables (their names are not "<base>
    // <tier>", so a name build cannot find them); Atma, Animus, and Nexus are "<base> <tier>"
    // and resolve by item name via RelicWeaponStages, so no further numeric ids are hardcoded.
    private static RelicStage StageOfEquippedItem(uint itemId)
    {
        if (itemId == 0)
            return RelicStage.None;
        if (ZetaRelicItemIds.Contains(itemId))
            return RelicStage.Zeta; // il135 Zeta weapon held -> the whole line is complete
        if (BravesRelicItemIds.Contains(itemId))
            return RelicStage.Braves; // il125 Braves weapon held -> Braves done, Zeta (Mahatma) on it
        if (NovusRelicItemIds.Contains(itemId))
            return RelicStage.Novus;
        return RelicWeaponStages.StageOf(itemId);
    }

    // The highest relic tier held ANYWHERE -- hands, armoury weapon slots, or bags -- or None when
    // no relic weapon is held at all.
    //
    // A relic proves its tier by existing, not by being worn. This matters most the moment a stage
    // FINISHES: every upgrade (and the base relic's final turn-in) hands the new weapon back
    // UNEQUIPPED, so for the window between receiving it and putting it on, the equipped-slot read
    // says None -- "no relic progress at all" -- and stage selection re-opens work the character has
    // just finished, including other jobs' base relics. Reported live: finishing the Artemis Bow on
    // Bard and having the run immediately pick up Monk's line and buy a second quenching oil.
    public static RelicStage HighestHeldRelicStage()
    {
        var best = RelicStage.None;
        var im = InventoryManager.Instance();
        if (im == null)
            return best;
        foreach (var bag in ZenithSearchContainers)
        {
            var c = im->GetInventoryContainer(bag);
            // EquippedItems reads without the IsLoaded gate (as EquippedRelicStage does); the
            // armoury/bag containers keep it, matching every other scan here.
            if (c == null || (bag != InventoryType.EquippedItems && !c->IsLoaded))
                continue;
            // EquippedItems holds every gear slot; only the two weapon slots (0 main, 1 off) matter.
            var max = bag == InventoryType.EquippedItems ? 2 : c->Size;
            for (var i = 0; i < max; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;
                var stage = StageOfEquippedItem(s->ItemId);
                if ((int)stage > (int)best)
                    best = stage;
            }
        }
        return best;
    }

    // ---- Relic auto-equip (best-effort; ensures the relic is on before a duty) ----
    //
    // True if an item id is one of the known relic weapons (any tier), reusing the same
    // classification as the equipped-stage detection.
    public static bool IsRelicWeaponId(uint itemId)
        => itemId != 0 && StageOfEquippedItem(itemId) != RelicStage.None;

    // Containers an unequipped relic weapon can sit in: the main-hand armoury slot (where a
    // weapon lands when swapped off) first, then the four bags.
    private static readonly InventoryType[] RelicSearchContainers =
    {
        InventoryType.ArmoryMainHand,
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    // Find the first known relic weapon that is NOT currently equipped (sitting in the armoury
    // or a bag), so the caller can equip it before a duty. False if none is found.
    public static bool TryFindRelicInBags(out InventoryType container, out ushort slot)
    {
        container = default;
        slot = 0;
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        foreach (var bag in RelicSearchContainers)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;
                if (IsRelicWeaponId(s->ItemId))
                {
                    container = bag;
                    slot = (ushort)i;
                    return true;
                }
            }
        }
        return false;
    }

    // ---- Zenith gate (finished base relics awaiting the Furnace trade) ----
    //
    // The Zenith step is a pure ITEM gate: merely HOLDING the finished bare base relic keeps
    // the step detected -- it does not need to be equipped. Containers such a weapon can sit
    // in: the hands, the armoury chest weapon slots (main hand, plus off hand for the
    // Paladin's Holy Shield), and the four bags.
    private static readonly InventoryType[] ZenithSearchContainers =
    {
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    // True when the weapon in the main or off hand (equipped only, bags/armoury NOT counted)
    // is a bare base relic still awaiting its Zenith trade. Automation gates key on this so an
    // alt job's parked relic cannot interrupt a run on the equipped weapon (see
    // BaseRelicState.EquippedNeedsZenith); the inventory-wide scan below is for guidance.
    public static bool EquippedZenithPending()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return false;
        for (var i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand (Holy Shield)
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && RelicWeaponStages.IsBareBaseRelic(slot->ItemId))
                return true;
        }
        return false;
    }

    // Every FINISHED bare base relic (the Zenith-pending form; RelicWeaponStages.IsBareBaseRelic)
    // currently held, as item id -> how many, scanned across hands/armoury chest/bags. Weapons do
    // not stack, so the count is occupied slots -- several relics at the same stage report x2/x3.
    // Fail safe: an unloaded or cleared container scans as empty, never a false "needs Zenith".
    public static Dictionary<uint, int> ZenithPendingWeapons()
    {
        var found = new Dictionary<uint, int>();
        var im = InventoryManager.Instance();
        if (im == null)
            return found;
        foreach (var bag in ZenithSearchContainers)
        {
            var c = im->GetInventoryContainer(bag);
            // EquippedItems reads like EquippedRelicStage (no IsLoaded gate); the armoury/bag
            // containers keep the IsLoaded gate the other bag scans use.
            if (c == null || (bag != InventoryType.EquippedItems && !c->IsLoaded))
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && RelicWeaponStages.IsBareBaseRelic(s->ItemId))
                    found[s->ItemId] = found.GetValueOrDefault(s->ItemId) + 1;
            }
        }
        return found;
    }

    // Find the first held relic weapon (across the hands, the armoury chest, and the four bags)
    // whose id satisfies `match`. includeEquipped controls whether the two equipped weapon slots
    // (main + off hand) are scanned. Used by the Atma upgrade to locate the Zenith weapon to turn
    // in (unequipped) and the resulting Atma weapon to re-equip. Reuses ZenithSearchContainers
    // (equipped + armoury main/off + bags).
    public static bool TryFindHeldRelic(System.Func<uint, bool> match, bool includeEquipped,
        out InventoryType container, out ushort slot, out uint itemId)
    {
        container = default;
        slot = 0;
        itemId = 0;
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        foreach (var bag in ZenithSearchContainers)
        {
            if (!includeEquipped && bag == InventoryType.EquippedItems)
                continue;
            var c = im->GetInventoryContainer(bag);
            if (c == null || (bag != InventoryType.EquippedItems && !c->IsLoaded))
                continue;
            // EquippedItems holds every gear slot; only the two weapon slots (0 main, 1 off) matter.
            var max = bag == InventoryType.EquippedItems ? 2 : c->Size;
            for (var i = 0; i < max; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && match(s->ItemId))
                {
                    container = bag;
                    slot = (ushort)i;
                    itemId = s->ItemId;
                    return true;
                }
            }
        }
        return false;
    }

    // Move an equipped weapon (slot 0 main / 1 off) out of the hands into a free armoury or bag
    // slot, so it becomes UNEQUIPPED -- Jalzahn's Zenith enhancement lists only unequipped relics.
    // Prefers the matching armoury weapon container, then the four bags. False if nothing moved.
    public static bool TryUnequipWeapon(ushort equipSlot)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        var eq = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (eq == null)
            return false;
        var src = eq->GetInventorySlot(equipSlot);
        if (src == null || src->ItemId == 0)
            return false;
        var targets = equipSlot == 1
            ? new[] { InventoryType.ArmoryOffHand, InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 }
            : new[] { InventoryType.ArmoryMainHand, InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
        foreach (var t in targets)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded)
                continue;
            for (ushort i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                {
                    try { im->MoveItemSlot(InventoryType.EquippedItems, equipSlot, t, i); return true; }
                    catch { return false; }
                }
            }
        }
        return false;
    }

    // Best-effort equip of a weapon from a bag/armoury slot into the main hand. MoveItemSlot is
    // the standard bag->equipped path (the documented crash is retainer<->player only); wrapped
    // so a failure cannot take down the run. The caller verifies via EquippedRelicStage.
    public static void TryEquipFromBag(InventoryType container, ushort slot)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return;
        try { im->MoveItemSlot(container, slot, InventoryType.EquippedItems, 0); }
        catch { /* best-effort; verified by the caller */ }
    }

    // ---- Nexus light (read live from the equipped Novus relic) ----
    //
    // The Novus relic accumulates "Light" (0..2000) toward its Nexus upgrade in the
    // same InventoryItem field the game uses for spiritbond/collectability, verified as
    // FFXIVClientStructs InventoryItem.SpiritbondOrCollectability (ushort, offset 0x18).
    // This is the same value the in-game Light gauge shows as 0/2000. Only the
    // Novus relics carry a Light value there, so it is read only when a known Novus
    // weapon is equipped (main hand, or off hand for Holy Shield Novus).
    public const int NexusLightMax = 2000;

    // The eleven Novus Zodiac weapons -- fixed game data (the Novus relic item ids,
    // one per job). These are the weapons that gather Light during the Nexus stage.
    // Public so EquippedRelicStage (and the stage filter in RelicController) can treat a
    // held Novus weapon as authoritative proof the Novus stage is complete.
    public static readonly System.Collections.Generic.HashSet<uint> NovusRelicItemIds = new()
    {
        7863, 7864, 7865, 7866, 7867, 7868, 7869, 7870, 7871, 7872, 9253,
    };

    // Sets 'light' to the equipped Novus relic's current Light (0..2000) and returns
    // true when such a relic is equipped; returns false when none is, so the UI can
    // prompt the player to equip it rather than showing a misleading 0.
    public static bool TryGetNexusLight(out int light)
    {
        light = 0;
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return false;
        for (var i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && NovusRelicItemIds.Contains(slot->ItemId))
            {
                light = slot->SpiritbondOrCollectability;
                return true;
            }
        }
        return false;
    }

    // Convenience: the current Light, or 0 if no Novus relic is equipped.
    public static int NexusLight() => TryGetNexusLight(out var l) ? l : 0;

    // The Nexus light bar is full at 2000; this drives the LightGauge completion.
    public static bool IsLightGaugeFull()
        => TryGetNexusLight(out var l) && l >= NexusLightMax;

    // ---- Ifrit EX (Bowl of Embers) Infernal Nail detection ----
    //
    // Ifrit's "Infernal Nail" adds (the nail phase) make him invulnerable until every nail is
    // destroyed -- a long detour on an otherwise few-second unsynced clear. The Light and Mahatma
    // farms both default to the Bowl of Embers (Extreme), where the farm strategy is to abandon and
    // re-enter for a fresh burst rather than wait the nails out (see EnterDutyExecutor). These are the
    // BNpcName ids the game gives Ifrit's nails across the trial's variants (1186 = the ARR Bowl of
    // Embers Normal/Hard/Extreme nail; 10043/10044 = the later 6.x variants), verified against
    // BNpcName.csv. The nails are Ifrit-exclusive, so a live match unambiguously means the nail phase
    // has begun -- this never fires on a non-Ifrit farm duty. The NameId is matched locale-
    // independently, with the English name as a fallback.
    private static readonly HashSet<uint> IfritNailNameIds = new() { 1186, 10043, 10044 };

    // True when a live Infernal Nail is loaded in the object table (Ifrit's nail phase is up). Scans
    // battle NPCs only and requires positive HP, so a spent/dead nail is ignored. Must run on the
    // framework thread (called from the duty executor's Update), where the object table is safe to read.
    public static bool IfritNailPresent()
    {
        foreach (var o in Plugin.ObjectTable)
        {
            if (o is not IBattleNpc npc || npc.CurrentHp == 0)
                continue;
            if (IfritNailNameIds.Contains(npc.NameId)
                || string.Equals(npc.Name.TextValue, "Infernal Nail", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---- Zeta / Mahatma (read live from the equipped Zodiac Braves weapon) ----
    //
    // During the Zeta stage the equipped weapon is an IL125 "Zodiac Braves" weapon,
    // whose SpiritbondOrCollectability packs BOTH the Mahatma progress and the
    // active-slot state, decoded (and verified against the in-game Mahatma display) as:
    //   completed = sb / 500          (0..12 Mahatma awakened)
    //   raw       = sb % 500          (0 = none attached; 1 = attached at 0 points;
    //                                  2..80 = 1..40 points; 80 awakens the Mahatma)
    //   attached  = raw != 0          (a Mahatma is attached and filling)
    //   points    = raw <= 1 ? 0 : raw / 2   (0..40; the raw==1 -> 0 quirk)
    // There are 12 Mahatma (one per zodiac sign), 40 points each, filled one at a time;
    // each next Mahatma is attached at Remon (50 Poetics) before farming resumes.
    public const int MahatmaCount = 12;
    public const int MahatmaPointsMax = 40;

    // The eleven il125 "Zodiac Braves" weapons -- the weapon the Braves stage yields and
    // on which the Zeta Mahatma is then charged. Fixed game data; the eleven Braves
    // relic item ids. Public so the stage detector can recognize a held Braves weapon
    // (Braves complete; the Zeta/Mahatma farm then runs on it).
    public static readonly System.Collections.Generic.HashSet<uint> BravesRelicItemIds = new()
    {
        9491, 9492, 9493, 9494, 9495, 9496, 9497, 9498, 9499, 9500, 9501,
    };

    // The twelve unique Atma items (one per zone) collected during the Atma stage to forge the
    // Zodiac weapon. Verified against Item.csv ("Atma of the Maiden" ... "Atma of the Crab");
    // the same ids CBT's "Atma (Zodiac)" grind mode targets. Contiguous Item block 7851-7862.
    public static readonly System.Collections.Generic.IReadOnlyList<uint> AtmaItemIds = new uint[]
    {
        7851, 7852, 7853, 7854, 7855, 7856, 7857, 7858, 7859, 7860, 7861, 7862,
    };

    // How many of the twelve unique Atmas are currently held (0..12). 12 == the Atma FATE farm
    // is finished and the Zenith->Zodiac enhancement can be performed. Used to know when to stop
    // a delegated CBT Atma grind (CBT collects the atmas but does not do the enhancement).
    public static unsafe int AtmaCollectedCount()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var held = 0;
        foreach (var id in AtmaItemIds)
            if (im->GetInventoryItemCount(id) > 0)
                held++;
        return held;
    }

    // The eleven final il135 "Zeta" Zodiac weapons -- the end of the ARR relic line. Unlike
    // the earlier tiers these are NOT named "<base> Zeta": the Zeta weapon carries the il125
    // Braves weapon's unique name plus " Zeta" (Yoichi Bow Zeta, Excalibur Zeta, Ragnarok Zeta,
    // Longinus Zeta, Kaiser Knuckles Zeta, Nirvana Zeta, Lilith Rod Zeta, Apocalypse Zeta,
    // Last Resort Zeta, Sasuke's blades Zeta, plus the Paladin's Aegis Shield Zeta off-hand).
    // So, like Braves and Novus, they are recognized by a verified id table rather than by name
    // (the old "<base> Zeta" name build never resolved -- that was the "unresolved names" warning).
    // Ids are the contiguous Item-sheet block 10054-10064, verified against Item.csv.
    public static readonly System.Collections.Generic.HashSet<uint> ZetaRelicItemIds = new()
    {
        10054, 10055, 10056, 10057, 10058, 10059, 10060, 10061, 10062, 10063, 10064,
    };

    // True (and fills the outs) when a Zodiac Braves weapon is equipped (main or off
    // hand). completed = Mahatma awakened (0..12); points = current Mahatma fill
    // (0..40); attached = a Mahatma is currently attached and filling.
    public static bool TryGetMahatma(out int completed, out int points, out bool attached)
    {
        completed = 0;
        points = 0;
        attached = false;
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return false;
        for (var i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && BravesRelicItemIds.Contains(slot->ItemId))
            {
                int sb = slot->SpiritbondOrCollectability;
                completed = sb / 500;
                var raw = sb % 500;
                attached = raw != 0;
                points = raw <= 1 ? 0 : raw / 2;
                return true;
            }
        }
        return false;
    }

    // Convenience: number of Mahatma awakened (0..12), or 0 if no Braves weapon equipped.
    public static int MahatmaCompleted()
        => TryGetMahatma(out var completed, out _, out _) ? completed : 0;

    // All 12 Mahatma charged: the relic is ready for Jalzahn's final awakening. The raw "completed"
    // count tops out at 11, because it only increments when a full Mahatma is BANKED by attaching the
    // next at Remon, and the 12th has no next -- so the done state is the 12th (last) Mahatma sitting
    // full: completed == 11 && points == 40. (completed >= 12 is kept in case a version banks the last.)
    public static bool IsZetaFarmComplete()
        => TryGetMahatma(out var completed, out var points, out _)
           && (completed >= MahatmaCount
               || (completed >= MahatmaCount - 1 && points >= MahatmaPointsMax));

    // A Braves weapon is equipped and the next Mahatma must be attached at Remon. Two cases: nothing
    // is attached (attach the next), OR the current Mahatma is full AND there is still a next to
    // attach. A full Mahatma is banked by attaching the next at Remon (completed++, raw -> 500+), so
    // a maxed Mahatma must not be farmed forever. BUT the 12th (last) Mahatma has no next: once it is
    // full the charge is simply done (see IsZetaFarmComplete), so do NOT send the player to Remon for
    // a non-existent next -- that lands on a sign picker with nothing "(Available)" and stalls.
    public static bool NeedsMahatmaAttach()
    {
        if (!TryGetMahatma(out var completed, out var points, out var attached))
            return false;
        if (completed >= MahatmaCount)
            return false;
        if (!attached)
            return true; // none attached -> attach the next
        return points >= MahatmaPointsMax && completed < MahatmaCount - 1; // bank the full one, if a next exists
    }

    // Display name of the equipped Zodiac Braves weapon (e.g. "Lilith Rod"), or empty if none
    // is equipped. Remon's "select a weapon to receive mahatma" menu lists the weapon by name;
    // matching that name (not the menu's "...receive mahatma" header, which also contains the
    // word "mahatma") is how the attach step picks the right line, in any client language.
    public static string EquippedBravesWeaponName()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return string.Empty;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return string.Empty;
        for (var i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && BravesRelicItemIds.Contains(slot->ItemId))
                return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                    .GetRowOrDefault(slot->ItemId)?.Name.ExtractText() ?? string.Empty;
        }
        return string.Empty;
    }

    // True when an il125 "Zodiac Braves" weapon (the weapon the Zeta/Mahatma stage
    // charges) is in the main or off hand. Used to gate the Zeta farm: a relic that has
    // just finished Nexus holds the Nexus weapon and has no Braves weapon yet, so there
    // is no Mahatma gauge to fill until the player does the Nexus -> Zodiac Braves
    // upgrade. TryGetMahatma already scans both hands against BravesRelicItemIds.
    public static bool HasBravesRelicEquipped()
        => TryGetMahatma(out _, out _, out _);

    // Diagnostic: dump the equipped weapons' raw Mahatma field so the decode can be checked
    // against what the game shows (e.g. a reported 40/40 that the engine reads as 0/40). Logged
    // via /relic mahatma. The decode assumes raw = completed*500 + points*2; this verifies it.
    public static void LogMahatmaDebug()
    {
        var im = InventoryManager.Instance();
        if (im == null) { Plugin.Log.Information("Relicable: Mahatma debug: no InventoryManager"); return; }
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null) { Plugin.Log.Information("Relicable: Mahatma debug: no equipped container"); return; }
        for (var i = 0; i <= 1; i++)
        {
            var slot = c->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;
            int sb = slot->SpiritbondOrCollectability;
            var isBraves = BravesRelicItemIds.Contains(slot->ItemId);
            Plugin.Log.Information(
                $"Relicable: Mahatma debug slot {i}: item={slot->ItemId} bravesWeapon={isBraves} " +
                $"raw={sb} (decoded: completed={sb / 500}, points={(sb % 500) / 2}, attached={sb % 500 != 0})");
        }
    }
}
