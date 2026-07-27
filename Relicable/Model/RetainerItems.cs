using System;
using System.Collections.Generic;

namespace Relicable.Model;

// Generic persisted snapshot of arbitrary item counts a single retainer holds. This
// mirrors RetainerMateriaSnapshot but is not materia-specific, so the base-relic
// material check can reuse the same native-bell scan the Novus materia cache uses.
// AutoRetainer's IPC cannot supply item-level retainer inventory, so counts are read
// from the open retainer in game memory and cached here for offline display.
[Serializable]
public sealed class RetainerItemSnapshot
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;

    // Owning character content id, so multi-character setups do not collide.
    public ulong OwnerContentId { get; set; }

    // Unix seconds when this snapshot was last refreshed from the open retainer.
    public long ScannedAtUnix { get; set; }

    // Item id -> quantity held by this retainer (only tracked ids are stored).
    public Dictionary<uint, int> Items { get; set; } = new();
}

// The persisted cache of every scanned retainer's tracked items, keyed by retainer id.
[Serializable]
public sealed class RetainerItemCache
{
    public Dictionary<ulong, RetainerItemSnapshot> Retainers { get; set; } = new();

    // Total quantity of one item id held across every cached retainer.
    public int TotalFor(uint itemId)
    {
        var sum = 0;
        foreach (var r in Retainers.Values)
            if (r.Items.TryGetValue(itemId, out var n))
                sum += n;
        return sum;
    }

    // Replace one retainer's snapshot wholesale (a fresh scan is authoritative).
    public void Upsert(RetainerItemSnapshot snapshot)
        => Retainers[snapshot.RetainerId] = snapshot;
}
