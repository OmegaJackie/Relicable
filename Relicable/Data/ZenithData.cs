using System.Collections.Generic;
using System.Numerics;
using Relicable.BaseRelic;
using Relicable.Steps;

namespace Relicable.Data;

// Data for the ZENITH step: the il90 upgrade that sits between the finished base relic and the
// Atma stage. It is a pure ITEM gate -- no quest, no unlock -- with two halves:
//
//   1. Thavnairian Mist, bought from Auriana at Revenant's Toll (Mor Dhona) with Allagan
//      Tomestones of Poetics. The relic materials sit under her SPECIAL ARMS category, not the
//      default gear grid, which is why the purchase walks her exchanges (AurianaPoeticsShop).
//   2. The Furnace beside Gerolt in Hyrstmill (North Shroud): SpecialShop 1769484, "Relic
//      Enhancement". Every solo main hand is its OWN trade at 3 mists; the Paladin pair is TWO
//      entries, Curtana + 2 mists and Holy Shield + 1 mist (3 for the full set, and no entry
//      yields two items). The per-weapon cost lives in RelicWeaponStages.ZenithMistCost.
//
// SEAM -- THE FURNACE ITSELF. Nothing about the Furnace is derivable offline here: it has no
// entry in any sheet this plugin already reads, so its DataId is unknown and NO position is
// invented for it. Instead it is addressed the way every other unverified world object in this
// codebase is: by NAME, near an anchor that IS verified -- Gerolt's own captured position, a few
// yalms away (BaseRelicData.GeroltPosition). WorldObject.FindNearest is name-driven,
// ObjectKind-tolerant and prefers a targetable match, so it locates the Furnace from there
// without a hardcoded id. If the object name differs by patch or client language the step fails
// with the nearby object names logged, rather than interacting with the wrong thing.
public static class ZenithData
{
    // The Furnace's in-game object name. The primary (and only) needle -- see the SEAM note.
    public const string FurnaceObjectName = "Furnace";

    // SpecialShop row behind the Furnace ("Relic Enhancement"). Recorded for provenance: the
    // mist-cost table in RelicWeaponStages was verified against it. Nothing opens it by id --
    // the shop is reached by interacting with the Furnace object.
    public const uint ZenithShopId = 1769484;

    // The consumable traded at the Furnace, resolved by name through the base-relic catalog (the
    // same path the main window's Zenith counter uses). 0 when the Item sheet is not ready yet.
    public static uint MistItemId => BaseRelicCatalog.ItemId(MistItemName);

    public const string MistItemName = "Thavnairian Mist";

    // The Furnace stands beside Gerolt in Hyrstmill, so his verified position doubles as the
    // streaming anchor and his zone as the teleport target.
    public static uint FurnaceTerritory => BaseRelicData.GeroltTerritory;

    public static Vector3 FurnaceAnchor => BaseRelicData.GeroltPosition;

    // Teleportable aetheryte serving Hyrstmill's zone (North Shroud), or 0 when unresolved.
    public static uint FurnaceAetheryte => Locations.AetheryteForTerritory(BaseRelicData.GeroltTerritory);

    // Total Thavnairian Mist the weapons currently in the hands need at the Furnace. Sums each
    // weapon's OWN trade cost, so the Paladin's Curtana (2) + Holy Shield (1) come to 3 just as a
    // solo main hand does.
    public static int MistNeededForEquipped()
    {
        var total = 0;
        foreach (var (_, itemId) in GameState.EquippedZenithPendingWeapons())
            total += RelicWeaponStages.ZenithMistCost(itemId);
        return total;
    }

    // The equipped bare relics still awaiting their trade, main hand first. The Zenith step walks
    // this list, trading one weapon per pass.
    public static List<(ushort Slot, uint ItemId)> PendingTrades()
        => GameState.EquippedZenithPendingWeapons();
}
