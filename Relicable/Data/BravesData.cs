using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// How a Braves (il125 Zodiac) material is obtained. The planner prices everything it
// can on the market board regardless, but the source drives the shopping-list grouping
// and the "what it costs in a non-gil currency / must be farmed" notes.
public enum BravesSource
{
    Craft,          // HQ level-50 3-star craft (or buy the HQ on the market board)
    DesynthSource,  // 3,000 gil item bought only to desynthesize for a craft ingredient
    VendorGil,      // 100,000 gil zone-vendor item
    VendorSeals,    // Grand Company seal item (Bombard Core)
    VendorPoetics,  // Allagan Tomestone of Poetics item (Sacred Spring Water)
    DungeonDrop,    // quest reward dropped in a dungeon (untradable; must be farmed)
}

// One material required somewhere in the Braves stage. Names are the English Item-sheet
// names, resolved to ids at runtime by BravesData (so nothing is hardcoded to a numeric
// id). FixedCost is the per-unit cost in the source's native currency (gil for VendorGil
// and DesynthSource, Company Seals for VendorSeals, Poetics for VendorPoetics); 0 for
// Craft and DungeonDrop.
public sealed class BravesMaterial
{
    public string ItemName { get; init; } = string.Empty;
    public int Quantity { get; init; } = 1;
    public BravesSource Source { get; init; }
    public long FixedCost { get; init; }
    public string Quest { get; init; } = string.Empty;   // which material quest needs it
    public string Where { get; init; } = string.Empty;   // vendor location or dungeon name
    public string CraftJob { get; init; } = string.Empty;     // Craft only
    public string DesynthFrom { get; init; } = string.Empty;  // Craft only: the Aged item to desynth

    // Optional vendor location for the map pin + teleport (0 territory = no clickable location).
    public uint Territory { get; init; }
    public float MapX { get; init; }
    public float MapY { get; init; }

    // DungeonDrop only: the live quest sequence(s) (GameState.QuestSequence(this quest)) at which the
    // quest actually REQUESTS this drop. The material quests batch their dungeon items across steps,
    // so the drop only drops while the quest is at its step. Empty = uncalibrated (run whenever the
    // quest is accepted). Calibrate in-game with /relic bravesseq, then fill these in.
    public IReadOnlyList<int> RequestedAtSequences { get; init; } = new int[0];
}

// Static content + id resolution for the Zodiac Braves (il125) stage shopping list,
// transcribed from the FFXIV Console Games Wiki (Zodiac_Braves_Weapons/Quest). The stage
// is: "Wherefore Art Thou, Zodiac" -> four repeatable material quests (A Ponze of Flesh,
// Labor of Love, Method in His Malice, A Treasured Mother) -> "His Dark Materia" ->
// Jalzahn's (guaranteed) Zodiac Weapon Recreation. Each material quest consumes:
//   1 Bombard Core (20,000 GC seals), 1 Sacred Spring Water (200 Poetics), 1 zone-vendor
//   item (100,000 gil), 2 HQ level-50 3-star crafted items, and 4 dungeon drops.
// Crafting and desynthesis are out of scope for this combat/duty plugin, so the planner
// prices the craftables (and anything else) on the market board for a buy-it-all total
// and otherwise reports the native-currency / dungeon-farm requirement.
public static class BravesData
{
    public const string QuestPonze = "A Ponze of Flesh";
    public const string QuestLabor = "Labor of Love";
    public const string QuestMethod = "Method in His Malice";
    public const string QuestMother = "A Treasured Mother";

    public const long GilVendorCost = 100_000;
    public const long SealsPerCore = 20_000;
    public const long PoeticsPerWater = 200;
    public const long DesynthSourceGil = 3_000;

    // ARR overworld TerritoryType ids for the vendor locations (map pin / teleport).
    private const uint UpperLaNoscea = 139;
    private const uint SouthernThanalan = 146;
    private const uint CoerthasCentralHighlands = 155;
    private const uint NorthShroud = 154;
    private const uint MorDhona = 156;
    private const uint WesternThanalan = 140;

    public static readonly IReadOnlyList<BravesMaterial> Materials = new[]
    {
        // ---- Grand Company seals: 4x Bombard Core (one per material quest) ----
        new BravesMaterial
        {
            ItemName = "Bombard Core", Quantity = 4, Source = BravesSource.VendorSeals,
            FixedCost = SealsPerCore, Quest = "all four material quests",
            Where = "Grand Company Quartermaster (Second Lieutenant or higher)",
        },

        // ---- Poetics: 4x Sacred Spring Water (one per material quest) ----
        new BravesMaterial
        {
            ItemName = "Sacred Spring Water", Quantity = 4, Source = BravesSource.VendorPoetics,
            FixedCost = PoeticsPerWater, Quest = "all four material quests",
            Where = "any Rowena's Representative, or Hismena / Auriana at Revenant's Toll, Mor Dhona (Poetics)",
            Territory = MorDhona, MapX = 22.7f, MapY = 6.7f,
        },

        // ---- Gil vendor items: 100,000 gil each, one per material quest ----
        new BravesMaterial
        {
            ItemName = "Bronze Lake Crystal", Quantity = 1, Source = BravesSource.VendorGil,
            FixedCost = GilVendorCost, Quest = QuestPonze,
            Where = "Junkmonger, Jijiroon's Trading Post, Upper La Noscea (X:26.1 Y:26.4)",
            Territory = UpperLaNoscea, MapX = 26.1f, MapY = 26.4f,
        },
        new BravesMaterial
        {
            ItemName = "Allagan Resin", Quantity = 1, Source = BravesSource.VendorGil,
            FixedCost = GilVendorCost, Quest = QuestLabor,
            Where = "Merchant & Mender, Forgotten Springs, Southern Thanalan (X:15.9 Y:29.0)",
            Territory = SouthernThanalan, MapX = 15.9f, MapY = 29.0f,
        },
        new BravesMaterial
        {
            ItemName = "Furite Sand", Quantity = 1, Source = BravesSource.VendorGil,
            FixedCost = GilVendorCost, Quest = QuestMethod,
            Where = "Merchant & Mender, Whitebrim Front, Coerthas Central Highlands (X:12.0 Y:16.5)",
            Territory = CoerthasCentralHighlands, MapX = 12.0f, MapY = 16.5f,
        },
        new BravesMaterial
        {
            ItemName = "Brass Kettle", Quantity = 1, Source = BravesSource.VendorGil,
            FixedCost = GilVendorCost, Quest = QuestMother,
            Where = "Tool Supplier & Mender, Hyrstmill, North Shroud (X:30.4 Y:19.7)",
            Territory = NorthShroud, MapX = 30.4f, MapY = 19.7f,
        },

        // ---- Crafted (HQ level-50 3-star) -- 8 items, 2 per material quest ----
        new BravesMaterial { ItemName = "Perfect Firewood", Source = BravesSource.Craft, Quest = QuestPonze,  CraftJob = "Carpenter",     DesynthFrom = "Aged Spear" },
        new BravesMaterial { ItemName = "Furnace Ring",     Source = BravesSource.Craft, Quest = QuestPonze,  CraftJob = "Goldsmith",     DesynthFrom = "Aged Ring" },
        new BravesMaterial { ItemName = "Perfect Pestle",   Source = BravesSource.Craft, Quest = QuestLabor,  CraftJob = "Blacksmith",    DesynthFrom = "Aged Pestle" },
        new BravesMaterial { ItemName = "Perfect Mortar",   Source = BravesSource.Craft, Quest = QuestLabor,  CraftJob = "Armorer",       DesynthFrom = "Aged Mortar" },
        new BravesMaterial { ItemName = "Perfect Vellum",   Source = BravesSource.Craft, Quest = QuestMethod, CraftJob = "Leatherworker", DesynthFrom = "Aged Grimoire" },
        new BravesMaterial { ItemName = "Perfect Pounce",   Source = BravesSource.Craft, Quest = QuestMethod, CraftJob = "Alchemist",     DesynthFrom = "Aged Phial" },
        new BravesMaterial { ItemName = "Perfect Cloth",    Source = BravesSource.Craft, Quest = QuestMother, CraftJob = "Weaver",        DesynthFrom = "Aged Robe" },
        new BravesMaterial { ItemName = "Tailor-made Eel Pie", Source = BravesSource.Craft, Quest = QuestMother, CraftJob = "Culinarian", DesynthFrom = "Aged Decanter" },

        // ---- Desynth source items: 3,000 gil each, only needed if crafting ----
        new BravesMaterial { ItemName = "Aged Spear",    Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestPonze,  Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Aged Spear Shaft)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Ring",     Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestPonze,  Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Aged Eye of Fire)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Pestle",   Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestLabor,  Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Aged Pestle Pieces)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Mortar",   Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestLabor,  Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Aged Mortar Pieces)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Grimoire", Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestMethod, Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Aged Vellum)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Phial",    Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestMethod, Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Dried Ether)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Robe",     Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestMother, Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Stained Cloth)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },
        new BravesMaterial { ItemName = "Aged Decanter", Source = BravesSource.DesynthSource, FixedCost = DesynthSourceGil, Quest = QuestMother, Where = "Merchant & Mender, The Silver Bazaar, Western Thanalan (desynth for Vintage Cooking Sherry)", Territory = WesternThanalan, MapX = 25.0f, MapY = 24.6f },

        // ---- Dungeon drops: 16 total, 4 per material quest (untradable; must be farmed) ----
        // RequestedAtSequences = the live BravesQuest sequence(s) at which the quest actually asks for
        // the drop (calibrated in-game via /relic bravesseq -- see 08:14/08:37 (Ponze), 08:38-08:44
        // (Mother), 11:09 (Method) logs). Ponze bundles obtain+deliver per wiki step, so its dungeon
        // steps land on even sequences (2, 4); Method/Mother count each obtain/deliver, so odd (1,3,5,7 /
        // 3,5). Labor is DERIVED from the same pattern (not yet observed in-game) -- confirm when run.
        new BravesMaterial { ItemName = "Horn of the Beast",   Source = BravesSource.DungeonDrop, Quest = QuestPonze, Where = "Dzemael Darkhold",           RequestedAtSequences = new[] { 2 } },
        new BravesMaterial { ItemName = "Gobmachine Bangplate", Source = BravesSource.DungeonDrop, Quest = QuestPonze, Where = "Brayflox's Longstop (Hard)", RequestedAtSequences = new[] { 2 } },
        new BravesMaterial { ItemName = "Narasimha Hide",      Source = BravesSource.DungeonDrop, Quest = QuestPonze, Where = "Halatali (Hard)",            RequestedAtSequences = new[] { 4 } },
        new BravesMaterial { ItemName = "Sickle Fang",         Source = BravesSource.DungeonDrop, Quest = QuestPonze, Where = "Snowcloak",                  RequestedAtSequences = new[] { 4 } },

        // Labor of Love: CORRECTED from a live /relic bravesseq (2026-07-18). The quest was stuck at
        // sequence 2 with Vale Bubo (seq 2) obtained but Voidweave NOT held, so seq 2 requests BOTH -- the
        // same 2-per-batch shape A Ponze of Flesh was OBSERVED to use (seq 2 pair, seq 4 pair). These quests
        // auto-advance on OBTAINING a batch (Ponze holds its seq-2 pair at seq 3, un-delivered), so a single
        // held item leaving the quest parked proves the batch needs the other one too. The earlier 2/3/5/6
        // "one per sequence" was a bad guess. The seq-4 pair mirrors Ponze; confirm when the quest reaches it.
        new BravesMaterial { ItemName = "Vale Bubo",      Source = BravesSource.DungeonDrop, Quest = QuestLabor, Where = "The Aurum Vale",          RequestedAtSequences = new[] { 2 } },
        new BravesMaterial { ItemName = "Voidweave",      Source = BravesSource.DungeonDrop, Quest = QuestLabor, Where = "Haukke Manor (Hard)",     RequestedAtSequences = new[] { 2 } },
        new BravesMaterial { ItemName = "Amdapor Vellum", Source = BravesSource.DungeonDrop, Quest = QuestLabor, Where = "The Lost City of Amdapor", RequestedAtSequences = new[] { 4 } },
        new BravesMaterial { ItemName = "Indigo Pearl",   Source = BravesSource.DungeonDrop, Quest = QuestLabor, Where = "Sastasha (Hard)",         RequestedAtSequences = new[] { 4 } },

        new BravesMaterial { ItemName = "Tonberry King Blood", Source = BravesSource.DungeonDrop, Quest = QuestMethod, Where = "The Wanderer's Palace",         RequestedAtSequences = new[] { 1 } },
        new BravesMaterial { ItemName = "Royal Gigant Blood",  Source = BravesSource.DungeonDrop, Quest = QuestMethod, Where = "Copperbell Mines (Hard)",        RequestedAtSequences = new[] { 3 } },
        new BravesMaterial { ItemName = "Kraken Blood",        Source = BravesSource.DungeonDrop, Quest = QuestMethod, Where = "Hullbreaker Isle",              RequestedAtSequences = new[] { 5 } },
        new BravesMaterial { ItemName = "Vicegerent Blood",    Source = BravesSource.DungeonDrop, Quest = QuestMethod, Where = "The Sunken Temple of Qarn (Hard)", RequestedAtSequences = new[] { 7 } },

        new BravesMaterial { ItemName = "Lost Treasure of Amdapor",                Source = BravesSource.DungeonDrop, Quest = QuestMother, Where = "Amdapor Keep",                RequestedAtSequences = new[] { 3 } },
        new BravesMaterial { ItemName = "Lost Treasure of Pharos Sirius",          Source = BravesSource.DungeonDrop, Quest = QuestMother, Where = "Pharos Sirius",               RequestedAtSequences = new[] { 3 } },
        new BravesMaterial { ItemName = "Lost Treasure of the Tam-Tara Deepcroft", Source = BravesSource.DungeonDrop, Quest = QuestMother, Where = "The Tam-Tara Deepcroft (Hard)", RequestedAtSequences = new[] { 3 } },
        new BravesMaterial { ItemName = "Lost Treasure of the Stone Vigil",        Source = BravesSource.DungeonDrop, Quest = QuestMother, Where = "The Stone Vigil (Hard)",       RequestedAtSequences = new[] { 5 } },
    };

    private static readonly Dictionary<string, uint> IdByName = new(StringComparer.OrdinalIgnoreCase);
    // The 16 dungeon-drop materials are Key Items (EventItem sheet), which are NOT in the Item
    // sheet and live in the separate KeyItems container. Resolved here so they can be counted via
    // GameState.KeyItemCount -- ItemId()/GetInventoryItemCount always return 0 for them.
    private static readonly Dictionary<string, uint> KeyIdByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Unresolved = new();
    private static bool _resolved;

    // Item-sheet id for a Braves material name, or 0 if it did not resolve (including every
    // Key Item, which is never in the Item sheet -- use KeyItemId for those).
    public static uint ItemId(string name)
    {
        Ensure();
        return IdByName.TryGetValue(name.Trim(), out var id) ? id : 0u;
    }

    // EventItem-sheet id for a Braves material that is a Key Item (the 16 dungeon drops), or 0.
    // Key items live in the KeyItems container and are counted via GameState.KeyItemCount; the
    // normal ItemId()/InventoryCount path never sees them.
    public static uint KeyItemId(string name)
    {
        Ensure();
        return KeyIdByName.TryGetValue(name.Trim(), out var id) ? id : 0u;
    }

    // The exact in-game item name (canonical casing) for an item id, or empty. Used for the
    // planner display and click-to-copy so names match the game / market board exactly.
    // Cached: the window calls this per visible row per frame, and the sheet lookup +
    // SeString decode allocates every time; item names never change within a session.
    private static readonly Dictionary<uint, string> GameNameCache = new();

    public static string GameName(uint itemId)
    {
        if (itemId == 0)
            return string.Empty;
        if (GameNameCache.TryGetValue(itemId, out var cached))
            return cached;
        string name;
        try
        {
            name = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;
        }
        catch { name = string.Empty; }
        GameNameCache[itemId] = name;
        return name;
    }

    // The four repeatable material quests, for detecting which one is active.
    public static readonly IReadOnlyList<string> MaterialQuests = new[] { QuestPonze, QuestLabor, QuestMethod, QuestMother };

    // The NPC you accept AND report back / turn in to for each material quest: its ENpcResident data id,
    // the overworld TerritoryType to teleport to, an approach anchor (world X/Y/Z from Level.csv; the
    // NpcInteractor homes on the live NPC by data id once it streams in, so the anchor only needs to be
    // near it), and a human location. Papana (1010809), Guiding Star (1006971) and Brangwine (1006981)
    // stand at Revenant's Toll (Mor Dhona, T156). Method's Adkin (1010810) resolves to a different zone
    // (T141) in the Level sheet -- a quest may relocate an NPC, so kept as its own zone and flagged; if
    // wrong the trip just fails safe. Drives both the who/where guidance and the auto report/turn-in.
    public static (string Npc, uint DataId, uint Territory, Vector3 Pos, string Where) TurnInNpc(string questName)
        => (questName?.Trim() ?? string.Empty) switch
        {
            QuestPonze  => ("Papana",       1010809, 156, new Vector3(73.1265f, 33.0666f, -704.391f), "Revenant's Toll, Mor Dhona"),
            QuestLabor  => ("Guiding Star", 1006971, 156, new Vector3(24.3083f, 29.0217f, -726.856f), "Revenant's Toll, Mor Dhona"),
            QuestMethod => ("Adkin",        1010810, 141, new Vector3(109.488f, 31f,      -388.829f), "Black Brush Station, Central Thanalan"),
            QuestMother => ("Brangwine",    1006981, 156, new Vector3(25.7269f, 29f,      -738.206f), "Revenant's Toll, Mor Dhona"),
            _ => (string.Empty, 0u, 0u, Vector3.Zero, string.Empty),
        };

    private static Dictionary<string, uint>? _questIds;

    // Quest-sheet row id for a material quest name (case-insensitive), or 0.
    public static uint MaterialQuestId(string questName)
    {
        if (string.IsNullOrWhiteSpace(questName))
            return 0;
        _questIds ??= BuildQuestIds();
        return _questIds.TryGetValue(questName.Trim(), out var id) ? id : 0u;
    }

    private static Dictionary<string, uint> BuildQuestIds()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var q in Plugin.DataManager.GetExcelSheet<Quest>())
            {
                var n = q.Name.ExtractText().Trim();
                if (!string.IsNullOrEmpty(n) && !map.ContainsKey(n))
                    map[n] = q.RowId;
            }
        }
        catch { /* leave empty */ }
        return map;
    }

    // Every resolved material item id, for a single Universalis price fetch.
    public static IReadOnlyList<uint> AllItemIds()
    {
        Ensure();
        return IdByName.Values.Where(v => v != 0).Distinct().ToList();
    }

    // Resolved ids for TRADABLE materials only. Untradable items (the dungeon drops) have no
    // market listings, so fetching them just yields Universalis errors; excluding them keeps
    // the price status clean. Cached: called every frame by the Braves window's EnsurePrices,
    // and tradability is fixed game data.
    private static IReadOnlyList<uint>? _tradableIds;

    public static IReadOnlyList<uint> TradableItemIds()
    {
        if (_tradableIds != null)
            return _tradableIds;
        Ensure();
        var ids = new List<uint>();
        try
        {
            var items = Plugin.DataManager.GetExcelSheet<Item>();
            foreach (var id in IdByName.Values.Where(v => v != 0).Distinct())
                if (items.GetRowOrDefault(id) is { IsUntradable: false })
                    ids.Add(id);
        }
        catch { return AllItemIds(); } // transient failure: do not cache the fallback
        _tradableIds = ids;
        return ids;
    }

    // Tradability check for a single resolved id (backed by the same cached list).
    public static bool IsTradable(uint itemId)
        => itemId != 0 && TradableItemIds().Contains(itemId);

    private static Dictionary<string, uint>? _dungeonCfc;

    // ContentFinderCondition row id for a dungeon name (case-insensitive), or 0. Used to open
    // the Duty Finder for a dungeon-drop material; the CFC row id is what OpenRegularDuty takes.
    public static uint DungeonCfcId(string dungeonName)
    {
        if (string.IsNullOrWhiteSpace(dungeonName))
            return 0;
        _dungeonCfc ??= BuildDungeonCfc();
        return _dungeonCfc.TryGetValue(dungeonName.Trim(), out var id) ? id : 0u;
    }

    private static Dictionary<string, uint> BuildDungeonCfc()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var cfc in Plugin.DataManager.GetExcelSheet<ContentFinderCondition>())
            {
                var n = cfc.Name.ExtractText().Trim();
                if (!string.IsNullOrEmpty(n) && !map.ContainsKey(n))
                    map[n] = cfc.RowId;
            }
        }
        catch { /* leave empty */ }
        return map;
    }

    // Names that did not resolve in the Item sheet (diagnostics; verify in-game).
    public static IReadOnlyList<string> UnresolvedNames()
    {
        Ensure();
        return Unresolved;
    }

    private static Dictionary<uint, ushort>? _recipeByItem;

    // Recipe-sheet row id that produces a given item id (first matching recipe), or 0.
    // Artisan's CraftItem IPC takes a recipe id (ushort), so the planner resolves the
    // craftables' recipes here. Built lazily and cached; recipes above ushort range are
    // skipped (the Braves craftables are all low-id ARR recipes).
    public static ushort RecipeId(uint itemId)
    {
        if (itemId == 0)
            return 0;
        _recipeByItem ??= BuildRecipeMap();
        return _recipeByItem.TryGetValue(itemId, out var r) ? r : (ushort)0;
    }

    private static Dictionary<uint, ushort> BuildRecipeMap()
    {
        var map = new Dictionary<uint, ushort>();
        try
        {
            foreach (var recipe in Plugin.DataManager.GetExcelSheet<Recipe>())
            {
                if (recipe.RowId == 0 || recipe.RowId > ushort.MaxValue)
                    continue;
                var result = recipe.ItemResult.RowId;
                if (result != 0 && !map.ContainsKey(result))
                    map[result] = (ushort)recipe.RowId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Relicable: Braves recipe map failed: {ex.Message}");
        }
        return map;
    }

    private static void Ensure()
    {
        if (_resolved)
            return;
        _resolved = true;

        try
        {
            var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                var n = item.Name.ExtractText().Trim();
                if (!string.IsNullOrEmpty(n) && !byName.ContainsKey(n))
                    byName[n] = item.RowId;
            }

            // The dungeon drops (Horn of the Beast, Sickle Fang, ...) are Key Items, i.e. rows of
            // the EventItem sheet -- not the Item sheet -- so they are resolved separately here.
            var keyByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in Plugin.DataManager.GetExcelSheet<EventItem>())
            {
                var n = ev.Name.ExtractText().Trim();
                if (!string.IsNullOrEmpty(n) && !keyByName.ContainsKey(n))
                    keyByName[n] = ev.RowId;
            }

            foreach (var m in Materials)
            {
                var key = m.ItemName.Trim();
                if (IdByName.ContainsKey(key) || KeyIdByName.ContainsKey(key))
                    continue;
                if (byName.TryGetValue(key, out var id))
                    IdByName[key] = id;
                else if (keyByName.TryGetValue(key, out var kid))
                    KeyIdByName[key] = kid; // a Key Item (dungeon drop): counted via KeyItemCount
                else
                    Unresolved.Add(m.ItemName);
            }

            if (Unresolved.Count > 0)
                Plugin.Log.Warning($"Relicable: Braves catalog has {Unresolved.Count} unresolved name(s): {string.Join(", ", Unresolved)}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Relicable: Braves catalog resolution failed: {ex.Message}");
        }
    }
}
