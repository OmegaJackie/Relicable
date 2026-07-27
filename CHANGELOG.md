# Changelog

## 1.5.2.1 — "A Relic Reborn" was missing both Rowena steps

### Fixed

- **The base-relic quest table skipped Rowena entirely, so everything from the class
  weapon through Amdapor Keep was gated two sequences too late.** Reported live: parked
  on *"Speak with Rowena"* at sequence 6 with the run trying to queue the Chimera.

  "A Relic Reborn" sends you to Rowena at Revenant's Toll twice — she is the one who
  asks for the Amdapor Glyph, and she is the one who hands you the tome copy Gerolt
  wants. Neither step was authored, and the table had been closed up over the gap:

  | Journal step | Was | Now |
  | --- | --- | --- |
  | Deliver the melded class weapon to Gerolt | 5 | **3** |
  | The Chimera → Alumina Salts | 6 | **4** |
  | Deliver the Alumina Salts to Gerolt | 7 | **5** |
  | Speak with Rowena, Revenant's Toll | *missing* | **6** |
  | Amdapor Keep → Amdapor Glyph | 8 | **7** |
  | Deliver the Amdapor Glyph to Rowena | *manual* | **8** |
  | Deliver the tome copy to Gerolt | 9 | 9 |

  Both Rowena visits are now driven the same way every Gerolt turn-in is (teleport,
  approach, interact, TextAdvance carries the dialogue), so the line no longer needs a
  hand from the player between the Chimera and the beastman hunt.

  The tail of the quest — the hunt at 10, the Hydra at 12, the hand-over at 14, the three
  primals at 15–17, the delivery at 18 — was already correct and is unchanged. That is
  why this went unnoticed: only the head was shifted, and it re-converged at sequence 9.

- The class-weapon step is **one** journal entry (sequence 3), not three. It was authored
  as obtain 3 / meld 4 / deliver 5, which is where the shift originated — buying the
  weapon and melding the two materia are preparation the quest never tracks. The
  `/relic` panel for it now opens as soon as the line is underway instead of waiting for
  sequence 3, so there is time to line the weapon up while the timeworn one is fetched.

- **The final turn-in could never fire.** The oil step was gated to exactly sequence 255,
  and the last journal entry is 19. It is a lower bound now, so it runs under either
  convention. The Bard quest-path file also carried an auto-generated `Sequence: 255`
  block that would have walked you to Gerolt without the oil and then waited forever;
  it is gone, and all ten jobs finish through the same objective.

## 1.5.2.0 — BossMod Reborn avoidance no longer steals your target

### Fixed

- **The BossMod Reborn avoidance preset defaulted to `"VBM Multibox"`, which hijacked
  targeting.** That preset contains `MiscAI.AutoTarget [Retarget=Always]`, which writes
  `Hints.ForcedTarget` every frame — BossMod copies that straight into
  `TargetSystem->Target`, so it overwrote the hard target belonging to whichever plugin
  was actually running the rotation. It also contains `MiscAI.FollowSlot`, which walks
  the character into melee against vnavmesh.

  The avoidance path is active whenever the combat backend is *not* BossMod Reborn — so
  this affected the Rotation Solver Reborn backend and, as of 1.5.1.0, Wrath Combo.
  Relicable's own config window already warned that this preset fights navigation, and
  then shipped it as the default.

  Relicable now installs and uses its own **"Relicable Avoidance"** preset, containing
  exactly one module: `MiscAI.NormalMovement`. That module is pure movement — it never
  assigns `Hints.ForcedTarget` and never touches `TargetSystem`. Omitting `AutoTarget`
  entirely is stronger than setting its `Retarget` track to `Never`, because a module
  absent from a preset is never instantiated at all.

  Existing configurations are migrated once: a saved `"VBM Multibox"` becomes blank
  (= use the built-in preset). A deliberate later choice is preserved.

- The config window's AI-preset warning now applies to the **avoidance** field too, which
  is the field it was always describing. It is name-based now, so it can be asked about
  either field without the avoidance field flagging itself.

### Notes

Re-verified against the installed BossMod Reborn 7.5.1.35 that avoidance does **not**
require BossMod's AI loop: `ExecuteHints()` runs unconditionally, and the preset's
modules run gated only on a preset being active. So keeping `/bmrai` off — which
Relicable does everywhere, because BossMod's `AIBehaviour` reassigns the active preset
every frame — costs nothing here.

Two honest limits of preset-based avoidance: it acts only while you are standing still,
and it stands aside for vnavmesh while that is moving you (BossMod checks the shared
`vnav.PathIsRunning` flag). So it dodges between navigation legs, not during travel.

## 1.5.1.0 — Wrath Combo support

### Added

- **Wrath Combo is now a supported combat backend**, alongside BossMod Reborn and
  Rotation Solver Reborn. Select it in `/relic config` → Combat backend.

  Wrath is *lease-based*, which makes it different from the other two: Relicable
  registers for control, and while that lease is held Wrath names Relicable as the owner
  of the settings it drives and locks them. So the lease is taken only when combat
  actually engages — not when you select the backend or travel to an objective — and it
  is handed back both when Relicable unloads and when you switch to a different backend.
  Turning auto-rotation off is not enough to release it; without an explicit release you
  would be left unable to edit your own Wrath settings.

  Wrath has no "manual mode" switch of its own, so the neutral relic-note grind pins
  `DPSRotationMode` to Manual and clears both in-combat gates (`InCombatOnly` and
  `OnlyAttackInCombat`) — otherwise Wrath waits for combat that never starts and the
  character stands over the mob doing nothing. In FATEs it sets `FATEPriority`, and
  `BypassFATE` so the rotation can open on a FATE mob out of combat.

  Note that Wrath hard-skips auto-rotation while mounted or occupied, and no setting
  relaxes that — the dismount-before-engage ordering in the executors is load-bearing
  for this backend.

- **`Let Relicable configure Wrath's Auto-Rotation`** (on by default). Turn it off to
  have Relicable only switch auto-rotation on and off and leave the rest of your Wrath
  configuration untouched. The relic grind will then stall on neutral enemies unless you
  have already cleared Wrath's in-combat options yourself, and the config window says so.

- **`FATE targeting`** for Wrath: how it picks targets inside a FATE. Setting it to
  Manual hands targeting back to Relicable.

### Notes

The IPC surface was verified against Wrath's own source and the shipped
`WrathCombo.API.dll` rather than its published example — that example's copy of the
configuration-option enum is truncated and omits half the options used here.

## 1.5.0.0 — first public Early Alpha

The first build prepared for public release. Everything below the surface is the
same engine that has been in private development through 1.4.x; this release is
about making it installable and supportable by people other than the author.

### Added

- **Early Alpha access gate.** Relicable now requires a signed access code to run.
  Codes are ECDSA P-256 signatures issued individually, carrying the name they were
  issued to and an expiry date. The plugin ships only the public key, so codes cannot
  be forged. The issued-to name is displayed in the main window while the plugin is in
  use. See `tools/RelicableKeygen/README.md`.
- **`tools/RelicableKeygen`** — the offline generator for issuing, verifying and
  revoking access codes. It shares `AlphaCode.cs` with the plugin by source reference,
  so the minting and verifying sides cannot drift apart.
- **GitHub release pipeline** — a tagged push builds the plugin on Windows, packages it,
  and attaches it to a prerelease.
- **`repo.json`** — a Dalamud third-party repository manifest, so the plugin can be
  installed from a URL instead of built from source.
- **Issue templates** that ask for the version, stage, combat backend and debug log.

### Changed

- **Documentation rewritten for public use.** The README now describes the plugin as it
  actually is rather than as a scaffold, and states the User Agreement risk up front.
  `BUILDING.md` was corrected — it documented .NET 9 and `net9.0-windows` while the
  project has required the .NET 10 SDK and Dalamud API 15 for some time.
- **Diagnostic subcommands are gated.** `adcfg`, `adset`, `bravesseq`, `questwork`,
  `mahatma` and `prereq` still work, but only with *Enable debug log* turned on in
  `/relic config`, and they are no longer advertised in the command help. `adset` in
  particular writes into another plugin's live configuration and should not be reachable
  by typing a word after `/relic`.
- **Book dungeon territory remapping is now debug-level logging.** It previously wrote
  several lines of raw `TerritoryType` internals to the Dalamud log on every plugin load.
- The development changelog — roughly 2,900 lines living as an XML comment inside
  `Relicable.csproj` — was moved out. Release notes live here now.

### Removed

- **The Splatoon integration.** `SplatoonLocatorIpc` and the `RelicableLocator` script
  are gone, along with the `/sf` shortcut on the objective name.

  It was never reachable in practice: it required hand-loading a custom script into
  Splatoon, a step no documentation described, and both call sites already fell through
  to the authored coordinate path in every real install. It had also been progressively
  narrowed after it caused FATE staging to strand runs in the wrong ring. Both executors
  now use the authored coordinate directly — the path that was already running for
  everyone. Clicking an objective name now drops a map flag and travels there, which
  works with no extra plugin installed.

### Fixed

- Developer machine paths (`C:\Users\...`) removed from the build documentation, the
  project file, and the rotation template.
- `RepoUrl` in the plugin manifest was empty, so the in-game installer showed no project
  link.
