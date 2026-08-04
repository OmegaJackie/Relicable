using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Relicable.Data;
using Relicable.Diagnostics;
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

    // How many of an item the character holds across the bags, the armoury chest, and the
    // equipped slots.
    //
    // BOTH QUALITIES ARE COUNTED, and that is the whole point of the two calls. The game's
    // GetInventoryItemCount signature is
    //     GetInventoryItemCount(itemId, isHq = false, checkEquipped = true, checkArmory = true, ...)
    // so the defaults already cover the armoury and the equipped slots -- but isHq is a MATCH,
    // not a minimum: the default counts NQ copies ONLY and silently ignores every HQ one. Most
    // items this plugin counts (atma, materia, Alexandrite, the maps, the quenching oil, the
    // Thavnairian Mist, the relic weapons themselves) have no HQ form, so the miss was invisible
    // -- until the "A Relic Reborn" part-2 check, whose CLASS WEAPON and craft materials are
    // routinely bought or crafted HQ. Those read as "you have 0 / 1" while sitting in the bag,
    // which is the reported "the inventory check does not see the weapon in your inventory or
    // armoury chest". NQ and HQ are disjoint, so summing them cannot double-count.
    public static int InventoryCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        return im->GetInventoryItemCount(itemId)
               + im->GetInventoryItemCount(itemId, isHq: true);
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

    // Display name for a ClassJob sheet row id, or empty when it does not resolve. Diagnostics
    // only: it lets a "could not determine the relic job" message name what the game actually
    // reported (e.g. "arcanist") instead of leaving the reader to guess from a bare number.
    public static string ClassJobName(uint classJobId)
    {
        if (classJobId == 0)
            return string.Empty;
        try
        {
            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
                       .GetRowOrDefault(classJobId)?.Name.ExtractText() ?? string.Empty;
        }
        catch { return string.Empty; }
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

    // How many finished relic weapons are held at or above `stage`, counted across the hands, the
    // armoury weapon slots and the bags. This is the "do I already have the end item?" question, and
    // it is the only honest way to ask whether a stage's work is done.
    //
    // WHY NOT ASK THE QUESTS. The stage's own quests cannot answer it. The four Braves material
    // quests are REPEATABLE, so finishing one returns its sequence to 0 -- identical to never having
    // taken it -- and the plugin walked back to the giver and accepted it again, forever. The
    // materials cannot answer it either: they are CONSUMED at turn-in, so "the key item is missing"
    // reads the same before the stage and after it. The weapon is the one witness that survives,
    // because the stage's whole purpose is to produce it.
    //
    // AT OR ABOVE, not equal: each upgrade consumes the previous weapon, so a character who pushed
    // on to Zeta no longer holds the Braves weapon -- but they certainly finished the Braves stage.
    // Counting only the exact tier would re-open every stage the moment the next one completed.
    //
    // Distinct ITEM IDS, not slots: a weapon in transit can be seen twice (and the Paladin's pair is
    // two ids of one line), so this counts finished lines, not stacks.
    public static int HeldRelicCountAtOrAbove(RelicStage stage)
    {
        if (stage == RelicStage.None)
            return 0;
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;

        var seen = new System.Collections.Generic.HashSet<uint>();
        foreach (var bag in ZenithSearchContainers)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null || (bag != InventoryType.EquippedItems && !c->IsLoaded))
                continue;
            var max = bag == InventoryType.EquippedItems ? 2 : c->Size;
            for (var i = 0; i < max; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;
                if ((int)StageOfEquippedItem(s->ItemId) >= (int)stage)
                    seen.Add(s->ItemId);
            }
        }
        return seen.Count;
    }

    // ---- Relic auto-equip (best-effort; ensures the relic is on before a duty) ----
    //
    // True if an item id is one of the known relic weapons (any tier), reusing the same
    // classification as the equipped-stage detection.
    public static bool IsRelicWeaponId(uint itemId)
        => itemId != 0 && StageOfEquippedItem(itemId) != RelicStage.None;

    // True when an "Unfinished <weapon>" is in the hands. This is the weapon 'A Relic Reborn'
    // means by "arm yourself with the unfinished <weapon>", and the only one whose beastman kills
    // and Hydra clear credit the quest -- EquippedRelicStage() != None is NOT a sufficient check
    // for those steps, because every other tier of the same job's relic satisfies it while
    // crediting nothing.
    public static bool UnfinishedRelicEquipped()
    {
        var im = InventoryManager.Instance();
        var c = im == null ? null : im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return false;
        for (var i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand
        {
            var s = c->GetInventorySlot(i);
            if (s != null && s->ItemId != 0 && RelicWeaponStages.IsUnfinishedForm(s->ItemId))
                return true;
        }
        return false;
    }

    // True when an "Unfinished <weapon>" is held ANYWHERE (hands, armoury weapon slots, bags).
    // The form only exists between Gerolt forging it (sequence 9) and taking it back (14), so
    // holding one is self-evident proof that the base-relic quest is mid-flight -- which is how
    // the equip step knows to insist on it without any caller having to say so.
    public static bool HoldsUnfinishedRelic()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        foreach (var bag in ZenithSearchContainers)
        {
            var c = im->GetInventoryContainer(bag);
            // EquippedItems reads without the IsLoaded gate, as every other scan here does.
            if (c == null || (bag != InventoryType.EquippedItems && !c->IsLoaded))
                continue;
            var max = bag == InventoryType.EquippedItems ? 2 : c->Size;
            for (var i = 0; i < max; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && RelicWeaponStages.IsUnfinishedForm(s->ItemId))
                    return true;
            }
        }
        return false;
    }

    // Containers an unequipped relic weapon can sit in: the armoury weapon slots (where a weapon
    // lands when swapped off) first, then the four bags. ArmoryOffHand is included for the
    // Paladin's Holy Shield, which is a relic in its own right and lives in that container.
    private static readonly InventoryType[] RelicSearchContainers =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    // Find a known relic weapon that is NOT currently equipped (sitting in the armoury chest or a
    // bag), so the caller can equip it before a duty or the beastman hunt. False if none is found.
    //
    // THE CURRENT JOB'S RELIC WINS, and that is the whole reason this is not a plain first-match.
    // A relic can only be equipped by the job it belongs to, and the game refuses the move
    // silently -- no error, nothing in the log. So on a character with more than one relic in
    // flight (or one finished relic parked in the armoury while a second job is levelling its
    // own), scanning "the first relic weapon found" would keep handing the equip another job's
    // weapon, the move would be ignored, and the hunt would run with an empty main hand while the
    // kills quietly failed to credit. Scanning the current job's weapons first fixes that; the
    // job-agnostic pass is kept as a fallback for the ids that carry no job mapping (the il125
    // Braves and il135 Zeta finals) and for an unrecognized class.
    // THE UNFINISHED FORM WINS over any other tier of the same job's relic. During 'A Relic Reborn'
    // the quest credits kills ONLY to the "Unfinished <weapon>" Gerolt forges at sequence 9 -- but a
    // finished relic, a Zenith or an Atma of the SAME job is equippable and reads as a relic just as
    // happily, so on a repeat run (or with an older relic parked in the armoury) the hunt would arm
    // the wrong weapon, complete its equip step, and cull 24 beastmen that credit nothing. Holding
    // an Unfinished form at all means the base-relic quest is mid-flight, since it exists only
    // between sequence 9 and the hand-back at 14 -- so no caller has to say which weapon it wants.
    public static bool TryFindRelicInBags(out InventoryType container, out ushort slot)
    {
        // Resolve the job the way the controller does, NOT from the raw ClassJob id: Arcanist (26)
        // is ambiguous and FromClassJobId returns None for it. None means "accept any relic weapon"
        // below, so on a Summoner reading as Arcanist -- see BaseRelicState.ActiveRelicJob -- this
        // handed the equip whatever relic sorted first, including another job's. The game refuses a
        // wrong-job equip SILENTLY, so the hunt then ran with the old weapon in hand and the kills
        // quietly failed to credit.
        var job = BaseRelic.BaseRelicState.ActiveRelicJob();
        if (job != RelicJob.None)
        {
            if (TryFindRelicInBags(job, unfinishedOnly: true, out container, out slot))
                return true;
            if (TryFindRelicInBags(job, unfinishedOnly: false, out container, out slot))
                return true;
        }
        // Job-agnostic fallback, for the ids that carry no job mapping (the il125 Braves and il135
        // Zeta finals) and for a class we do not recognize.
        return TryFindRelicInBags(RelicJob.None, unfinishedOnly: false, out container, out slot);
    }

    // requiredJob None = accept any relic weapon; otherwise only ones that job can equip.
    // unfinishedOnly restricts the match to the "Unfinished <weapon>" form.
    private static bool TryFindRelicInBags(RelicJob requiredJob, bool unfinishedOnly,
        out InventoryType container, out ushort slot)
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
                if (!IsRelicWeaponId(s->ItemId))
                    continue;
                if (unfinishedOnly && !RelicWeaponStages.IsUnfinishedForm(s->ItemId))
                    continue;
                if (requiredJob != RelicJob.None && RelicWeaponStages.JobOf(s->ItemId) != requiredJob)
                    continue;
                container = bag;
                slot = (ushort)i;
                return true;
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

    // The bare base relics currently IN THE HANDS, as (equip slot, item id) -- 0 = main hand,
    // 1 = off hand. This is the exact set the Zenith step has to trade: every solo main hand is
    // one 3-mist trade, and the Paladin's Curtana + Holy Shield are two separate trades (2 + 1)
    // that must BOTH happen, so the step works from the pair rather than from a single weapon.
    public static List<(ushort Slot, uint ItemId)> EquippedZenithPendingWeapons()
    {
        var found = new List<(ushort, uint)>();
        var im = InventoryManager.Instance();
        if (im == null)
            return found;
        var c = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null)
            return found;
        for (ushort i = 0; i <= 1; i++) // 0 = main hand, 1 = off hand (Holy Shield)
        {
            var slot = c->GetInventorySlot(i);
            if (slot != null && RelicWeaponStages.IsBareBaseRelic(slot->ItemId))
                found.Add((i, slot->ItemId));
        }
        return found;
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

    // Get the weapon in an equipped slot (0 main / 1 off) OFF the character, so it counts as
    // UNEQUIPPED -- Jalzahn's enhancements and the quest hand-overs list only unequipped relics.
    // False when nothing could be moved.
    //
    // THE MAIN HAND CANNOT BE EMPTIED. This used to move the weapon out to a free armoury/bag slot
    // for both hands, which is correct for the off hand and IMPOSSIBLE for the main one: FFXIV has
    // no bare-handed state, so the server simply refuses the move. It failed silently -- no error,
    // nothing in the log, MoveItemSlot does not report it -- so the relic stayed on, the trade
    // window listed nothing, and the step waited out its timer. That is the reported "cannot
    // unequip a weapon".
    //
    // The main hand therefore SWAPS instead: put another weapon on and the relic is displaced into
    // the armoury, which is the same end state. See TryFindSwapWeapon for how the replacement is
    // chosen. The off hand keeps the move-out (an empty off hand is legal), and falls back to a
    // swap if there is nowhere to put it.
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
        var wornId = src->ItemId;

        // Off hand first: it can legally be left empty, so the plain move-out is tried for it.
        if (equipSlot == 1 && TryMoveOutOfHands(im, equipSlot))
            return true;

        // Main hand (or an off hand with nowhere to go): displace it by equipping something else.
        if (!TryFindSwapWeapon(wornId, equipSlot, out var container, out var slot, out var swapId))
        {
            DebugLog.Warn($"Cannot take off '{ItemName(wornId)}': this job owns no other " +
                          $"{(equipSlot == 1 ? "off-hand item" : "weapon")} to put on in its place, and the game " +
                          "does not allow an empty main hand. Buy or retrieve any other weapon for this job " +
                          "(a vendor one is fine) and run it again.");
            return false;
        }

        try
        {
            im->MoveItemSlot(container, slot, InventoryType.EquippedItems, equipSlot);
            DebugLog.Info($"Swapped '{ItemName(swapId)}' into the {(equipSlot == 1 ? "off" : "main")} hand so " +
                          $"'{ItemName(wornId)}' comes off (the main hand cannot be emptied).");
            return true;
        }
        catch { return false; }
    }

    // The original behaviour: move the equipped item out to the first free armoury/bag slot. Only
    // legal for the off hand.
    private static bool TryMoveOutOfHands(InventoryManager* im, ushort equipSlot)
    {
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

    // Containers a stand-in weapon can be pulled from: the armoury chest slot for that hand first
    // (where spare weapons live), then the bags.
    private static readonly InventoryType[] SwapSourcesMain =
    {
        InventoryType.ArmoryMainHand,
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    private static readonly InventoryType[] SwapSourcesOff =
    {
        InventoryType.ArmoryOffHand,
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    // Pick a NON-relic item this job can wear in `equipSlot`, to stand in while the relic is off.
    //
    // The EquipSlotCategory must match the relic's exactly (a shield cannot stand in for a sword,
    // and a two-handed weapon cannot stand in for a one-handed one). The JOB test is a flag lookup
    // on the candidate's ClassJobCategory, not a comparison against the relic's own category --
    // that shortcut looks right and is not: a relic carries the job-only category (Curtana is
    // "PLD", row 20) while ordinary gear carries the class+job one ("GLA PLD", row 38), so
    // comparing the two rows matches nothing and no swap would ever be found.
    //
    // Getting this wrong is silent: the game refuses a wrong-job equip without an error (the same
    // trap documented on TryFindRelicInBags), so a bad pick would leave the relic on with nothing
    // to show for it. The highest item level wins, so if a restore ever fails the character is left
    // holding the best weapon it owns rather than a level-1 one.
    private static bool TryFindSwapWeapon(uint wornId, ushort equipSlot,
        out InventoryType container, out ushort slot, out uint itemId)
    {
        container = default;
        slot = 0;
        itemId = 0;

        var items = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (items.GetRowOrDefault(wornId) is not { } worn)
            return false;
        var wantSlot = worn.EquipSlotCategory.RowId;
        var fallbackJob = worn.ClassJobCategory.RowId;
        var job = ActiveClassJobId();
        var level = ActiveJobLevel();

        var im = InventoryManager.Instance();
        if (im == null)
            return false;

        var bestIlvl = -1;
        foreach (var bag in equipSlot == 1 ? SwapSourcesOff : SwapSourcesMain)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null || !c->IsLoaded)
                continue;
            for (ushort i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || s->ItemId == wornId)
                    continue;
                // Never stand in with another relic: it would be the next stage's turn-in item, or
                // another job's line, and either way it is not a neutral placeholder.
                if (IsRelicWeaponId(s->ItemId))
                    continue;
                if (items.GetRowOrDefault(s->ItemId) is not { } cand)
                    continue;
                if (cand.EquipSlotCategory.RowId != wantSlot)
                    continue;
                // Unknown job -> fall back to the relic's own category, which is at least never wrong.
                if (!(CategoryAllowsJob(cand.ClassJobCategory.RowId, job)
                      ?? cand.ClassJobCategory.RowId == fallbackJob))
                    continue;
                if (level > 0 && cand.LevelEquip > level)
                    continue;
                var ilvl = (int)cand.LevelItem.RowId;
                if (ilvl <= bestIlvl)
                    continue;
                bestIlvl = ilvl;
                container = bag;
                slot = i;
                itemId = s->ItemId;
            }
        }
        return itemId != 0;
    }

    // Does a ClassJobCategory permit this ClassJob? Null when the job is not one we map, so the
    // caller can fall back rather than treat "unknown" as "no".
    //
    // The category rows carry one flag per class/job abbreviation ("GLA PLD" sets GLA and PLD;
    // "Disciple of War" sets nineteen), so the test is simply the flag for the job we are on. It is
    // done through the typed properties rather than by parsing the category's NAME because that
    // name is localised -- a non-English client would match nothing, the same way the hardcoded
    // English materia names once did.
    private static bool? CategoryAllowsJob(uint categoryRow, uint classJobId)
    {
        if (categoryRow == 0 || classJobId == 0)
            return null;
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJobCategory>()
                .GetRowOrDefault(categoryRow) is not { } c)
            return null;
        // Both the base class and the job, so this holds whichever the character is on.
        return classJobId switch
        {
            1 => c.GLA, 19 => c.PLD,
            3 => c.MRD, 21 => c.WAR,
            2 => c.PGL, 20 => c.MNK,
            4 => c.LNC, 22 => c.DRG,
            29 => c.ROG, 30 => c.NIN,
            5 => c.ARC, 23 => c.BRD,
            7 => c.THM, 25 => c.BLM,
            26 => c.ACN, 27 => c.SMN, 28 => c.SCH,
            6 => c.CNJ, 24 => c.WHM,
            _ => null,
        };
    }

    // Best-effort equip of a weapon from a bag/armoury slot into the main hand. MoveItemSlot is
    // the standard bag->equipped path (the documented crash is retainer<->player only); wrapped
    // so a failure cannot take down the run. The caller verifies via EquippedRelicStage.
    public static void TryEquipFromBag(InventoryType container, ushort slot)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return;
        // Which hand the item belongs in. This used to be hardcoded to the MAIN hand at every call
        // site, which is wrong for the Paladin's Holy Shield -- the one relic that lives in the off
        // hand. The game refuses a shield sent to the main hand silently, so every "put it back"
        // path looked like it ran while the shield stayed off.
        var src = im->GetInventoryContainer(container);
        var item = src == null ? null : src->GetInventorySlot(slot);
        var dest = item == null ? (ushort)0 : EquipSlotForItem(item->ItemId);
        try { im->MoveItemSlot(container, slot, InventoryType.EquippedItems, dest); }
        catch { /* best-effort; verified by the caller */ }
    }

    // The equipped-container slot an item belongs to: 1 for something that only goes in the off
    // hand, 0 otherwise. Only the two weapon slots matter here -- nothing in the relic line equips
    // armour or accessories.
    private static ushort EquipSlotForItem(uint itemId)
    {
        if (itemId == 0)
            return 0;
        try
        {
            var cat = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                .GetRowOrDefault(itemId)?.EquipSlotCategory.ValueNullable;
            if (cat is { } c && c.OffHand > 0 && c.MainHand <= 0)
                return 1;
        }
        catch { /* fall through to the main hand */ }
        return 0;
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
