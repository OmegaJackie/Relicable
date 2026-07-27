using System.Collections.Generic;
using System.Linq;

namespace Relicable.Steps;

// Checks whether something about to be BOUGHT is already owned.
//
// Every purchase in the line costs a currency that had to be farmed -- Poetics for the quenching
// oil and the Thavnairian Mist, gil for the crafted parts -- so buying a second copy of something
// already sitting in a bag or on a retainer is wasted farming. Bags are easy (the executors already
// check them); RETAINERS are the gap, because their contents are not readable unless a retainer is
// open. The plugin caches what it sees during its own retainer visits (RetainerScanner ->
// Configuration.Retainer* snapshots), and that cache is what this reads.
//
// The cache is a SNAPSHOT, not a live read, so it is used in one direction only: it can say "you
// already have one of these, do not buy" -- worth acting on even if slightly stale, because the
// wrong answer costs nothing but a stop -- and it never says "you do not have one", which would
// need it to be current. Nothing here moves items; withdrawing from a retainer is the player's call
// (or the existing retainer-withdraw step's), so this reports and lets the caller stop.
public static class PurchaseGuard
{
    // Where an item is already held, for a purchase that is about to happen. player = the count in
    // your bags; retainers = the total across every cached retainer snapshot; where = a readable
    // "Retainer A (x2), Retainer B" for the guidance message (empty when none hold it).
    public static void FindHeld(Configuration config, uint itemId,
        out int player, out int retainers, out string where)
    {
        player = itemId == 0 ? 0 : GameState.InventoryCount(itemId);
        retainers = 0;
        where = string.Empty;
        if (itemId == 0 || config == null)
            return;

        var parts = new List<string>();
        // The base-relic material cache is the one that tracks the vendor consumables (the oil, the
        // Thavnairian Mist); the atma cache is scanned in the same bag pass. Both are consulted so a
        // caller does not have to know which snapshot its item lands in.
        foreach (var cache in new[] { config.RetainerBaseRelicItems, config.RetainerAtmas })
        {
            if (cache == null)
                continue;
            foreach (var r in cache.Retainers.Values)
            {
                if (!r.Items.TryGetValue(itemId, out var n) || n <= 0)
                    continue;
                retainers += n;
                var name = string.IsNullOrWhiteSpace(r.RetainerName) ? "a retainer" : r.RetainerName;
                parts.Add(n > 1 ? $"{name} (x{n})" : name);
            }
        }
        where = string.Join(", ", parts.Distinct());
    }
}
