namespace Relicable.External;

// The BossMod Reborn AoE-avoidance preset Relicable ships and auto-installs, used when
// SOME OTHER plugin drives the rotation -- i.e. under the Rotation Solver Reborn and
// Wrath Combo combat backends.
//
// WHY THIS EXISTS (the previous default was actively harmful):
//   The avoidance preset used to default to BMR's own "VBM Multibox", which contains
//   MiscAI.AutoTarget [Retarget=Always] and MiscAI.FollowSlot. AutoTarget writes
//   Hints.ForcedTarget every frame, which Plugin.ExecuteHints copies straight into
//   TargetSystem->Target -- so it HIJACKED the hard target out from under whichever
//   plugin actually owned the rotation, every frame. FollowSlot adds a goal zone toward
//   the primary target, walking the character into melee against Relicable's navigation.
//   Relicable's own config window already warned that "VBM Multibox" fights navigation,
//   and then shipped it as the default.
//
// THE FIX -- exactly one module, and it is not a targeting module:
//   MiscAI.NormalMovement is pure movement. It writes Hints.ForcedMovement, GoalZones,
//   MaxCastTime, ForceCancelCast, WantJump and SpinDirection, and never assigns
//   Hints.ForcedTarget or touches TargetSystem. Omitting AutoTarget entirely is stronger
//   than setting its Retarget track to "Never": a module absent from a preset is never
//   instantiated at all, whereas Retarget=Never still recomputes target priorities every
//   frame for no benefit.
//
// WHY THIS WORKS WITH BMR'S AI LOOP OFF:
//   Avoidance does NOT need /bmrai. BMR's Plugin.DrawUI calls ExecuteHints()
//   unconditionally, outside any AI-loop check, and RotationModuleManager.Update runs
//   every module of the active preset gated only on Preset != null. So the boss modules
//   fill AIHints.ForbiddenZones, NormalMovement turns those into ForcedMovement, and
//   ExecuteHints feeds MovementOverride -- all with the AI loop off, which is what
//   Relicable requires everywhere else (see BossModRebornCombatBackend).
//
// THE TRACK VALUES ARE LOad-BEARING:
//   Destination=Pathfind is mandatory. An unlisted track deserializes to the FIRST enum
//   member, and Destination's first member is "None", which makes Execute return early
//   having written nothing -- i.e. no dodging at all, not "dodge in place". All six
//   tracks are listed explicitly so a future reordering of BMR's enums cannot silently
//   turn this preset into a no-op.
//   Range=Any is what stops it closing to the target: BMR's melee/caster range clamp is
//   guarded by `if (rangeStrategy != RangeStrategy.Any && Player.InCombat)`, and every
//   other value drags the destination to within 2.6y (melee) or 25y of the target.
//
// LIMITS, stated honestly:
//   * It dodges only while STANDING STILL. MovementOverride.RMIWalkDetour injects
//     DesiredDirection only when ActualMove == default, i.e. when nothing else is
//     supplying movement input.
//   * It does not dodge during vnavmesh travel, and does not fight it either:
//     RMIWalkDetour skips the injection entirely while the Dalamud shared-data flag
//     "vnav.PathIsRunning" is true. Avoidance is therefore live between navigation legs
//     -- standing in a FATE, in melee on a target, waiting out a mechanic -- which is
//     when it matters.
//   * It presses no buttons other than a jump (SpecialModes=Automatic breaks Freezing).
//
// Module type name, track names and option names were all verified present in
// BossModReborn 7.5.1.35 by reflecting the installed assembly; the type name and the
// Destination/Pathfind pair also appear verbatim in BMR's own shipped
// DefaultRotationPresets.json.
internal static class BossModRebornAvoidancePreset
{
    // Distinct name so it never collides with a user-made preset, and the
    // overwrite-on-create only ever replaces our own.
    public const string Name = "Relicable Avoidance";

    public const string Json = """
{
  "Name": "Relicable Avoidance",
  "Modules": {
    "BossMod.Autorotation.MiscAI.NormalMovement": [
      { "Track": "Destination", "Option": "Pathfind" },
      { "Track": "Range", "Option": "Any" },
      { "Track": "Cast", "Option": "Leeway" },
      { "Track": "SpecialModes", "Option": "Automatic" },
      { "Track": "ForbiddenZoneCushion", "Option": "None" },
      { "Track": "DelayMovement", "Option": "None" }
    ]
  }
}
""";
}
