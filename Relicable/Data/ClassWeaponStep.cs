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
// QUEST SEQUENCES (the journal's own entries -- see the full map in BaseRelicData.GlobalParts):
//   seq 0 accept  1 timeworn weapon  2 deliver the timeworn weapon
//   seq 3 DELIVER the class weapon melded with two Grade III materia to Gerolt
//   seq 4 the Chimera trial
// The journal has ONE entry for this part, not three: "Deliver a materia-enhanced <weapon> to
// Gerolt". Buying/crafting the weapon and melding the materia are player preparation the quest
// never tracks separately -- it simply sits at 3 until Gerolt has the finished item. (Until
// 1.5.2.1 this file claimed a separate obtain=3 / meld=4 / deliver=5, which is where the
// two-sequence shift in the rest of the head of the table came from.)
//
// Because the preparation is untracked and can take a long time, the panel's window opens at
// sequence 1 -- as soon as the line is genuinely underway -- rather than only at 3, so the
// weapon and materia can be lined up while the timeworn weapon is being fetched.
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
    // The relic-quest sequences around this step (see the ClassWeaponStep header).
    // PrepFromSequence is not a journal entry -- it is the earliest sequence at which showing the
    // panel is useful (the line is accepted and the timeworn weapon is being fetched).
    public const int PrepFromSequence = 1;
    public const int DeliverSequence = 3;
    public const int ChimeraSequence = 4;

    // The market board nearest the Limsa Lominsa Lower Decks aetheryte (the closest board to any
    // teleport destination on the ARR market circuit). Derived from the zone's own layer data
    // (bg/ffxiv/sea_s1/twn/s1t2/level/planlive.lgb, EObj 2000402 "market board", instance
    // 4167364) rather than eyeballed: world (-123.44, 18.00, 10.14), map (8.8, 11.5), ~41y from
    // the aetheryte at world (-84.00, 20.78, 0.03) / map (9.6, 11.3).
    public const uint MarketBoardTerritory = 129; // Limsa Lominsa Lower Decks
    public static readonly Vector3 MarketBoardWorld = new(-123.44f, 18.00f, 10.14f);
    public const string MarketBoardLabel = "Limsa Lominsa Lower Decks";

    private static readonly Dictionary<RelicJob, ClassWeaponStep?> Cache = new();

    // True while the melded class weapon is something the player still has to produce: from the
    // start of the line through the sequence-3 delivery, inclusive. It closes the moment the quest
    // advances to the Chimera (4), which is the game's own proof that Gerolt has the weapon.
    public static bool IsWindow(int liveSequence)
        => liveSequence >= PrepFromSequence && liveSequence <= DeliverSequence;

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
