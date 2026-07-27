# Changelog

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
