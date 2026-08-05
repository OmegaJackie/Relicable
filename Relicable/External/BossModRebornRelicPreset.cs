namespace Relicable.External;

// The BossMod Reborn autorotation preset Relicable ships and auto-installs, so the
// BossMod Reborn combat backend works out of the box with no manual preset setup.
//
// WHY THIS EXISTS (the "BMR won't attack" root cause):
//   BMR's shipped default preset "VBM Default" has every job module using Targeting =
//   "Auto". "Auto" lets BMR pick the best target from its priority list -- and a
//   NEUTRAL, un-aggroed relic mob that is not already a combat/hint target scores
//   priority 0, so the auto-target search returns null and the rotation never fires.
//   Relicable hard-targets the note mob and waits for the backend to swing, so the
//   result is "hard-targets it, never attacks".
//
// THE FIX -- Targeting = "Manual":
//   BMR defines Manual as "Use player's current target for all actions" (its
//   Basexan.cs, same as upstream). With Manual, every job module casts on whatever
//   Relicable has hard-targeted, and the out-of-combat handling explicitly allows
//   opening on a single manually-targeted enemy even while neutral. This is the same
//   targeting Questionable's proven "Overworld" preset uses for neutral overworld mobs.
//
// NO AI TARGET-SELECTION, ever:
//   Only the per-job rotation modules are listed -- no Melee/Tank/Ranged AI modules and no
//   MiscAI.AutoTarget. Relicable owns targeting everywhere, so BMR must ONLY cast on the
//   current hard target. This is also why the backend keeps BMR's AI loop (/bmrai) off.
//
// MOVEMENT (MiscAI.NormalMovement) IS OPTIONAL AND OFF BY DEFAULT IN THE JSON:
//   Build(withAvoidance) appends the same movement module the RSR/Wrath backends get via
//   BossModRebornAvoidancePreset. This is the ONLY way the "Use BossMod Reborn AoE avoidance"
//   checkbox can do anything under this backend: BossMod.Presets.SetActive is EXCLUSIVE, so
//   activating a second avoidance preset would evict this rotation preset and the character
//   would dodge but never attack. Merging the module into this preset is the merge that
//   exclusivity forces. Before this, the checkbox was inert under the default backend and
//   nothing ever wrote AIHints.ForcedMovement -- i.e. avoidance was never active at all.
//
//   The old objection here ("NormalMovement would fight vnavmesh") does not hold: BMR's
//   MovementOverride.RMIWalkDetour injects only while nothing else supplies movement input AND
//   the Dalamud shared flag "vnav.PathIsRunning" is false, so it stands down entirely during
//   navigation. See BossModRebornAvoidancePreset for the full track-by-track rationale.
//
//   Caveat worth knowing: under THIS backend (unlike RSR/Wrath) the job modules live in the
//   same preset and therefore read the MaxCastTime / ForceCancelCast hints NormalMovement
//   writes, so a caster may hold or drop a cast it thinks a mechanic would interrupt. Untick
//   the checkbox to drop the module again -- the backend re-installs on the next step start.
//
// Installed via BossMod.Presets.Create (idempotent, overwrite) and activated by name via
// BossMod.Presets.SetActive; BMR keeps the "BossMod." IPC prefix and the same bare
// {Name, Modules} preset JSON (verified against BMR's IPCProvider Presets.Create and its
// shipped DefaultRotationPresets.json, which uses these exact module keys and
// Track/Option strings). The module type names below are BMR's OWN type names
// (namespace BossMod.Autorotation[.xan]) and must not be renamed.
internal static class BossModRebornRelicPreset
{
    // The preset name Relicable installs + activates. Distinct so it never collides with a
    // user-made preset, and overwrite-on-create only ever replaces our own.
    public const string Name = "Relicable Combat";

    // Bare preset JSON for BossMod.Presets.Create (BMR's gate), with the movement module
    // appended when the user has AoE avoidance on. Assembled rather than a single const so the
    // checkbox can add or drop MiscAI.NormalMovement without a second (mutually exclusive)
    // preset -- see the class note.
    public static string Build(bool withAvoidance)
        => "{\n  \"Name\": \"" + Name + "\",\n  \"Modules\": {\n"
           + RotationModules
           + (withAvoidance ? ",\n" + AvoidanceModule : string.Empty)
           + "\n  }\n}";

    // The movement module, byte-identical to the one in BossModRebornAvoidancePreset (all six
    // tracks listed explicitly -- an unlisted track deserializes to the FIRST enum member, and
    // Destination's first member is "None", which makes Execute return having written nothing).
    private const string AvoidanceModule = """
    "BossMod.Autorotation.MiscAI.NormalMovement": [
      { "Track": "Destination", "Option": "Pathfind" },
      { "Track": "Range", "Option": "Any" },
      { "Track": "Cast", "Option": "Leeway" },
      { "Track": "SpecialModes", "Option": "Automatic" },
      { "Track": "ForbiddenZoneCushion", "Option": "None" },
      { "Track": "DelayMovement", "Option": "None" }
    ]
""";

    // Every job's Targeting is "Manual" so the rotation casts on Relicable's hard target
    // (including a neutral note mob). WAR uses the VeynWAR module (its AOE track auto-finishes
    // combos), matching the reference preset; both VeynWAR and the xan modules exist in BMR
    // under the same full type names. No trailing comma -- Build appends one when it needs to.
    private const string RotationModules = """
    "BossMod.Autorotation.xan.PLD": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.VeynWAR": [
      { "Track": "AOE", "Option": "AutoFinishCombo" }
    ],
    "BossMod.Autorotation.xan.DRK": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.GNB": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.WHM": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.SCH": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.AST": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.SGE": [
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.MNK": [
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.DRG": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.NIN": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.SAM": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.RPR": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.VPR": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.BRD": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.MCH": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.DNC": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.BLM": [
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.SMN": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.RDM": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" }
    ],
    "BossMod.Autorotation.xan.PCT": [
      { "Track": "Buffs", "Option": "Automatic" },
      { "Track": "AOE", "Option": "AOE" },
      { "Track": "Targeting", "Option": "Manual" },
      { "Track": "Motifs", "Option": "Downtime" }
    ]
""";
}
