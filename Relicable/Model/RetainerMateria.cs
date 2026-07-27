using System;
using System.Collections.Generic;

namespace Relicable.Model;

// Persisted snapshot of the materia a single retainer holds. AutoRetainer's IPC
// exposes retainer NAMES, gil, and venture state but NOT item-level inventory
// (verified against AutoRetainerAPI OfflineRetainerData, which carries only an
// 'MBItems' count). Item-level counts are therefore read from the native retainer
// inventory in game memory while a retainer is open at the summoning bell, then
// cached here so the Novus planner can report retainer stock even while offline.
[Serializable]
public sealed class RetainerMateriaSnapshot
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;

    // Owning character content id, so multi-character setups do not collide.
    public ulong OwnerContentId { get; set; }

    // Unix seconds when this snapshot was last refreshed from the open retainer.
    public long ScannedAtUnix { get; set; }

    // Materia item id -> quantity held by this retainer. Only materia ids tracked by
    // the Novus catalog are stored; other items are ignored.
    public Dictionary<uint, int> Materia { get; set; } = new();
}

// The persisted cache of every scanned retainer, keyed by retainer id.
[Serializable]
public sealed class RetainerMateriaCache
{
    public Dictionary<ulong, RetainerMateriaSnapshot> Retainers { get; set; } = new();

    // Total quantity of one materia item id held across every cached retainer.
    public int TotalFor(uint itemId)
    {
        var sum = 0;
        foreach (var r in Retainers.Values)
            if (r.Materia.TryGetValue(itemId, out var n))
                sum += n;
        return sum;
    }

    // Replace one retainer's snapshot wholesale (a fresh scan is authoritative).
    public void Upsert(RetainerMateriaSnapshot snapshot)
        => Retainers[snapshot.RetainerId] = snapshot;
}
