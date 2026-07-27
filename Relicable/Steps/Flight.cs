using System.Numerics;
using Dalamud.Game.ClientState.Conditions; // ConditionFlag.InFlight / Mounted (the takeoff gate)
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Relicable.Model;

namespace Relicable.Steps;

// Centralized flight-capability gate.
//
// vnavmesh only routes a flight path correctly where the game actually permits flying.
// Passing fly=true in a zone where flight is NOT available makes vnav pathfind through a
// flight volume the client does not have, which sends the character out of bounds
// (observed in Mor Dhona). So flight is allowed only when the user's AllowFlight config
// is on AND the game reports the current zone as flyable AND we are not inside an authored
// no-fly area (an enclosed cave the client permits flight in but vnav cannot traverse well).
//
// PlayerState.CanFly is the game's own per-zone flag (set during zone loading; true only
// when the zone supports flight and the player has unlocked it), so this is correct in
// every zone without a hand-maintained list. When it is briefly stale right after a zone
// change it reads false, which just means "walk" -- the safe default.
internal static unsafe class Flight
{
    // Areas where the game PERMITS flight but vnav flight is unreliable (enclosed caves / low
    // ceilings), so navigation must WALK even with AllowFlight on. Reported from a live run:
    // U'Ghamaro Mines (Outer La Noscea, territory 180) -- the internal caves are hard to traverse
    // by flight (ARR zones became flyable in patch 5.3, so CanFly is true there). Each entry is a
    // horizontal disc around a map centre; outside the disc the zone flies normally. SEAM: the
    // centre/radius are a generous approximation of the mines footprint (the broken-weapon coffer +
    // the three beastmen sit at map ~21-24, 5-10), so the disc covers the mines while leaving the
    // rest of Outer La Noscea flyable; widen or shrink the radius if it clips.
    private static readonly (uint Territory, float MapX, float MapY, float Radius)[] NoFlyAreas =
    {
        (180, 22.5f, 7.5f, 220f), // U'Ghamaro Mines, Outer La Noscea
    };

    // Cached world centres for NoFlyAreas (MapToWorld is stable per territory), resolved lazily on
    // first use so the game sheets are loaded. Parallel to NoFlyAreas by index; only X/Z are used
    // (the test is horizontal, the caves span a range of heights).
    private static Vector3[]? _noFlyCenters;

    public static bool Allowed(ExecutionContext ctx)
        => ctx.Config.AllowFlight && CanFlyHere() && !InNoFlyArea();

    // Minimum remaining travel (yalms) worth FLYING. Deliberately higher than the MOUNT threshold
    // (Combat.Mount.MinDistance, 30y): a takeoff plus a landing costs a few seconds, so a short hop
    // -- crossing a FATE ring to the next mob -- is faster ridden on the ground. Flight wins on the
    // long legs (crossing a zone to the FATE itself), where it is a straight line over the terrain.
    public const float MinFlyDistance = 60f;

    // Should THIS leg of travel be flown? Pass the remaining distance to the destination.
    //
    // The returned flag is what vnavmesh's PathfindAndMoveCloseTo takes, and it decides which mesh
    // the path is built on. It is load-bearing for TAKEOFF, not merely for routing: vnavmesh never
    // mounts, and it only presses jump (its ExecuteJump takeoff spam) while following a path whose
    // next waypoint is ABOVE the character -- which only a FLYING path produces. So a mounted
    // character handed fly=false rides the ground path the whole way and never leaves the floor.
    //
    // That is exactly why the old "fly only when ConditionFlag.InFlight" rule at the FATE approach
    // sites could never fly: InFlight needs a takeoff, a takeoff needs a flying path, and the flying
    // path was gated on InFlight. Mounting does NOT set InFlight (it sets Mounted), so the
    // "EnsureMounted flips InFlight true for the next tick" assumption never held and every approach
    // walked or rode -- the reported "not flying, walking on the ground".
    //
    // The rule below breaks that cycle without reintroducing the stall the InFlight gate was
    // guarding against (a grounded, UN-mounted character cannot follow a 3D flight path, so vnav
    // gives up short of the target):
    //   * already airborne  -> keep flying, so a path is never flipped to the ground mesh mid-air;
    //   * otherwise fly only when flight is permitted here (Allowed), the leg is long enough to be
    //     worth a takeoff, AND we are actually MOUNTED -- the one state in which vnav's jump can
    //     put us in the air.
    // Callers pair this with Combat.Mount.EnsureMounted(ctx, distance) on the SAME tick: the first
    // ticks path on the ground while the mount is being summoned, then Mounted flips, the fly flag
    // changes, and MoveCloseTo re-issues the request as a flight path (which triggers the takeoff).
    public static bool ShouldFly(ExecutionContext ctx, float distance)
    {
        if (Plugin.Condition[ConditionFlag.InFlight])
            return true;
        if (distance < MinFlyDistance || !Allowed(ctx))
            return false;
        return Plugin.Condition[ConditionFlag.Mounted];
    }

    public static bool CanFlyHere()
    {
        var ps = PlayerState.Instance();
        return ps != null && ps->CanFly;
    }

    // True when the player is inside an authored no-fly disc (see NoFlyAreas). Cheap: a territory
    // compare plus a squared-distance test against the cached centres.
    private static bool InNoFlyArea()
    {
        if (NoFlyAreas.Length == 0)
            return false;
        var territory = (uint)Plugin.ClientState.TerritoryType;
        if (Plugin.ObjectTable.LocalPlayer is not { } me)
            return false;
        var centers = _noFlyCenters ??= ResolveCenters();
        var p = me.Position;
        for (var i = 0; i < NoFlyAreas.Length; i++)
        {
            var a = NoFlyAreas[i];
            if (a.Territory != territory)
                continue;
            var dx = p.X - centers[i].X;
            var dz = p.Z - centers[i].Z;
            if (dx * dx + dz * dz <= a.Radius * a.Radius)
                return true;
        }
        return false;
    }

    private static Vector3[] ResolveCenters()
    {
        var arr = new Vector3[NoFlyAreas.Length];
        for (var i = 0; i < NoFlyAreas.Length; i++)
        {
            var a = NoFlyAreas[i];
            arr[i] = Data.MapCoords.MapToWorld(a.Territory, a.MapX, a.MapY);
        }
        return arr;
    }
}
