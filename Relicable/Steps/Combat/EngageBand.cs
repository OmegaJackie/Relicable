using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;

namespace Relicable.Steps.Combat;

// How close to stand to a target before handing off to the combat backend.
//
// This replaces the flat "4 yalms, centre to centre" constant every combat loop carried, which
// was wrong in two ways.
//
// (1) IT IGNORED THE TARGET'S SIZE. The game measures melee reach from the HITBOX EDGE, not the
//     centre, so a target with a large collision hull can never be approached to 4y of its
//     centre -- the character closes until the hull stops it, several yalms short, and the
//     "am I in melee?" test stays false forever. The loop then keeps re-issuing an approach it
//     can never satisfy while standing on top of a mob it could have been hitting. This is the
//     reported "does not move into melee range" symptom. ParticipateFateExecutor was given the
//     hitbox term in 1.5.4.2; the relic-note grind and the leve runner never got it.
//
// (2) IT WAS ROLE-BLIND, marching casters and physical ranged jobs into melee for no benefit.
//
// The minimum distance is a FLOOR, not a retreat: nothing here ever walks backwards. A move is
// only ever issued when the target is beyond Engage(), so a ranged job that is already close
// simply fights from where it stands. Backing out would fight the combat backend's own
// positioning (BossMod Reborn repositions continuously) and invites a walk-out/walk-in
// oscillation, which is the same class of bug as the Off/On rotation thrash.
public static class EngageBand
{
    // Melee actions reach 3y edge-to-edge. Stop just inside that so a step of target drift does
    // not immediately read as out of range.
    private const float MeleeReach = 2.5f;

    // Where a ranged job settles: well inside the 25y action range, and close enough that terrain
    // rarely breaks line of sight -- a blocked cast is a silent no-op, so an over-long band trades
    // one stall for another (see the line-of-sight recovery in KillTargetExecutor).
    private const float RangedReach = 15f;

    // Hysteresis added on top of Engage(): once committed to a target, keep fighting until it is
    // beyond this. A single threshold made a mob wobbling around the band flip between the engage
    // branch (rotation on) and the travel branch (rotation off) every tick, and the backend never
    // settles into its rotation -- "engages but never attacks".
    private const float Hysteresis = 4f;

    // The game's default player hitbox, used when the local player cannot be read.
    private const float DefaultSelfHitbox = 0.5f;

    // Never ask the navmesh to stop closer than this; 0 would mean "stand inside it".
    private const float MinStopDistance = 1f;

    // Centre-to-centre distance at which we stop navigating and let the backend fight. Includes
    // both hitboxes, because that is how the game measures reach.
    public static float Engage(IGameObject? target)
        => Reach() + Hitboxes(target);

    // Same, for a caller that must be in true melee regardless of role (closing in to clear a
    // blocked line of sight, holding on a protected leve charge).
    public static float Melee(IGameObject? target)
        => MeleeReach + Hitboxes(target);

    // The looser band an already-engaged target may drift to before we chase it again.
    public static float Disengage(IGameObject? target)
        => Engage(target) + Hysteresis;

    // What to hand vnavmesh as its stop distance. A yalm inside Engage() so pathing overshoot
    // still lands in the band.
    public static float Stop(IGameObject? target)
    {
        var d = Engage(target) - 1f;
        return d < MinStopDistance ? MinStopDistance : d;
    }

    // Melee variant of Stop(), for the same callers as Melee().
    public static float MeleeStop(IGameObject? target)
    {
        var d = Melee(target) - 1f;
        return d < MinStopDistance ? MinStopDistance : d;
    }

    private static float Reach() => IsRanged() ? RangedReach : MeleeReach;

    private static float Hitboxes(IGameObject? target)
        => (target?.HitboxRadius ?? 0f) + (Plugin.ObjectTable.LocalPlayer?.HitboxRadius ?? DefaultSelfHitbox);

    private static readonly Dictionary<uint, bool> RangedByJob = new();

    // True when the active job fights at range. Read from the ClassJob sheet's Role rather than a
    // hardcoded job list so it is right for any job the character happens to be on, not just the
    // ten with a relic line.
    //
    // UNKNOWN RESOLVES TO MELEE ON PURPOSE. Closing further than necessary always works -- a ranged
    // job standing in melee still fights -- whereas holding at 15y on a job that turns out to be
    // melee reproduces exactly the bug this exists to fix.
    public static bool IsRanged()
    {
        var jobId = GameState.ActiveClassJobId();
        if (jobId == 0)
            return false;
        if (RangedByJob.TryGetValue(jobId, out var cached))
            return cached;

        var ranged = false;
        try
        {
            // ClassJob.Role: 0 none (crafter/gatherer), 1 tank, 2 melee DPS,
            // 3 ranged DPS (physical and magical), 4 healer.
            var role = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId)?.Role ?? 0;
            ranged = role is 3 or 4;
        }
        catch { /* leave melee; see the comment above */ }

        RangedByJob[jobId] = ranged;
        return ranged;
    }

    // "melee"/"ranged", for log lines.
    public static string RoleLabel() => IsRanged() ? "ranged" : "melee";
}
