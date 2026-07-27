using System;
using System.Collections.Generic;

namespace Relicable.Data;

// Authored "item-lure" battle-leve mechanics: leves whose journal reads "Lure target from hiding and
// slay it". LeveRunner's default fight loop only clears loaded BattleLeveDirector objective enemies,
// so on a lure leve it finds the roaming fillers but never the hidden target, and the leve times out.
//
// The canonical example is "Don't Forget to Cry" (Leve 645, an Animus / Trials-of-the-Braves book
// leve -- Leve[0] of RelicNote books 2 and 9). Its REAL three-step mechanic (verified against the
// game sheets; the earlier two-role model was inverted and never worked -- see below):
//   1. KILL the "balor's bell" (BNpcName 1738, a level-38 COMBATANT enemy, NOT a passive object) to
//      obtain the "Tears of Nepenthe" key item (EventItem 2000872). The bell is the ONLY leve entry
//      that drops it; the roaming fillers drop nothing.
//   2. USE the Tears on a "prime location" marker (EObjName 2000610 "prime location", a targetable
//      EObj -- NOT the bell).
//   3. A "balor" (BNpcName 1739) emerges; slay two to complete the leve.
//
// So there are THREE distinct object names: the ItemSource enemy you kill for the item, the
// PrimeTarget object you use the item ON, and the Emerge enemy you slay. The prior version conflated
// the source and the prime into one "balor's bell" and tried to use the item on the bell (an enemy)
// -- the item was never usable there, so it stalled at the bell "farming for more" forever (the
// reported symptom). ItemSourceName splits them apart.
//
// Keyed by the leve's English name (resolved at runtime via Sheets.LeveName) so no numeric Leve row
// id is hardcoded, matching EscortLevePaths / LeveNamedTargets / LeveStartOverrides.
//
// SEAM (offline-untestable; verify in-game): the exact object-table name strings, that the "prime
// location" EObj is targetable and the key item is usable on it (ActionManager.UseAction with
// ActionType.EventItem targeted at the EObj), and that the item lives in the Key Items container
// (EventItem ids do). All are parameters here, so a mismatch is a data edit, not a code change.
public static class LeveItemLures
{
    // One item-lure leve:
    //   ItemId           the EventItem (key item) used to lure -- Tears of Nepenthe = 2000872.
    //   PrimeTargetName  the object the item is USED ON, matched by object-table name (the EObj
    //                    "prime location" for leve 645). NOT the enemy that drops the item.
    //   EmergeTargetName the enemy that emerges and must be slain, matched by name -- "balor".
    //   ItemSourceName   the ENEMY you KILL to obtain the item when you hold none (the "balor's bell"
    //                    for leve 645). null when the item is obtained some other way (or the lure
    //                    mechanic is unverified for that leve, in which case RunItemLure degrades to a
    //                    plain objective fight -- no worse than the default).
    public sealed record ItemLure(uint ItemId, string PrimeTargetName, string EmergeTargetName,
        string? ItemSourceName = null);

    public static readonly IReadOnlyDictionary<string, ItemLure> Lures =
        new Dictionary<string, ItemLure>(StringComparer.OrdinalIgnoreCase)
        {
            // Leve 645, Northern Thanalan (Camp Bluefog). Kill a "balor's bell" for Tears of Nepenthe,
            // use the Tears on a "prime location" marker, slay the two balors that emerge.
            ["Don't Forget to Cry"] = new ItemLure(
                ItemId: 2000872,               // EventItem "bottle of Tears of Nepenthe"
                PrimeTargetName: "prime location", // EObjName 2000610 -- the marker the item is used ON
                EmergeTargetName: "balor",
                ItemSourceName: "balor's bell"),   // BNpcName 1738 -- KILL it to get the Tears

            // Leves 650 & 658 share the SAME three-object structure as 645, now verified from
            // BattleLeve.LeveData (DataId 65668 / 65731): LeveData[0] is the "prime location" EObj
            // (2000610 -- the SAME marker as 645); the entry with ItemsInvolved>0 is the SOURCE enemy
            // you kill for the "hippogryph shank" (EventItem 2000880); the entry with
            // ToDoNumberInvolved>0 and a BNpcName is the SLAY target (two to kill). The earlier model
            // put the SOURCE enemy in the PrimeTarget slot and left ItemSourceName null, so RunItemLure
            // never killed a source for shanks, and FindNearestInteractable never matched the (BNpc, not
            // EObj) "prime" -- every branch fell through and the run stood at the anchor (the reported
            // "not moving, not reaching prime locations, not killing the hippogryphs"). Corrected to
            // mirror 645: kill source -> shank -> use on the "prime location" -> slay the emerged target.
            //
            // Leve 650 (Coerthas Central Highlands, Whitebrim): kill "downcast hippocerf" for shanks, use
            // them on a "prime location", slay the two "stegotaur" (the demon "tauri" of the journal).
            ["Got a Gut Feeling about This"] = new ItemLure(
                ItemId: 2000880,                       // hippogryph shank (EventItem)
                PrimeTargetName: "prime location",     // EObjName 2000610 -- the marker the shank is used ON
                EmergeTargetName: "stegotaur",         // BNpcName 1760 -- slay 2
                ItemSourceName: "downcast hippocerf"), // BNpcName 1762 -- KILL for shanks
            // Leve 658 (Mor Dhona): kill "ragged hippogryph" for shanks, use them on a "prime location",
            // slay the two "Foul River hapalit".
            ["Big, Bad Idea"] = new ItemLure(
                ItemId: 2000880,                        // hippogryph shank (EventItem)
                PrimeTargetName: "prime location",      // EObjName 2000610
                EmergeTargetName: "Foul River hapalit", // BNpcName 1770 -- slay 2
                ItemSourceName: "ragged hippogryph"),   // BNpcName 1774 -- KILL for shanks
        };

    // The item-lure spec for an accepted leve name, or null when the leve is not an authored lure leve
    // (LeveRunner then uses its default nearest-objective fight loop).
    public static ItemLure? ForLeveName(string? leveName)
        => !string.IsNullOrEmpty(leveName) && Lures.TryGetValue(leveName!, out var lure)
            ? lure
            : null;
}
