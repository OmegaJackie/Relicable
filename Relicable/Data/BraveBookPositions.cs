using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace Relicable.Data;

// Authored Trials of the Braves target coordinates, transcribed from the in-game
// Trials of the Braves book and verified against the live spawns.
//
// The game sheets do NOT contain exact monster or FATE spawn coordinates (see the
// note in Locations.cs), so this table hardcodes one map coordinate per
// MonsterNoteTarget row and per Fate row. Relicable navigates to that coordinate and
// auto-places a map flag there, so no manual flag is needed.
//
// The table stores human-readable MAP coordinates (the values shown in-game). Each
// is converted to a WORLD position with Dalamud's MapLinkPayload, whose RawX/RawY
// are world units multiplied by 1000 (world = Raw / 1000). vnavmesh and the rest of
// Relicable operate in that world space.
public static class BraveBookPositions
{
    // Territory + Map row ids plus the in-game map X/Y for a single target.
    public readonly record struct MapCoord(ushort Territory, ushort Map, float X, float Y);

    // Key: MonsterNoteTarget row id. These match RelicNote.MonsterNoteTargetCommon
    // (and ...NM for dungeon bosses), i.e. the targetRef.RowId the generator reads.
    public static readonly IReadOnlyDictionary<uint, MapCoord> Monsters = new Dictionary<uint, MapCoord>
    {
        // Common enemies (used by the kill-grind generator).
        [356] = new(152, 5, 29.1f, 15.3f),
        [357] = new(156, 25, 17.0f, 16.0f),
        [358] = new(155, 53, 13.8f, 27.0f),
        [359] = new(138, 18, 17.7f, 16.3f),
        [360] = new(156, 25, 11.0f, 15.1f),
        [361] = new(180, 30, 23.9f, 7.7f),
        [362] = new(140, 20, 11.0f, 6.2f),
        [363] = new(146, 23, 21.5f, 25.2f),
        [364] = new(147, 24, 22.1f, 26.6f),
        [365] = new(137, 17, 29.5f, 20.8f),
        [366] = new(156, 25, 17.0f, 16.0f),
        [367] = new(155, 53, 13.8f, 30.5f),
        [368] = new(138, 18, 17.6f, 16.0f),
        [369] = new(146, 23, 21.9f, 18.7f),
        [370] = new(154, 7, 22.6f, 20.0f),
        [371] = new(152, 5, 24.2f, 16.9f),
        [372] = new(138, 18, 13.8f, 16.9f),
        [373] = new(146, 23, 26.1f, 21.1f),
        [374] = new(180, 30, 27.4f, 7.2f),
        [375] = new(155, 53, 33.9f, 21.6f),
        [376] = new(153, 6, 30.8f, 24.8f),
        [377] = new(156, 25, 17.0f, 16.0f),
        [378] = new(155, 53, 13.8f, 27.0f),
        [379] = new(146, 23, 21.9f, 18.7f),
        [380] = new(156, 25, 29.6f, 14.3f),
        [381] = new(180, 30, 23.9f, 7.7f),
        [382] = new(152, 5, 25.7f, 13.3f),
        [383] = new(138, 18, 13.4f, 16.9f),
        [384] = new(146, 23, 26.1f, 21.1f),
        [385] = new(137, 17, 29.5f, 20.8f),
        [386] = new(138, 18, 18.1f, 19.9f),
        [387] = new(156, 25, 14.1f, 11.0f),
        [388] = new(146, 23, 18.9f, 22.9f),
        [389] = new(156, 25, 25.3f, 10.9f),
        [390] = new(155, 53, 13.8f, 27.0f),
        [391] = new(180, 30, 23.9f, 7.7f),
        [392] = new(152, 5, 24.6f, 11.2f),
        [393] = new(138, 18, 14.4f, 17.0f),
        [394] = new(147, 24, 18.0f, 16.9f),
        [395] = new(137, 17, 29.5f, 20.8f),
        [396] = new(152, 5, 29.1f, 12.4f),
        [397] = new(146, 23, 16.4f, 23.7f),
        [398] = new(156, 25, 11.4f, 12.9f),
        [399] = new(155, 53, 13.8f, 30.5f),
        [400] = new(153, 6, 33.3f, 23.7f),
        [401] = new(180, 30, 23.9f, 7.7f),
        [402] = new(138, 18, 13.4f, 16.9f),
        [403] = new(140, 20, 11.0f, 6.2f),
        [404] = new(156, 25, 33.4f, 15.2f),
        [405] = new(138, 18, 14.5f, 14.0f),
        [406] = new(146, 23, 18.9f, 22.9f),
        [407] = new(153, 6, 33.3f, 23.7f),
        [408] = new(156, 25, 11.4f, 12.9f),
        [409] = new(156, 25, 28.9f, 13.6f),
        [410] = new(154, 7, 20.2f, 19.6f),
        [411] = new(180, 30, 23.9f, 7.7f),
        [412] = new(140, 20, 11.0f, 6.2f),
        [413] = new(155, 53, 33.9f, 21.6f),
        [414] = new(152, 5, 24.6f, 11.2f),
        [415] = new(138, 18, 16.3f, 14.9f),
        [416] = new(152, 5, 29.1f, 12.4f),
        [417] = new(146, 23, 18.9f, 22.9f),
        [418] = new(156, 25, 11.4f, 12.9f),
        [419] = new(138, 18, 16.3f, 14.9f),
        [420] = new(156, 25, 28.7f, 6.9f),
        [421] = new(138, 18, 20.4f, 19.1f),
        [422] = new(155, 53, 33.9f, 21.6f),
        [423] = new(180, 30, 23.9f, 7.7f),
        [424] = new(147, 24, 24.8f, 20.8f),
        [425] = new(137, 17, 29.5f, 20.8f),
        [426] = new(156, 25, 31.4f, 14.0f),
        [427] = new(156, 25, 11.4f, 12.9f),
        [428] = new(152, 5, 28.2f, 17.2f),
        [429] = new(154, 7, 20.2f, 19.6f),
        [430] = new(146, 23, 18.9f, 22.9f),
        [431] = new(140, 20, 10.2f, 6.0f),
        [432] = new(146, 23, 31.1f, 19.5f),
        [433] = new(138, 18, 16.3f, 14.9f),
        [434] = new(155, 53, 33.9f, 21.6f),
        [435] = new(180, 30, 23.9f, 7.7f),
        [436] = new(146, 23, 18.9f, 22.9f),
        [437] = new(156, 25, 11.4f, 12.9f),
        [438] = new(154, 7, 20.2f, 19.6f),
        [439] = new(146, 23, 31.1f, 19.5f),
        [440] = new(138, 18, 13.9f, 15.5f),
        [441] = new(156, 25, 31.0f, 5.6f),
        [442] = new(180, 30, 23.9f, 7.7f),
        [443] = new(155, 53, 33.9f, 21.6f),
        [444] = new(152, 5, 23.8f, 14.6f),
        [445] = new(137, 17, 29.5f, 20.8f),
        // Notorious monsters (dungeon bosses). Only the Territory field is read for these
        // (MonsterTerritory -> RelicNoteDataGenerator.ResolveDungeonTerritory); the map coord is
        // unused (book dungeons are entered through AutoDuty, which travels itself), so only the
        // TerritoryType has to be current.
        //
        // The 6.1 "Duty Support" ARR revamp REASSIGNED the TerritoryType of five normal dungeons
        // from their launch ids (162/163/170/171/172, now retired or repurposed) to new ids. The
        // authored table still had the launch ids, so those five NM slots resolved to a
        // non-dungeon territory and were SILENTLY SKIPPED by the generator: the book then looked
        // complete and the engine tried to buy the next book (the reported "Sunken Temple of Qarn
        // is not picked up; it buys the next book"). Corrected to the live dungeon ids inline.
        // Source of truth: the boss's PlaceNameLocation names the dungeon; ContentFinderCondition
        // (ContentType 2) gives that dungeon's current TerritoryType.
        [446] = new(1037, 8, 6.8f, 7.6f),
        [447] = new(1042, 37, 11.2f, 6.3f),
        [448] = new(363, 152, 11.2f, 11.2f),
        [449] = new(1041, 45, 10.6f, 6.5f),
        [450] = new(159, 32, 12.7f, 2.5f),
        [451] = new(349, 142, 9.2f, 11.3f),
        [452] = new(1267, 43, 16.0f, 11.2f), // The Sunken Temple of Qarn (was 163, retired in 6.1)
        [453] = new(350, 138, 11.2f, 11.3f),
        [454] = new(360, 145, 6.1f, 11.6f),
        [455] = new(1038, 41, 9.2f, 11.3f),
        [456] = new(1330, 86, 12.8f, 7.8f), // Dzemael Darkhold (was 171, reassigned in 6.1)
        [457] = new(362, 146, 10.6f, 6.5f),
        [458] = new(1039, 9, 15.6f, 8.30f),
        [459] = new(167, 85, 11.4f, 11.2f),
        [460] = new(1303, 97, 7.7f, 7.2f), // Cutter's Cry (was 170, retired in 6.1)
        [461] = new(160, 134, 11.3f, 11.3f),
        [462] = new(1036, 31, 4.9f, 17.7f),
        [463] = new(1331, 38, 3.1f, 8.7f), // the Aurum Vale (was 172, reassigned in 6.1)
        [464] = new(1040, 54, 11.2f, 11.3f),
        [465] = new(1245, 46, 6.1f, 11.7f), // Halatali (was 162, retired in 6.1)
    };

    // Key: Fate row id. Matches RelicNote.Fate (the fateRef.RowId the generator reads).
    public static readonly IReadOnlyDictionary<uint, MapCoord> Fates = new Dictionary<uint, MapCoord>
    {
        [317] = new(139, 19, 26.8f, 18.2f),
        [424] = new(146, 23, 21.0f, 16.0f),
        [430] = new(146, 23, 24.0f, 26.0f),
        [475] = new(155, 53, 34.0f, 13.0f),
        [480] = new(155, 53, 8.0f, 11.0f),
        [486] = new(155, 53, 10.0f, 28.0f),
        [493] = new(155, 53, 5.0f, 22.0f),
        [499] = new(155, 53, 34.0f, 20.0f),
        [516] = new(156, 25, 15.0f, 13.0f),
        // Good to Be Bud (Mor Dhona, the Fogfens / The Tangle marsh). The older rounded (13.0,
        // 12.0) sat ~0.6 map units off, over the Tangle's central pond -- a non-landable water column
        // where the floor probe finds no navmesh, so the staging flag "is in a non-landable area" and
        // the character cannot dismount. Wiki-verified spawn is (13.6, 12.1), on the walkable marsh.
        [517] = new(156, 25, 13.6f, 12.1f),
        [521] = new(156, 25, 31.0f, 5.0f),
        [540] = new(145, 22, 26.0f, 24.0f),
        // The Big Bagoly Theory (Eastern Thanalan). User-verified spawn (30.1, 25.6); earlier values were
        // (30.1, 25.4) and a rounded (30.0, 25.0), which staged short of the ring.
        [543] = new(145, 22, 30.1f, 25.6f),
        [552] = new(146, 23, 18.0f, 20.0f),
        [569] = new(138, 18, 21.0f, 19.0f),
        [571] = new(138, 18, 18.0f, 22.0f),
        [577] = new(138, 18, 14.0f, 34.0f),
        [587] = new(180, 30, 23.8f, 16.4f), // Schism: the Storm Private start-NPC spot (user-provided, was rounded 25.0/16.0)
        [589] = new(180, 30, 25.0f, 17.0f),
        [604] = new(148, 4, 11.0f, 18.0f),
        [611] = new(152, 5, 27.0f, 21.0f),
        [616] = new(152, 5, 32.0f, 14.0f),
        [620] = new(152, 5, 23.0f, 14.0f),
        [628] = new(153, 6, 32.0f, 25.0f),
        // Rude Awakening. This table first held (21.0, 19.0), but the FATE actually spawns at the
        // wiki-verified (22.0, 20.0). The old spot sat almost on top of Air Supply (633) at (19.0,
        // 20.0) -- the two North Shroud boss FATEs are only ~3 map units apart -- which fed the
        // "getting me stuck in a separate fate" report (see also the authored-spot staging in
        // ParticipateFateExecutor, the primary cause).
        [632] = new(154, 7, 22.0f, 20.0f),
        [633] = new(154, 7, 19.0f, 20.0f),
        [642] = new(147, 24, 21.0f, 29.0f),

        // ---- Prerequisite FATEs (NOT book slots; see FatePrerequisite) ----
        // A few book FATEs do not spawn until a PREDECESSOR overworld FATE is cleared. Those
        // predecessors are not RelicNote slots, so they have no generated objective; the executor
        // drives them via ParticipateFateExecutor, staging at these coords. Coords for the two Tidegate
        // "Gauging" FATEs are approximate (their zone is W. La Noscea, near their "Breaching" pair);
        // the executor homes on the live FATE position once it streams in, so an approximate stage is
        // sufficient. 610 (The Enemy of My Enemy) is the wiki-verified East Shroud Larkscall spot -- and
        // is where the standing BNpc "Mianne Thousandmalm" who SPAWNS it stands (see FateSpawnerNpc: 610
        // is NPC-SPAWNED, not in the FATE table until she is talked to, so the executor must interact
        // with her here to spawn it). VERIFY the Gauging coords in-game.
        [568] = new(138, 18, 20.0f, 19.0f),  // Gauging North Tidegate -> 569 Breaching North Tidegate
        [570] = new(138, 18, 18.0f, 22.0f),  // Gauging South Tidegate -> 571 Breaching South Tidegate
        [610] = new(152, 5, 27.0f, 21.0f),   // The Enemy of My Enemy  -> 611 The Enmity of My Enemy
    };

    // Book FATE (target) -> the predecessor FATE that must be cleared before it spawns. The predecessor
    // is an overworld FATE, NOT a book slot, so it has no objective of its own; ParticipateFateExecutor
    // drives it when the target is absent. Verified against the console wiki's "Notes -> For FATEs".
    public static readonly IReadOnlyDictionary<uint, uint> FatePrerequisite = new Dictionary<uint, uint>
    {
        [569] = 568, // Breaching North Tidegate needs Gauging North Tidegate
        [571] = 570, // Breaching South Tidegate needs Gauging South Tidegate
        [611] = 610, // The Enmity of My Enemy needs The Enemy of My Enemy (610 is NPC-SPAWNED; see FateSpawnerNpc)
    };

    // The predecessor FATE id for a book FATE, or 0 if it is not gated.
    public static uint PrerequisiteFate(uint fateId)
        => FatePrerequisite.TryGetValue(fateId, out var p) ? p : 0u;

    // A prerequisite FATE (above) that is NPC-SPAWNED, not merely NPC-INITIATED: it does NOT appear in
    // the live FATE table at all until a STANDING NPC is talked to (which spawns it). That NPC has no
    // MotivationNpc link on a (non-existent) FATE, so ParticipateFateExecutor's DriveFateStart cannot
    // fire for it -- the stage-and-wait would idle forever. The value is the spawner NPC's object-table
    // NAME: these are BNpcs whose object BaseId is a BNpcBase id (NOT the BNpcName id), so they must be
    // found by name, not DataId. The executor travels to the FATE's staging coord (Fates[fateId], where
    // the NPC stands), finds the NPC by name, and interacts + accepts the Yes/No to spawn the FATE, then
    // resumes the normal prereq flow. Empty for a prereq that spawns on its own (e.g. the Gauging Tidegates).
    public static readonly IReadOnlyDictionary<uint, string> FateSpawnerNpc = new Dictionary<uint, string>
    {
        // 610 "The Enemy of My Enemy" (East Shroud, Larkscall) is spawned by talking to the standing BNpc
        // "Mianne Thousandmalm" and accepting her proposal (a Yes/No); clearing 610 then spawns the book
        // target 611 "The Enmity of My Enemy" (in which Mianne is the defend-target). She is present when
        // 610 is on its rotation, so the executor rotates to another objective and retries if she is absent.
        [610] = "Mianne Thousandmalm",
    };

    // The spawner NPC name for an NPC-SPAWNED prerequisite FATE, or null when the FATE spawns on its own.
    public static string? SpawnerNpcForFate(uint fateId)
        => FateSpawnerNpc.TryGetValue(fateId, out var name) ? name : null;

    // Exact vnavmesh WORLD positions for specific FATEs, captured in-game (the player's vnav position
    // standing at the FATE), used VERBATIM as the navigation target -- bypassing the map-coordinate
    // conversion above, which rounds to 0.1 map units (~5 yalms) and can stage a little off the ring.
    // Y is the real height but only advisory: LandableFloorForMapPoint re-probes the floor from XZ. The
    // map-coord Fates entry is still required (FateTerritory reads the zone from it), so keep BOTH; this
    // table just wins for FateWorld. Key: Fate row id.
    public static readonly IReadOnlyDictionary<uint, Vector3> FatesWorld = new Dictionary<uint, Vector3>
    {
        // The Big Bagoly Theory (543), Eastern Thanalan -- vnav player position at the ring.
        [543] = new(435.709f, -64.581f, 206.361f),
    };

    private static readonly Dictionary<uint, Vector3?> MonsterWorldCache = new();
    private static readonly Dictionary<uint, Vector3?> FateWorldCache = new();

    // Authored territory for a monster target, or 0 if not in the table.
    public static uint MonsterTerritory(uint monsterNoteTargetId)
        => Monsters.TryGetValue(monsterNoteTargetId, out var c) ? c.Territory : 0u;

    // Authored territory for a FATE, or 0 if not in the table.
    public static uint FateTerritory(uint fateId)
        => Fates.TryGetValue(fateId, out var c) ? c.Territory : 0u;

    // World position for a monster target, or null if not in the table / conversion
    // failed. Y is 0; callers snap to the navmesh floor (vnavmesh resolves height).
    public static Vector3? MonsterWorld(uint monsterNoteTargetId)
        => World(monsterNoteTargetId, Monsters, MonsterWorldCache);

    // World position for a FATE, or null if not in the table / conversion failed. Prefers an exact
    // vnav-captured world position (FatesWorld) over the rounded map-coordinate conversion.
    public static Vector3? FateWorld(uint fateId)
        => FatesWorld.TryGetValue(fateId, out var w) ? w : World(fateId, Fates, FateWorldCache);

    private static Vector3? World(uint id, IReadOnlyDictionary<uint, MapCoord> table, Dictionary<uint, Vector3?> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        Vector3? result = null;
        if (table.TryGetValue(id, out var c))
        {
            try
            {
                // MapLinkPayload converts the human map coordinate to the internal
                // raw position using the map's SizeFactor/Offset. World = Raw / 1000.
                var link = new MapLinkPayload(c.Territory, c.Map, c.X, c.Y);
                result = new Vector3(link.RawX / 1000f, 0f, link.RawY / 1000f);
            }
            catch
            {
                result = null;
            }
        }

        cache[id] = result;
        return result;
    }
}
