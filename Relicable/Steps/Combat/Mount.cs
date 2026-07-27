using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps.Combat;

// Summons a mount for long-distance travel. vnavmesh moves the character but never
// mounts on its own, so without this it runs everywhere on foot. Uses Mount
// Roulette (verified GeneralAction 9) so it works with whatever mounts the player
// has set. Flight itself is handled by vnavmesh when AllowFlight is passed and the
// zone permits it; this just gets the character mounted.
internal static unsafe class Mount
{
    private const uint MountRouletteGeneralAction = 9;
    private const uint DismountGeneralAction = 23;
    private const float MinDistance = 30f;
    private const long ThrottleMs = 3000;

    private static long _last;
    private static long _lastDismount;

    // Mount up when the remaining travel distance is long, mounting is allowed, and
    // the character is not already mounted or otherwise busy.
    public static void EnsureMounted(ExecutionContext ctx, float distance)
    {
        if (!ctx.Config.UseMount || distance < MinDistance)
            return;
        if (Plugin.Condition[ConditionFlag.Mounted])
            return;
        if (Plugin.Condition[ConditionFlag.InCombat]
            || Plugin.Condition[ConditionFlag.BoundByDuty]
            || Plugin.Condition[ConditionFlag.Casting])
            return;
        if (Environment.TickCount64 - _last < ThrottleMs)
            return;

        _last = Environment.TickCount64;
        var am = ActionManager.Instance();
        if (am != null)
        {
            am->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
            DebugLog.Verbose("Mount: summoning (Mount Roulette)");
        }
    }

    // Dismount so the character can act (you cannot attack while mounted). Call on
    // arrival, before engaging.
    public static void EnsureDismounted()
    {
        if (!Plugin.Condition[ConditionFlag.Mounted])
            return;
        if (Environment.TickCount64 - _lastDismount < 1000)
            return;
        _lastDismount = Environment.TickCount64;
        var am = ActionManager.Instance();
        if (am != null)
        {
            am->UseAction(ActionType.GeneralAction, DismountGeneralAction);
            DebugLog.Verbose("Mount: dismounting");
        }
    }

    // True once the character is fully grounded and able to cast: not mounted, not
    // flying, not mid-jump/dive. The rotation must not be enabled before this holds
    // or RSR has nothing it can do and appears to "not cast".
    public static bool IsGrounded()
        => !Plugin.Condition[ConditionFlag.Mounted]
        && !Plugin.Condition[ConditionFlag.InFlight]
        && !Plugin.Condition[ConditionFlag.Jumping]
        && !Plugin.Condition[ConditionFlag.Diving];

    // Get the character onto the ground near 'near' so it can dismount and fight.
    //
    // A direct dismount fails or strands the character when vnavmesh has left it
    // hovering over non-landable terrain (a flag on a cliff edge, over water, etc.).
    // 'near' is the target we want to land AT -- a live mob (whose own position is, by
    // definition, a valid standing spot) or an already floor-resolved point. We descend
    // to the floor DIRECTLY BELOW it, then dismount once on or near the ground. Returns
    // true when grounded and dismounted.
    public static bool LandAndDismount(ExecutionContext ctx, Vector3 near)
    {
        if (IsGrounded())
            return true;

        // Airborne: route DOWN to the floor beneath the target before dismounting, so we do not drop
        // onto unwalkable terrain. PointOnFloor "drops the point to the floor below it", so it can only
        // ever resolve at or BELOW the target -- landable floor first, then any floor. Crucially we do
        // NOT fall back to Query.Mesh.NearestPoint here: that returns the nearest mesh point in a box
        // ABOVE and below the target and can snap to an isolated, higher, unreachable island (a tree
        // canopy, or the flight-ceiling "skybox"), which then flew the character UP to it -- the
        // reported "trying to land on the skybox" (e.g. over the Dreamtoads). The final fallback is the
        // target position itself: a live mob stands on walkable ground, so landing right on top of it is
        // always valid and never above where the mob actually is.
        if (Plugin.Condition[ConditionFlag.InFlight])
        {
            // Descend to the floor beneath the target (landable first, then any floor). If NEITHER
            // resolves -- the mob is over a shaft / void / water with no floor under it -- descend to a
            // floor under the PLAYER's OWN position instead (where we already are, so it is reachable),
            // NOT the mob's airborne position: a fly=false move to an in-air point cannot descend and
            // left the character hovering forever. If even that fails, issue no move and just dismount;
            // the KillTarget landing watchdog gives up and moves on rather than hanging here.
            var player = Plugin.ObjectTable.LocalPlayer?.Position;
            var landing = ctx.Navmesh.PointOnFloor(near, false, 7f)
                ?? ctx.Navmesh.PointOnFloor(near, true, 7f)
                ?? (player is { } p ? ctx.Navmesh.PointOnFloor(p, true, 10f) : null);
            if (landing is { } dest)
            {
                ctx.Navmesh.MoveCloseTo(dest, false, 1f);
                DebugLog.Verbose($"Mount: descending to {dest:0.0} (target {near:0.0}) before dismount");
            }
        }

        // Issue the dismount whether grounded-mounted or descending; over flat ground
        // this lands immediately, and while gliding down it completes once low enough.
        EnsureDismounted();
        return false;
    }
}
