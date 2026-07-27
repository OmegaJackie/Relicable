using System.Collections.Generic;
using Relicable.Model;

namespace Relicable.BaseRelic;

// Data and report types for the "A Relic Reborn" (base 2-star) stage. The static
// content lives in BaseRelicData; item/quest ids are resolved by name in
// BaseRelicCatalog; the live evaluation is done by PrerequisiteChecker.
//
// Scope note: this is the data + checker foundation. In-zone routing (converting the
// map coordinates below to world points and resolving height via vnavmesh) and the
// configuration window are a later pass; the coordinates are captured here so that
// pass has authored data to consume.

// Whether a single requirement is met, not met, or cannot be read from game memory
// (so it is neither asserted satisfied nor failed).
public enum RequirementState
{
    Satisfied,
    Unsatisfied,
    Unknown,
}

// Where a base-relic material is obtained, for the shopping list grouping.
public enum MaterialSource
{
    Vendor,           // bought with gil or tomestones (e.g. Radz-at-Han Quenching Oil)
    Materia,          // a meld component (2x Grade III per weapon)
    WanderersPalace,  // dungeon chest weapon-piece
    UnspoiledNode,    // timed gathering node
    Trial,            // dropped by an 8-man trial (Alumina Salts, primal materials)
    Dungeon,          // dropped/awarded by a dungeon (Amdapor Glyph)
    OtherCraft,       // remaining crafting ingredients
    Reward,           // the finished relic weapon
}

// One required material: resolved to an item id by English name at runtime.
public sealed class MaterialReq
{
    public string ItemName { get; init; } = string.Empty;
    public int Quantity { get; init; } = 1;
    public MaterialSource Source { get; init; }

    // Free-text sourcing detail for the shopping list (vendor, node, cost, etc.).
    public string SourceDetail { get; init; } = string.Empty;

    public MaterialReq() { }

    public MaterialReq(string itemName, int quantity, MaterialSource source, string sourceDetail = "")
    {
        ItemName = itemName;
        Quantity = quantity;
        Source = source;
        SourceDetail = sourceDetail;
    }
}

// A map location for a stop on the (deferred) route. Map coordinates are the values
// shown in-game and on the wiki; the world height (FFXIV world Y) is resolved at
// navigation time via vnavmesh PointOnFloor/NearestPoint, so it is not stored here.
public sealed class MapStop
{
    public string Label { get; init; } = string.Empty;

    // TerritoryType sheet row id (stable game data). Used to teleport to the zone's
    // aetheryte (Locations.AetheryteForTerritory) before navigating.
    public uint TerritoryTypeId { get; init; }

    // In-game map coordinates (the x/y the wiki lists).
    public float MapX { get; init; }
    public float MapY { get; init; }

    // In-game height readout (the "Z:" value). 0 means unknown; the routing pass can
    // still resolve the world height via vnavmesh PointOnFloor/NearestPoint when needed.
    public float MapZ { get; init; }

    public bool HasHeight => MapZ != 0f;

    public MapStop() { }

    public MapStop(string label, uint territoryTypeId, float mapX, float mapY)
    {
        Label = label;
        TerritoryTypeId = territoryTypeId;
        MapX = mapX;
        MapY = mapY;
    }

    public MapStop(string label, uint territoryTypeId, float mapX, float mapY, float mapZ)
        : this(label, territoryTypeId, mapX, mapY)
        => MapZ = mapZ;
}

// One of the three beastmen culled 8 times each in Part 5 (the unfinished-relic hunt).
// MapX/MapY are the in-game map coordinates of a primary spawn cluster for this mob,
// used as the navigation anchor; the hunt generator converts them to a world point and
// KillTargetExecutor then engages the nearest mob of this Name within the loaded area.
public sealed class BeastmanTarget
{
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; } = 8;
    public float MapX { get; init; }
    public float MapY { get; init; }

    // Optional in-game height (the "Z:" readout). 0 lets the navmesh resolve the world
    // Y on its own; a value snaps the height so a multi-level stronghold lands right.
    public float MapZ { get; init; }

    public BeastmanTarget() { }

    public BeastmanTarget(string name, float mapX, float mapY, int count = 8)
    {
        Name = name;
        MapX = mapX;
        MapY = mapY;
        Count = count;
    }

    public BeastmanTarget(string name, float mapX, float mapY, float mapZ, int count = 8)
        : this(name, mapX, mapY, count)
        => MapZ = mapZ;
}

// All per-job content for the base relic, keyed by RelicJob in BaseRelicData.
public sealed class JobRelicData
{
    public RelicJob Job { get; init; }

    // The finished base relic awarded at the end (e.g. "Curtana"). Paladin also
    // receives a shield, captured in SecondaryRewardName.
    public string RelicWeaponName { get; init; } = string.Empty;
    public string? SecondaryRewardName { get; init; }

    // Part 1: the broken quest weapon recovered from a beastman stronghold.
    public MapStop BrokenWeapon { get; init; } = new();

    // Part 2: the class weapon that is melded with two Grade III materia.
    public string ClassWeaponName { get; init; } = string.Empty;
    public IReadOnlyList<MaterialReq> Materia { get; init; } = new List<MaterialReq>();

    // Part 2: crafting ingredients for the class weapon (Wanderer's Palace piece,
    // unspoiled-node mats, and any other components). Captured from the wiki; treated
    // as informational/shopping data since the weapon may also be bought.
    public IReadOnlyList<MaterialReq> CraftMaterials { get; init; } = new List<MaterialReq>();

    // Part 5: the stronghold and the three beastmen hunted there (8 each = 24).
    public MapStop BeastmenHunt { get; init; } = new();
    public IReadOnlyList<BeastmanTarget> Beastmen { get; init; } = new List<BeastmanTarget>();
}

// A global prerequisite quest shared by every job (read via QuestManager).
public sealed class PrereqQuest
{
    public string QuestName { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;

    public PrereqQuest() { }

    public PrereqQuest(string questName, string purpose)
    {
        QuestName = questName;
        Purpose = purpose;
    }
}

// One of the ten ordered parts of the "A Relic Reborn" quest. The completion check is
// driven primarily by the live quest sequence; CompletedAtSequence is the sequence
// value at which the part is finished. See BaseRelicData for the seam note: those
// thresholds are confirmed in-game (the checker also surfaces the raw live sequence
// so the values can be read off and corrected). HaveItemName/HaveKeyItemName are
// positive-only corroborating signals (the quest item for that part).
public sealed class QuestPart
{
    public int Part { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;

    // Quest sequence at which this part is complete; 0 = not yet confirmed (seam).
    public int CompletedAtSequence { get; init; }

    // The lowest quest sequence at which this part's content becomes active. The engine must
    // not run it before this, while a preceding turn-in (report to Gerolt / item delivery) is
    // still pending. 0 = no lower bound. Carried onto the RelicObjective.
    public int ActiveFromSequence { get; init; }

    // Optional Questionable-style completion flags: nibble matchers over the relic quest's
    // six work bytes that mark this part done, verified the exact way Questionable verifies
    // (QuestWorkUtils.MatchesQuestWork). Empty until calibrated in-game via /relic questwork,
    // after which per-part completion becomes precise even where CompletedAtSequence is an
    // uncalibrated seam (Parts 1, 2, 10). Six entries (null = don't-care nibble) when set.
    public IReadOnlyList<QuestWorkValue?> CompletionQuestVariablesFlags { get; init; }
        = new List<QuestWorkValue?>();

    // Optional positive signal: the quest item awarded by this part, if any.
    public string? HaveItemName { get; init; }
    public bool ItemIsKeyItem { get; init; }

    // Optional location (trial entrance etc.) for the deferred routing pass.
    public MapStop? Location { get; init; }

    // Optional duty (ContentFinderCondition name) the part requires, for the trial
    // parts. Used to read whether the duty is already unlocked (so the report can show
    // "queue from Duty Finder" instead of "examine the entrance to unlock"). Note the
    // Chimera trial's duty is itself named "A Relic Reborn" -- the same text as the
    // quest -- which is why it is resolved through the duty sheet, not the quest sheet.
    public string? DutyName { get; init; }

    // Optional items associated with the part (vendor buys, trial drops) for display.
    public IReadOnlyList<MaterialReq> Items { get; init; } = new List<MaterialReq>();
}

// ---- Report types (produced by PrerequisiteChecker) ----

// One evaluated requirement line.
public sealed class CheckedRequirement
{
    public string Label { get; init; } = string.Empty;
    public RequirementState State { get; init; }
    public string Detail { get; init; } = string.Empty;
}

// One evaluated material line: how many are needed and how many are held where.
public sealed class CheckedMaterial
{
    public string ItemName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public int Needed { get; init; }
    public int InInventory { get; init; }
    public int OnRetainers { get; init; }
    public MaterialSource Source { get; init; }
    public string SourceDetail { get; init; } = string.Empty;

    public int Total => InInventory + OnRetainers;
    public RequirementState State =>
        ItemId == 0 ? RequirementState.Unknown
        : Total >= Needed ? RequirementState.Satisfied
        : RequirementState.Unsatisfied;
}

// One evaluated quest part.
public sealed class CheckedPart
{
    public int Part { get; init; }
    public string Name { get; init; } = string.Empty;
    public RequirementState State { get; init; }
    public string Detail { get; init; } = string.Empty;
}

// The full base-relic readiness report for one job.
public sealed class PrerequisiteReport
{
    public RelicJob Job { get; init; }
    public bool JobWasDetected { get; init; }
    public bool JobIsActive { get; init; }   // the player is currently on this job
    public int JobLevel { get; init; }       // level on the active job (0 if not active)

    public CheckedRequirement JobLevelRequirement { get; init; } = new();
    public IReadOnlyList<CheckedRequirement> GlobalPrerequisites { get; init; } = new List<CheckedRequirement>();
    public IReadOnlyList<CheckedMaterial> Materials { get; init; } = new List<CheckedMaterial>();

    // The per-job relic quest title ("A Relic Reborn (<weapon>)") and its resolved row
    // id (0 if the title did not resolve in the Quest sheet) -- surfaced for diagnostics.
    public string RelicQuestName { get; init; } = string.Empty;
    public uint RelicQuestId { get; init; }

    // Live relic quest sequence (0 when the quest is not currently active), and whether
    // this job's relic quest has ever been completed.
    public int LiveQuestSequence { get; init; }
    public bool RelicQuestEverCompleted { get; init; }
    public IReadOnlyList<CheckedPart> Parts { get; init; } = new List<CheckedPart>();

    // True when every global prerequisite and the job-level requirement are satisfied
    // (i.e. the player may begin the base relic). Material and per-part readiness are
    // reported separately and do not gate this flag.
    public bool PrerequisitesMet { get; init; }
}
