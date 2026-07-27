using System.Collections.Generic;
using System.Numerics;
using Relicable.BaseRelic;
using Relicable.Model;

namespace Relicable.Data;

// Part 2 of "A Relic Reborn": the job's CLASS WEAPON, melded with two Grade III materia,
// which is handed to Gerolt before the Chimera trial can be run.
//
// This is the one part of the base relic Relicable cannot drive for you (the weapon is
// bought/crafted and the meld is done at a materia melder), so instead of an objective it is
// surfaced as an annotated step: "<Job>: <Weapon> (<Materia> x2)", with the weapon and materia
// names clickable to search an open market board, a travel button to the market board nearest
// the Limsa Lominsa aetheryte, and an Artisan crafting-list button that queues the weapon and
// every pre-craft.
//
// QUEST SEQUENCES (derived from the journal, and consistent with every value calibrated live in
// BaseRelicData -- Amdapor Keep at 8, the alumina delivery at 7, the beastman hunt at 10, the
// Hydra at 12, the weapon hand-over at 14):
//   seq 0 accept  1 broken weapon  2 deliver broken weapon
//   seq 3 OBTAIN the class weapon      <- this step
//   seq 4 AFFIX two Grade III materia  <- this step
//   seq 5 DELIVER the melded weapon to Gerolt (automated: a Gerolt turn-in objective)
//   seq 6 the Chimera trial
// Before this was authored the Chimera (part 3) carried no ActiveFromSequence, so it was
// eligible from sequence 0 and the run queued the trial the moment the broken weapon was
// reported -- the "it tried to run chimera first" report.
public sealed class ClassWeaponStep
{
    public RelicJob Job { get; init; }
    public string JobName { get; init; } = string.Empty;

    // The class weapon (e.g. "Aeolian Scimitar") and its Item-sheet id (0 = unresolved).
    public string WeaponName { get; init; } = string.Empty;
    public uint WeaponItemId { get; init; }

    // Its crafting recipe (0 when the item has none) and the crafter that makes it.
    public uint RecipeId { get; init; }
    public string CraftJob { get; init; } = string.Empty;

    // The Grade III materia melded onto it, two of them for every job.
    public string MateriaName { get; init; } = string.Empty;
    public uint MateriaItemId { get; init; }
    public int MateriaCount { get; init; } = 2;

    // The annotation the UI and the run log show: "Paladin: Aeolian Scimitar (Battledance Materia III x2)".
    public string Annotation => $"{JobName}: {WeaponName} ({MateriaName} x{MateriaCount})";
}

public static class ClassWeaponSteps
{
    // The relic-quest sequences this step spans (see the ClassWeaponStep header).
    public const int ObtainSequence = 3;
    public const int MeldSequence = 4;
    public const int DeliverSequence = 5;
    public const int ChimeraSequence = 6;

    // The market board nearest the Limsa Lominsa Lower Decks aetheryte (the closest board to any
    // teleport destination on the ARR market circuit). Derived from the zone's own layer data
    // (bg/ffxiv/sea_s1/twn/s1t2/level/planlive.lgb, EObj 2000402 "market board", instance
    // 4167364) rather than eyeballed: world (-123.44, 18.00, 10.14), map (8.8, 11.5), ~41y from
    // the aetheryte at world (-84.00, 20.78, 0.03) / map (9.6, 11.3).
    public const uint MarketBoardTerritory = 129; // Limsa Lominsa Lower Decks
    public static readonly Vector3 MarketBoardWorld = new(-123.44f, 18.00f, 10.14f);
    public const string MarketBoardLabel = "Limsa Lominsa Lower Decks";

    private static readonly Dictionary<RelicJob, ClassWeaponStep?> Cache = new();

    // True while the live relic-quest sequence is inside the class-weapon step (obtain / meld /
    // deliver). The delivery (5) is included so the panel stays up until Gerolt has it.
    public static bool IsWindow(int liveSequence)
        => liveSequence >= ObtainSequence && liveSequence <= DeliverSequence;

    // The class-weapon step for a job, or null when the job has no base-relic data. Item and
    // recipe ids are resolved once and cached; an unresolved id degrades to 0 (the UI then hides
    // the affected control rather than failing).
    public static ClassWeaponStep? For(RelicJob job)
    {
        if (job == RelicJob.None)
            return null;
        if (Cache.TryGetValue(job, out var cached))
            return cached;

        var built = Build(job);
        // Only latch once the ids resolved: BaseRelicCatalog / the Recipe sheet may not be ready
        // on the first call (the same "do not latch a failed resolve" posture MateriaCatalog takes).
        if (built is { WeaponItemId: not 0, MateriaItemId: not 0 })
            Cache[job] = built;
        return built;
    }

    private static ClassWeaponStep? Build(RelicJob job)
    {
        var data = BaseRelicData.For(job);
        if (data == null || string.IsNullOrEmpty(data.ClassWeaponName))
            return null;

        // Part 2's meld requirement is the single MaterialReq in JobRelicData.Materia (2x Grade III).
        var materia = data.Materia.Count > 0 ? data.Materia[0] : null;
        var weaponId = BaseRelicCatalog.ItemId(data.ClassWeaponName);
        var (recipeId, craftJob) = Sheets.RecipeForItem(weaponId);

        return new ClassWeaponStep
        {
            Job = job,
            JobName = RelicJobs.DisplayName(job),
            WeaponName = data.ClassWeaponName,
            WeaponItemId = weaponId,
            RecipeId = recipeId,
            CraftJob = craftJob,
            MateriaName = materia?.ItemName ?? string.Empty,
            MateriaItemId = materia == null ? 0u : BaseRelicCatalog.ItemId(materia.ItemName),
            MateriaCount = materia?.Quantity ?? 2,
        };
    }
}
