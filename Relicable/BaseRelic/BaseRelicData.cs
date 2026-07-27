using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Relicable.Model;

namespace Relicable.BaseRelic;

// Static content for the "A Relic Reborn" (base 2-star) stage, transcribed from the
// FFXIV Console Games Wiki (Zodiac_Weapons/Quest). All item names are the English
// Item-sheet names, resolved to ids at runtime by BaseRelicCatalog; all quest names
// are English Quest-sheet titles, resolved by QuestManager lookups in the catalog.
//
// TerritoryType ids feed the deferred in-zone routing pass (teleport-to-zone then
// vnavmesh to the map coordinate); they are stable ARR overworld ids. The map
// coordinates are the values the wiki lists.
public static class BaseRelicData
{
    // The one-time quest that unlocks the relic line (Nedrick Ironheart, Vesper Bay).
    public const string UnlockQuestName = "The Weaponsmith of Legend";

    // The base title of the relic quest. The LIVE quest is per job -- its journal title
    // is "A Relic Reborn (<weapon>)" (e.g. "A Relic Reborn (Curtana)") -- so detection
    // builds the per-job name from this prefix plus the job's relic weapon name. The
    // bare prefix never resolves on its own, which is why a generic lookup failed.
    public const string RelicQuestName = "A Relic Reborn";

    // Gerolt (Hyrstmill, North Shroud) is the relic NPC for every job: the line is accepted
    // from him and every part is reported back to him to advance the quest. These constants
    // let the hunt/trial generator append the report-to-Gerolt turn-in after combat and each
    // trial, so the quest progresses hands-off. DataId and world position match the values
    // the Bard quest-path file (1125) uses.
    public const uint GeroltDataId = 1003075;
    public const uint GeroltTerritory = 154; // North Shroud (Hyrstmill)
    public static readonly Vector3 GeroltPosition = new(440.726f, -0.937455f, -62.1923f);

    // The per-job relic quest title, e.g. "A Relic Reborn (Curtana)". Empty for None.
    public static string RelicQuestNameFor(RelicJob job)
    {
        var data = For(job);
        return data == null ? string.Empty : $"{RelicQuestName} ({data.RelicWeaponName})";
    }

    // Every job's relic quest title, for the catalog to resolve up front.
    public static IEnumerable<string> AllRelicQuestNames()
        => RelicJobs.All.Select(RelicQuestNameFor).Where(s => s.Length > 0);

    // The relic job whose reward weapon matches a name (e.g. "Artemis Bow" -> Bard,
    // "Holy Shield" -> Paladin). None if no match. Used to resolve a quest-path file's
    // job from its filename.
    public static RelicJob JobForRelicWeapon(string weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName))
            return RelicJob.None;
        foreach (var job in RelicJobs.All)
        {
            var d = For(job);
            if (d == null)
                continue;
            if (string.Equals(d.RelicWeaponName, weaponName, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.SecondaryRewardName, weaponName, System.StringComparison.OrdinalIgnoreCase))
                return job;
        }
        return RelicJob.None;
    }

    // ---- TerritoryType ids (stable ARR overworld; confirm during the routing pass) ----
    private const uint WesternThanalan = 140;
    private const uint SouthernThanalan = 146;
    private const uint EasternThanalan = 145;
    private const uint OuterLaNoscea = 180;
    private const uint WesternLaNoscea = 138;
    private const uint EastShroud = 152;
    private const uint NorthShroud = 154;
    private const uint CoerthasCentralHighlands = 155;
    private const uint MorDhona = 156;

    // The ARR main-scenario finale. It gates accepting the relic line IN-GAME, but by design
    // it is NOT one of Relicable's checked/gating prerequisites: it is a general story gate,
    // not a one-time quest directly attached to the Zodiac line. The checker surfaces it as
    // informational context only (a first-timer sees why the line may not yet be acceptable),
    // and it never affects PrerequisitesMet.
    public const string MsqGateQuestName = "The Ultimate Weapon";

    // ---- Checked prerequisites: one-time quests DIRECTLY attached to the Zodiac line ----
    // (read via QuestManager.IsQuestComplete). The line unlock itself is UnlockQuestName; the
    // MSQ finale is intentionally excluded (see MsqGateQuestName). These three are the
    // content-unlock quests the parts genuinely require, so they are checked and they gate.
    public static readonly IReadOnlyList<PrereqQuest> GlobalPrerequisites = new[]
    {
        new PrereqQuest("Ghosts of Amdapor", "Unlocks Amdapor Keep (Amdapor Glyph, Part 4)"),
        new PrereqQuest("Trauma Queen", "Unlocks The Wanderer's Palace (weapon piece, Part 2)"),
        new PrereqQuest("Waking the Spirit", "Unlocks materia melding (Part 2 meld)"),
    };

    // ---- The ten ordered quest parts (shared structure; per-job specifics in JobRelicData) ----
    //
    // SEAM: CompletedAtSequence is 0 for every part below, meaning "not yet confirmed".
    // The exact "A Relic Reborn" quest-sequence value at which each part finishes must
    // be confirmed on the live client. The checker surfaces the raw live sequence
    // (PrerequisiteReport.LiveQuestSequence) precisely so these can be read off in-game
    // and filled in here, after which per-part status becomes exact. Until then the
    // checker reports parts from the live sequence ordinally and from the positive
    // item signals below.
    public static readonly IReadOnlyList<QuestPart> GlobalParts = new[]
    {
        new QuestPart
        {
            Part = 1, Name = "Broken Weapon",
            Summary = "Recover the broken quest weapon from your job's beastman stronghold, then report to Gerolt.",
            // The generated start block recovers the weapon at sequence 1 and reports it at 2, so the
            // part is done once the quest advances past the report (seq >= 3). Derived, like the part
            // 2/3 windows below, from the journal order the live calibrations already confirm (the
            // alumina delivery at 7, Amdapor Keep at 8, the beastman hunt at 10, the Hydra at 12).
            CompletedAtSequence = 3,
        },
        new QuestPart
        {
            Part = 2, Name = "Class Weapon",
            Summary = "Obtain the class weapon and meld two Grade III materia onto it (requires Waking the Spirit), " +
                      "then deliver it to Gerolt. Buying and melding cannot be automated, so the step is surfaced " +
                      "as '<Job>: <Weapon> (<Materia> x2)' with market-board search and an Artisan crafting list.",
            // The three journal steps of this part: obtain the weapon (seq 3), affix the two materia
            // (4), deliver the melded weapon to Gerolt (5, a generated Gerolt turn-in). Complete once
            // the quest reaches the Chimera (6). See Data/ClassWeaponStep for the full derivation.
            ActiveFromSequence = 3,
            CompletedAtSequence = 6,
        },
        new QuestPart
        {
            Part = 3, Name = "Complete A Relic Reborn: The Chimera",
            Summary = "Defeat the Dhorme Chimera in the trial 'A Relic Reborn: The Chimera' and obtain Alumina Salts.",
            HaveItemName = "Alumina Salts",
            // The CFC name carries the ": The Chimera" suffix (the bare "A Relic Reborn" did not
            // resolve). Run via AutoDuty. CALIBRATED from a live /relic prereq: the quest reads
            // sequence 7 right after the Chimera, so the part is complete at seq >= 7.
            DutyName = "A Relic Reborn: The Chimera",
            // ActiveFromSequence 6 is the FIX for "after handing over the broken weapon it tried to
            // run the Chimera first": with no lower bound the trial's window was [0, 7), so it was
            // eligible from the moment the quest was accepted and the engine queued it while the
            // quest was still asking for the melded class weapon (part 2, sequences 3-5). The
            // Chimera is the journal step right after the melded-weapon delivery, so it runs AT
            // sequence 6 and completes when the quest advances to 7 (calibrated live: the quest
            // reads 7 right after the Chimera).
            ActiveFromSequence = 6,
            CompletedAtSequence = 7,
            Location = new MapStop("A Relic Reborn (Chimera) entrance, the Weeping Saint", CoerthasCentralHighlands, 32.1f, 7.2f, 2.1f),
            Items = new[] { new MaterialReq("Alumina Salts", 1, MaterialSource.Trial, "A Relic Reborn: The Chimera (8-man trial)") },
        },
        new QuestPart
        {
            Part = 4, Name = "Complete Amdapor Keep",
            Summary = "Clear Amdapor Keep (normal) for the Amdapor Glyph, trade it to Rowena at Revenant's Toll, and buy the Mor Dhona consumables. Then follow the questline as usual.",
            HaveItemName = "Amdapor Glyph", ItemIsKeyItem = true,
            // The Amdapor Keep clear is run via AutoDuty. Gated to sequence 8: after the Chimera (part 3,
            // done at seq 7) the quest parks at seq 7 to DELIVER the Alumina Salts to Gerolt (the seq-7
            // between-trial turn-in in BaseRelicHuntGenerator), advancing to seq 8. Amdapor Keep must NOT
            // run before that hand-over -- reported live: "after the Chimera it tried to bring me into
            // Amdapor Keep, but you have to hand over the Alumina Salts to Gerolt first". Without an
            // ActiveFromSequence the dungeon (ActiveFrom 0) was eligible the instant the Chimera cleared.
            // CALIBRATED: the quest reads sequence 8 right after the Amdapor Keep clear (Amdapor Glyph in
            // hand), so the dungeon runs AT seq 8 and the part is complete once the quest advances past it
            // (seq >= 9, the post-clear Amdapor Glyph trade to Rowena). ActiveFromSequence 8 pairs with
            // CompleteAtSequence 9 to form the [8, 9) run window (the in-session _relicRan flag prevents a
            // re-clear at seq 8 while the glyph is held before the trade).
            // Post-clear tail: trade the Amdapor Glyph to Rowena (advances to seq 9), then report to
            // Gerolt (the seq-9 between-trial turn-in, advancing to seq 10 where the beastman hunt begins).
            // The Gerolt report IS now automated (BaseRelicHuntGenerator seq-9 turn-in); the Rowena glyph
            // trade and the Auriana vendor buys are still manual SEAMs (Rowena/Auriana shop ids needed).
            DutyName = "Amdapor Keep",
            ActiveFromSequence = 8,
            CompletedAtSequence = 9,
            Location = new MapStop("Rowena / Auriana, Revenant's Toll", MorDhona, 21.9f, 5.0f, 0.5f),
            Items = new[]
            {
                new MaterialReq("Amdapor Glyph", 1, MaterialSource.Dungeon, "Amdapor Keep (normal; not Hard / Lost City)"),
                new MaterialReq("Radz-at-Han Quenching Oil", 1, MaterialSource.Vendor, "Auriana, Mor Dhona (15 Poetics)"),
                new MaterialReq("Thavnairian Mist", 3, MaterialSource.Vendor, "Auriana, Mor Dhona (20 Poetics each; for the later Zenith step)"),
            },
        },
        new QuestPart
        {
            Part = 5, Name = "Beastmen Hunt",
            Summary = "Equip the unfinished relic and cull 24 beastmen (8 each of 3 types) at your stronghold, then report to Gerolt.",
            // VERIFIED (quest text JobXxx001 + live /relic questwork): active at sequence 10, the
            // 24 kills are done at sequence 11, so the part is complete at seq >= 11. The Gerolt
            // report after the hunt then advances the quest to sequence 12 (where the Hydra begins).
            ActiveFromSequence = 10,
            CompletedAtSequence = 11,
        },
        new QuestPart
        {
            Part = 6, Name = "Hydra",
            Summary = "Defeat the Hydra in the trial 'A Relic Reborn: The Hydra' with the unfinished relic equipped.",
            DutyName = "A Relic Reborn: The Hydra",
            // VERIFIED (quest text JobXxx001 + user live report): the Hydra becomes active at
            // sequence 12 -- AFTER the beastman-hunt report to Gerolt (seq 11 -> 12) -- and the duty
            // clears at sequence 13. So it must NOT run at seq 11 (the prior value 14 also left it
            // wrongly "incomplete" at seq 13 after the clear). Active from 12, complete at seq >= 13.
            ActiveFromSequence = 12,
            CompletedAtSequence = 13,
            Location = new MapStop("A Relic Reborn: The Hydra entrance (by Halatali)", EasternThanalan, 14.7f, 30.2f, 0.4f),
        },
        new QuestPart
        {
            Part = 7, Name = "White-Hot Ember",
            Summary = "Defeat Ifrit in The Bowl of Embers (Hard) and obtain White-Hot Ember.",
            HaveItemName = "White-Hot Ember",
            // VERIFIED: the three primals run consecutively from seq 15 with NO Gerolt report between
            // them (a single delivery of all three items follows Titan) -- Ifrit active 15/done 16,
            // Garuda active 16/done 17, Titan active 17/done 18. The give-weapon-to-Gerolt step (seq
            // 14) precedes Ifrit, so Ifrit must not run before seq 15.
            ActiveFromSequence = 15,
            CompletedAtSequence = 16,
            Items = new[] { new MaterialReq("White-Hot Ember", 1, MaterialSource.Trial, "Ifrit, The Bowl of Embers (Hard)") },
        },
        new QuestPart
        {
            Part = 8, Name = "Howling Gale",
            Summary = "Defeat Garuda in The Howling Eye (Hard) and obtain Howling Gale.",
            HaveItemName = "Howling Gale",
            ActiveFromSequence = 16,
            CompletedAtSequence = 17,
            Items = new[] { new MaterialReq("Howling Gale", 1, MaterialSource.Trial, "Garuda, The Howling Eye (Hard)") },
        },
        new QuestPart
        {
            Part = 9, Name = "Hyperfused Ore",
            Summary = "Defeat Titan in The Navel (Hard) and obtain Hyperfused Ore, then report to Gerolt.",
            HaveItemName = "Hyperfused Ore",
            // Titan is the last combat (active 17, done 18); after the Navel (Hard) the quest goes to
            // the item-delivery turn-ins (seq 18 deliver the 3 drops, seq 19 deliver the oil).
            ActiveFromSequence = 17,
            CompletedAtSequence = 18,
            Items = new[] { new MaterialReq("Hyperfused Ore", 1, MaterialSource.Trial, "Titan, The Navel (Hard)") },
        },
        new QuestPart
        {
            Part = 10, Name = "Radz-at-Han Quenching Oil",
            Summary = "Buy a second Radz-at-Han Quenching Oil from Auriana and turn it in to Gerolt for the finished relic.",
            Location = new MapStop("Auriana, Revenant's Toll (then Gerolt, Hyrstmill)", MorDhona, 21.9f, 5.0f, 0.5f),
            Items = new[] { new MaterialReq("Radz-at-Han Quenching Oil", 1, MaterialSource.Vendor, "Auriana, Mor Dhona (15 Poetics)") },
        },
    };

    // Consumables shared by every job (the Mor Dhona purchases). Radz-at-Han Quenching
    // Oil is bought twice (Part 4 and Part 10). Thavnairian Mist is for the subsequent
    // Zenith upgrade but is bought on the same trip per the wiki.
    private static readonly IReadOnlyList<MaterialReq> SharedConsumables = new[]
    {
        new MaterialReq("Radz-at-Han Quenching Oil", 2, MaterialSource.Vendor, "Auriana, Mor Dhona (15 Poetics each; Parts 4 and 10)"),
        new MaterialReq("Thavnairian Mist", 3, MaterialSource.Vendor, "Auriana, Mor Dhona (20 Poetics each; for the Zenith upgrade)"),
    };

    // ---- Per-job content ----
    public static readonly IReadOnlyDictionary<RelicJob, JobRelicData> ByJob = BuildJobs();

    public static JobRelicData? For(RelicJob job)
        => job != RelicJob.None && ByJob.TryGetValue(job, out var d) ? d : null;

    // The QuestPart.CompletedAtSequence for a part number (0 = uncalibrated / not gated).
    public static int CompletedAtSequenceFor(int part)
    {
        foreach (var p in GlobalParts)
            if (p.Part == part)
                return p.CompletedAtSequence;
        return 0;
    }

    // The QuestPart.ActiveFromSequence for a part number (0 = no lower bound). Carried onto the
    // generated RelicObjective so the controller does not run a trial before the quest has
    // reached its step (e.g. the Hydra at seq 12, not while the seq-11 Gerolt report is pending).
    public static int ActiveFromSequenceFor(int part)
    {
        foreach (var p in GlobalParts)
            if (p.Part == part)
                return p.ActiveFromSequence;
        return 0;
    }

    // The QuestPart.CompletionQuestVariablesFlags for a part number (empty when none are
    // authored). Carried onto the generated RelicObjective so BaseRelicState can verify the
    // part the Questionable way (work-byte nibble match) in addition to the sequence gate.
    public static IReadOnlyList<QuestWorkValue?> CompletionFlagsFor(int part)
    {
        foreach (var p in GlobalParts)
            if (p.Part == part)
                return p.CompletionQuestVariablesFlags;
        return System.Array.Empty<QuestWorkValue?>();
    }

    // The full material list to stock for a job: the two meld materia, the class-weapon
    // crafting ingredients, and the shared Mor Dhona consumables. This is the basis of
    // the shopping list and the retainer-availability check.
    public static IReadOnlyList<MaterialReq> MaterialsFor(RelicJob job)
    {
        var data = For(job);
        if (data == null)
            return System.Array.Empty<MaterialReq>();
        return data.Materia
            .Concat(data.CraftMaterials)
            .Concat(SharedConsumables)
            .ToList();
    }

    private static Dictionary<RelicJob, JobRelicData> BuildJobs()
    {
        var jobs = new List<JobRelicData>
        {
            new()
            {
                Job = RelicJob.Paladin,
                RelicWeaponName = "Curtana", SecondaryRewardName = "Holy Shield",
                ClassWeaponName = "Aeolian Scimitar",
                BrokenWeapon = new MapStop("Zahar'ak", SouthernThanalan, 30f, 19f),
                Materia = Meld("Battledance Materia III"),
                CraftMaterials = new[]
                {
                    Node("Darksteel Ore", 3),
                    Wp("Blunt Aeolian Scimitar", 1),
                    Craft("Basilisk Egg", 1),
                },
                BeastmenHunt = new MapStop("Zahar'ak", SouthernThanalan, 30f, 19f),
                Beastmen = new[]
                {
                    new BeastmanTarget("Zahar'ak Lancer", 27f, 20f),
                    new BeastmanTarget("Zahar'ak Pugilist", 23f, 21f),
                    new BeastmanTarget("Zahar'ak Thaumaturge", 29.7f, 19.1f),
                },
            },
            new()
            {
                Job = RelicJob.Warrior,
                RelicWeaponName = "Bravura",
                ClassWeaponName = "Barbarian's Bardiche",
                BrokenWeapon = new MapStop("U'Ghamaro Mines", OuterLaNoscea, 23f, 10f),
                Materia = Meld("Battledance Materia III"),
                CraftMaterials = new[]
                {
                    Node("Darksteel Ore", 3),
                    Wp("Bloody Bardiche Head", 1),
                    Craft("Basilisk Egg", 1),
                },
                BeastmenHunt = new MapStop("U'Ghamaro Mines", OuterLaNoscea, 23f, 10f),
                Beastmen = new[]
                {
                    new BeastmanTarget("U'Ghamaro Quarryman", 21f, 5f),
                    new BeastmanTarget("U'Ghamaro Bedesman", 22.6f, 7.0f),
                    new BeastmanTarget("U'Ghamaro Roundsman", 23f, 9f),
                },
            },
            new()
            {
                Job = RelicJob.Dragoon,
                RelicWeaponName = "Gae Bolg",
                ClassWeaponName = "Champion's Lance",
                BrokenWeapon = new MapStop("Natalan", CoerthasCentralHighlands, 34f, 21f),
                Materia = Meld("Savage Aim Materia III"),
                CraftMaterials = new[]
                {
                    Node("Spruce Log", 3),
                    Wp("Bloody Lance Head", 1),
                },
                BeastmenHunt = new MapStop("Natalan", CoerthasCentralHighlands, 34f, 21f),
                Beastmen = new[]
                {
                    new BeastmanTarget("Natalan Boldwing", 32f, 18f),
                    new BeastmanTarget("Natalan Fogcaller", 31.2f, 17.2f),
                    // Gae Bolg's third mob is the SWIFTBEAK (verified in the quest text
                    // JobDrg001_01122); the Windtalon is the BARD relic's mob. Killing
                    // Windtalons never credits the Dragoon quest.
                    new BeastmanTarget("Natalan Swiftbeak", 34.7f, 22.2f),
                },
            },
            new()
            {
                Job = RelicJob.Monk,
                RelicWeaponName = "Sphairai",
                ClassWeaponName = "Wildling's Cesti",
                BrokenWeapon = new MapStop("Zahar'ak", SouthernThanalan, 32f, 18f),
                Materia = Meld("Savage Aim Materia III"),
                CraftMaterials = new[]
                {
                    Node("Darksteel Ore", 3),
                    Wp("Bloody Cesti Covers", 1),
                },
                BeastmenHunt = new MapStop("Zahar'ak", SouthernThanalan, 32f, 18f),
                Beastmen = new[]
                {
                    new BeastmanTarget("Zahar'ak Lancer", 27f, 20f),
                    new BeastmanTarget("Zahar'ak Pugilist", 23f, 21f),
                    new BeastmanTarget("Zahar'ak Archer", 23f, 21f),
                },
            },
            new()
            {
                Job = RelicJob.Ninja,
                RelicWeaponName = "Yoshimitsu",
                ClassWeaponName = "Vamper's Knives",
                BrokenWeapon = new MapStop("Sapsa Spawning Grounds", WesternLaNoscea, 16f, 17f),
                Materia = Meld("Heavens' Eye Materia III"),
                CraftMaterials = new[]
                {
                    Wp("Bloody Knife Blades", 1),
                    Craft("Cinnabar", 2),
                    Craft("Ochu Vine", 1),
                    Craft("Desert Saffron", 1),
                    Craft("Rosewood Log", 3),
                },
                BeastmenHunt = new MapStop("Sapsa Spawning Grounds", WesternLaNoscea, 16f, 17f),
                Beastmen = new[]
                {
                    new BeastmanTarget("Sapsa Shelfspine", 15.6f, 14.8f),
                    new BeastmanTarget("Sapsa Shelfclaw", 15f, 14f),
                    new BeastmanTarget("Sapsa Shelftooth", 13f, 14f),
                },
            },
            new()
            {
                Job = RelicJob.Bard,
                RelicWeaponName = "Artemis Bow",
                ClassWeaponName = "Longarm's Composite Bow",
                BrokenWeapon = new MapStop("Natalan", CoerthasCentralHighlands, 35.4f, 22.2f),
                Materia = Meld("Heavens' Eye Materia III"),
                CraftMaterials = new[]
                {
                    Node("Spruce Log", 3),
                    Wp("Bloody Bow Rim", 1),
                },
                BeastmenHunt = new MapStop("Natalan", CoerthasCentralHighlands, 34.2f, 23.6f, 1.5f),
                Beastmen = new[]
                {
                    // Single central anchor for all three (per request); the engine
                    // engages each by name in a large radius around this point rather
                    // than visiting three separate table coordinates.
                    new BeastmanTarget("Natalan Boldwing", 34.2f, 23.6f, 1.5f),
                    new BeastmanTarget("Natalan Fogcaller", 34.2f, 23.6f, 1.5f),
                    new BeastmanTarget("Natalan Windtalon", 34.2f, 23.6f, 1.5f),
                },
            },
            new()
            {
                Job = RelicJob.BlackMage,
                RelicWeaponName = "Stardust Rod",
                ClassWeaponName = "Sanguine Scepter",
                BrokenWeapon = new MapStop("U'Ghamaro Mines", OuterLaNoscea, 23f, 10f),
                Materia = Meld("Savage Might Materia III"),
                CraftMaterials = new[]
                {
                    Node("Darksteel Ore", 3),
                    Wp("Pinprick Pebble", 1),
                },
                BeastmenHunt = new MapStop("U'Ghamaro Mines", OuterLaNoscea, 23f, 10f),
                Beastmen = new[]
                {
                    new BeastmanTarget("U'Ghamaro Quarryman", 21f, 5f),
                    new BeastmanTarget("U'Ghamaro Bedesman", 22.6f, 7.0f),
                    new BeastmanTarget("U'Ghamaro Priest", 22f, 6f),
                },
            },
            new()
            {
                Job = RelicJob.Summoner,
                RelicWeaponName = "The Veil of Wiyu",
                ClassWeaponName = "Erudite's Picatrix of Casting",
                BrokenWeapon = new MapStop("Sylphlands", EastShroud, 25f, 19f),
                Materia = Meld("Savage Might Materia III"),
                CraftMaterials = new[]
                {
                    Node("Gold Sand", 3),
                    Wp("Bloody Grimoire Binding", 1),
                    Craft("Spoken Blood", 1),
                },
                BeastmenHunt = new MapStop("Sylphlands", EastShroud, 25f, 19f),
                // The Veil of Wiyu hunt culls SYLPHEED snarls/sighs/screeches (verified in the
                // quest text JobSmn001_01126), not the "violet" sylphs -- killing violet mobs
                // never credits the Summoner quest. Coords are the prior anchors (verify in-game).
                Beastmen = new[]
                {
                    new BeastmanTarget("Sylpheed Sigh", 24f, 16f),
                    new BeastmanTarget("Sylpheed Screech", 24.8f, 10.5f),
                    new BeastmanTarget("Sylpheed Snarl", 24f, 16f),
                },
            },
            new()
            {
                Job = RelicJob.WhiteMage,
                RelicWeaponName = "Thyrus",
                ClassWeaponName = "Madman's Whispering Rod",
                BrokenWeapon = new MapStop("U'Ghamaro Mines", OuterLaNoscea, 24.3f, 6.4f),
                Materia = Meld("Quicktongue Materia III"),
                CraftMaterials = new[]
                {
                    Node("Trillium Bulb", 1),
                    Node("Thavnairian Mistletoe", 1),
                    Node("Vampire Plant", 1),
                    Craft("Cinnabar", 2),
                    Craft("Rock Salt", 1),
                },
                BeastmenHunt = new MapStop("U'Ghamaro Mines", OuterLaNoscea, 24.3f, 6.4f),
                Beastmen = new[]
                {
                    new BeastmanTarget("U'Ghamaro Quarryman", 21f, 5f),
                    new BeastmanTarget("U'Ghamaro Bedesman", 22.6f, 7.0f),
                    new BeastmanTarget("U'Ghamaro Priest", 22f, 6f),
                },
            },
            new()
            {
                Job = RelicJob.Scholar,
                RelicWeaponName = "Omnilex",
                ClassWeaponName = "Erudite's Picatrix of Healing",
                BrokenWeapon = new MapStop("Sapsa Spawning Grounds", WesternLaNoscea, 16f, 17f),
                Materia = Meld("Quicktongue Materia III"),
                CraftMaterials = new[]
                {
                    Node("Gold Sand", 3),
                    Wp("Bloody Grimoire Binding", 1),
                    Craft("Spoken Blood", 1),
                },
                BeastmenHunt = new MapStop("Sapsa Spawning Grounds", WesternLaNoscea, 16f, 17f),
                Beastmen = new[]
                {
                    new BeastmanTarget("Sapsa Shelfspine", 15.6f, 14.8f),
                    new BeastmanTarget("Sapsa Shelfclaw", 15f, 14f),
                    new BeastmanTarget("Sapsa Shelftooth", 13f, 14f),
                },
            },
        };

        return jobs.ToDictionary(j => j.Job);
    }

    // ---- Small builders to keep the per-job blocks readable ----

    private static MaterialReq[] Meld(string materiaName) => new[]
    {
        new MaterialReq(materiaName, 2, MaterialSource.Materia,
            "Meld onto the class weapon (Waking the Spirit); ~1,000 gil/meld at a Materia Melder"),
    };

    private static MaterialReq Node(string name, int qty)
        => new(name, qty, MaterialSource.UnspoiledNode, "Gathered from a timed unspoiled node");

    private static MaterialReq Wp(string name, int qty)
        => new(name, qty, MaterialSource.WanderersPalace, "Chest in The Wanderer's Palace (Trauma Queen)");

    private static MaterialReq Craft(string name, int qty)
        => new(name, qty, MaterialSource.OtherCraft, "Class-weapon crafting ingredient");
}
