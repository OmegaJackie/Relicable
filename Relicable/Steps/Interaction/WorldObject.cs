using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Relicable.Diagnostics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Relicable.Steps.Interaction;

// Finding and interacting with a plain WORLD OBJECT (not an NPC): a quest treasure
// coffer, a lever, an event marker.
//
// Why this is not NpcInteractor.Find: that finder matches on o.BaseId ONLY
// (NpcInteractor.cs:254-272). It is ObjectKind-agnostic and so would happily return a
// coffer -- but only if the caller already knows the object's DataId. The authored relic
// data describes the target by NAME ("Treasure Coffer"); the DataId is derived from the
// offline game sheets and is UNVERIFIED in-game, so it can only ever be a preference,
// never a requirement.
//
// Why this is not TreasureMapExecutor.FindCoffer: that one hard-filters
// ObjectKind.Treasure (TreasureMapExecutor.cs:617), which is the treasure-MAP chest kind.
// A quest-placed coffer lives in the EObj sheet id space (2000000+), i.e. almost certainly
// ObjectKind.EventObj, so that scan would return nothing. UNVERIFIED SEAM: a quest coffer's
// live ObjectKind cannot be read without the game running, which is exactly why this finder
// checks NO ObjectKind at all.
internal static class WorldObject
{
    // The nearest object matching dataId (preferred when non-zero) or name
    // (case-insensitive), within radius. Null when nothing matches -- the caller keeps
    // travelling so the object can stream in, then times out honestly.
    //
    // targetable reports whether the returned object is currently interactable. It is an
    // OUTPUT, not a filter: a non-targetable match is still returned, because whether a
    // quest coffer reads targetable from far away (or while airborne) is unverified, and
    // refusing to return one would strand the caller with nothing to walk toward. The
    // caller decides what to do with it (see InteractObjectExecutor's spent-object logic).
    public static IGameObject? FindNearest(string? name, uint dataId, float radius, out bool targetable)
    {
        targetable = false;
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var r2 = radius * radius;

        IGameObject? best = null;
        var bestRank = float.MaxValue;

        foreach (var o in Plugin.ObjectTable)
        {
            var byId = dataId != 0 && o.BaseId == dataId;
            var byName = !string.IsNullOrEmpty(name)
                && string.Equals(o.Name.TextValue, name, StringComparison.OrdinalIgnoreCase);
            if (!byId && !byName)
                continue;

            var d = Vector3.DistanceSquared(me, o.Position);
            if (d > r2)
                continue;

            // A DataId hit outranks ANY name hit, at any distance: ids do not localize and
            // cannot collide, names do both. Within a class, nearest wins.
            var rank = byId ? d : d + 1_000_000f;
            if (rank < bestRank)
            {
                bestRank = rank;
                best = o;
            }
        }

        if (best != null)
            targetable = best.IsTargetable;
        return best;
    }

    // VERIFIED against current FFXIVClientStructs, and identical to the four existing call
    // sites (NpcInteractor.cs:243-252, LeveRunner.cs:617-624,
    // ParticipateFateExecutor.cs:393-400, TreasureMapExecutor.cs:654-662).
    // checkLineOfSight is FALSE, matching every one of those: it SKIPS the client-side LoS
    // raycast, i.e. it is the permissive choice. The call returns a ulong whose semantics are
    // undocumented, so it is not a verified success signal -- every caller must carry its own
    // completion evidence and its own timeout.
    public static unsafe void Interact(IGameObject obj)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || obj.Address == nint.Zero)
            return;
        Plugin.TargetManager.Target = obj;
        ts->InteractWithObject((CSGameObject*)obj.Address, false);
        DebugLog.Verbose($"WorldObject: interact -> {obj.Name.TextValue} ({obj.BaseId}, kind {obj.ObjectKind})");
    }
}
