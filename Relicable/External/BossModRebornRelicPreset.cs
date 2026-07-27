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
// ROTATION-ONLY (no movement, no AI target-selection):
//   Only the per-job rotation modules are listed -- NO MiscAI.NormalMovement (which
//   would have BMR pathfind and fight vnavmesh for movement) and NO Melee/Tank/
//   Ranged AI modules. Relicable owns navigation everywhere (it walks to melee with the
//   rotation off, then engages), so BMR must ONLY cast on the current target. This
//   is why the backend keeps BMR's AI loop (/bmrai) off for the grind.
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

    // Bare preset JSON for BossMod.Presets.Create (BMR's gate). Every job's Targeting is
    // "Manual" so the rotation casts on Relicable's hard target (including a neutral note
    // mob). WAR uses the VeynWAR module (its AOE track auto-finishes combos), matching the
    // reference preset; both VeynWAR and the xan modules exist in BMR under the same
    // full type names.
    public const string Json = """
{
  "Name": "Relicable Combat",
  "Modules": {
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
  }
}
""";
}
