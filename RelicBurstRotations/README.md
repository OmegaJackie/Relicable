# RelicBurstRotations

Ten Rotation Solver Reborn (RSR) custom rotations, one per ARR Zodiac relic job, tuned for a single
purpose: killing Ifrit as fast as possible in **the Bowl of Embers (Extreme)** (TerritoryType **295**)
while farming Nexus relic light.

> **Read this first.** RSR 7.5.1.17 does **not** load rotation DLLs from disk. The built
> `RelicBurstRotations.dll` can never be discovered by the official plugin. Running these rotations
> requires compiling their source into a **fork of RotationSolver.dll**. See
> [Install](#install-there-is-no-drop-in-dll-path). Nothing in this project has been tested in-game.

---

## 1. What this is

- A class library (`net10.0-windows`, x64) containing:
  - `IfritExBurst.cs` — shared state helpers (in-territory check, opener window, Infernal Nail
    identification, "hold fire while nails are up" gate).
  - `Rotations\{PLD,WAR,MNK,DRG,NIN,BRD,WHM,BLM,SMN,SCH}_IfritEX.cs` — one `CustomRotation`
    subclass per job, each `[Rotation("... Ifrit EX Burst", CombatType.PvE, GameVersion = "7.5")]`.
- Type names, which is what RSR's config keys on:
  `RelicBurstRotations.Rotations.<JOB>_IfritEX`.
- The `.csproj` here is a **compile-verification harness only**. Its output is deliberately not
  copied anywhere. Do not add a post-build copy step.

## 2. The exact scenario optimized for

Fixed, not general-purpose:

| | |
|---|---|
| Duty | the Bowl of Embers (Extreme), TerritoryType 295 |
| Party | **solo**, **unsynced** (Unrestricted Party), level 100 |
| Weapon | an ARR Zodiac relic, iLvl 80–135 — roughly **0.50×** a geared level-100 character's damage |
| Buff | Epic Echo **+300%** (×4 damage, ×4 max HP) from entering unsynced |
| Expected kill | **10–20 s**, i.e. ~5–11 GCDs |
| Metric | wall-clock time from pull to boss death, repeated 42–63 times |

Consequences baked into every file: **the rotation *is* the opener.** Every 60 s and 120 s cooldown
fires exactly once. There is no cooldown alignment, no drift handling, no holding for a second burst
window, no AoE design (the Infernal Nails are spread around the arena ring, so AoE gains ~2 targets
at best and travel time dominates).

Two jobs are deliberately built the other way — see the table.

### The Infernal Nail rule

Ifrit spawns nails at ~50% / ~30% / ~20-10%. Since patch 4.56, **damaging Ifrit while nails are alive
can make him temporarily invulnerable**, and the failure mode is an unwinnable Hellfire loop. So:

- Every rotation calls `IfritExBurst.MustHoldFire()` and returns false out of `GeneralGCD` /
  `AttackAbility` while nails are up and the resolved target is **not** a nail.
- Nail *targeting* is driven from Relicable, not from these rotations: RSR's `targetOverride`
  parameter is provably ignored for hostile selection in 7.5.1.17, so Relicable instead pins
  `DataCenter.TargetingTypeOverride = LowMaxHP` for the duration of territory 295
  (config: **"Target Ifrit's Infernal Nails first…"**, default **ON**).

## 3. Per-job burst plan, one line each

| Job | Plan |
|---|---|
| **PLD** | Fight or Flight + Imperator at the pull, spend all Requiescat/Blade combo damage inside the one 20 s window; no second FoF exists. |
| **WAR** | Defiance up, Inner Release immediately (3 auto-CDH Fell Cleaves + Primal Rend), Inner Chaos/Upheaval spent at pull; sustain gated behind every spender. |
| **MNK** | Pre-pull Form Shift + Meditation (5 Chakra), then Brotherhood/Riddle of Fire/Riddle of Wind all at pull, Perfect Balance ×2; no Nadi banking, Phantom Rush unreachable. |
| **DRG** | Lance Charge + Battle Litany + Geirskogul + Dragonfire Dive dumped at pull; level-50-era GCDs (Vorpal/Disembowel/Full Thrust) omitted as dead at 100. |
| **NIN** | Pre-pull Hide → Ten/Chi/Jin → Suiton, then Mug/Trick/Kassatsu → Hyosho Ranryu and Ten Chi Jin; the designated "skip" job — biggest single finishing blow. |
| **BRD** | Raging Strikes + Battle Voice pre-pull, Radiant Finale/Barrage burst immediately; DoTs suppressed during the opener window (they feed the nail-phase invuln guard). |
| **WHM** | Built for the **full fight** (90–150 s), not the skip: Presence of Mind fires twice, Assize 3–4 times, Glare/Misery filler; cannot frontload hard enough to skip nails. |
| **BLM** | Pre-pull Umbral Soul/Swiftcast where reachable, then Astral Fire III → Fire IV → Flare Star (900 effective) as the big hit; Polyglot reserve off by default. |
| **SMN** | Solar Bahamut → Enkindle (1500p) → primal phases; assumes the 50% nail phase **will** happen (~20–35 s kill), so nail handling matters as much as the opener. |
| **SCH** | Like WHM, assumes the whole nail fight: Biolysis + Baneful Impaction + Broil IV filler, Energy Drain on cooldown; structurally cannot produce a skip-tier finishing blow. |

## 4. How the auto-swap works

Implemented in Relicable (**not** in this assembly):

- `Relicable\External\RsrRotationOverride.cs` — late-bound reflection into RSR:
  writes `Service.Config.Configs._rotationChoiceDict[<job>] = "RelicBurstRotations.Rotations.<JOB>_IfritEX"`,
  then calls `RotationUpdater.ChangeRotation(...)` to apply live — the same two steps RSR's own picker does.
- `Relicable\External\IfritBurstRotationSwap.cs` — polled from the framework tick (not `TerritoryChanged`,
  because RSR returns an empty rotation list until the player object and its rotation cache exist).
  On entering 295 on one of the 10 relic jobs it applies the swap; on leaving, job change, toggle-off,
  or plugin dispose it restores the previous value.
- A **breadcrumb** (`RsrRotationOverrideActive/JobId/Previous/RsrVersion` in Relicable's config) is
  persisted the moment the override lands, so a crash or reload can still undo it. It is cleared only
  after the dict write, `Configs.Save()`, and the live re-apply have all succeeded.
- If you change your rotation yourself in RSR's picker while inside 295, Relicable notices on the next
  tick, **abandons ownership**, and writes nothing back.

## 5. Install (there is no drop-in DLL path)

**`RotationLibs` in `RotationSolver.json` does not need editing — and would not help.** That key, and
`pluginConfigs\RotationSolver\Rotations\`, are dead in 7.5.1.17: `RotationUpdater.LoadBuiltInRotations()`
scans only `typeof(RotationUpdater).Assembly`. The `RebornRotations.dll` sitting in that folder is a
stale leftover from an older RSR and is never read.

The only path that can work:

1. Clone `FFXIV-CombatReborn/RotationSolverReborn` at tag **7.5.1.17**.
2. Copy `IfritExBurst.cs` and `Rotations\*.cs` from this folder into the **RotationSolver** project
   (not RotationSolver.Basic). The namespaces are file-scoped and stay
   `RelicBurstRotations` / `RelicBurstRotations.Rotations`, so no wiring change is needed and the type
   names Relicable writes into RSR's config remain correct.
   *The fork is required for compilation as well as loading:* `RotationSolver.Basic` ships
   `[assembly: InternalsVisibleTo("RotationSolver")]`, so anything touching RSR internals only compiles
   inside an assembly named `RotationSolver`.
3. Build the fork and replace
   `%AppData%\XIVLauncher\installedPlugins\RotationSolver\7.5.1.17\RotationSolver.dll`
   with your build.
4. Restart the game (or reload RSR).

**This is undone by every RSR update.** Dalamud installs each release into a new version-stamped
directory, so a fresh official `RotationSolver.dll` replaces the fork and the rotations vanish. Relicable
records the RSR assembly version alongside the breadcrumb and re-warns when it changes.

## 6. How to verify it actually loaded

1. **In RSR's UI**: open Rotation Solver → Rotations, with one of the ten jobs active. The dropdown
   should list an entry named e.g. `Ifrit EX Burst` for that job alongside the stock rotations.
   If it is not in the list, RSR did not load the fork — nothing else will work.
2. **In Relicable's config window**: the checkbox *"Use the Ifrit EX burst rotation in the Bowl of
   Embers (Extreme)"* now renders a status line beside it. Red text
   ("not found in your RotationSolver build" or similar) means the lookup failed.
3. **In the log**: Relicable emits a one-shot warning naming which lookup failed
   (`Service` / `Configs._rotationChoiceDict` / `RotationUpdater` / rotation not found).

If you see no `*_IfritEX` in the RSR dropdown, stop — the feature is inert and Relicable will change
nothing.

## 7. How to turn it off

- **The rotation swap**: Relicable config window → Combat section → uncheck
  *"Use the Ifrit EX burst rotation in the Bowl of Embers (Extreme)"*
  (`AutoSwapIfritBurstRotation`, **default OFF**). Unchecking triggers an immediate restore of your
  previous RSR rotation.
- **The nail targeting override**: uncheck *"Target Ifrit's Infernal Nails first in the Bowl of Embers
  (Extreme)"* (`PrioritiseIfritNailTargeting`, default **ON**). This releases
  `DataCenter.TargetingTypeOverride` back to whatever RSR/you had set.
- **Fully**: revert `RotationSolver.dll` by reinstalling Rotation Solver from Dalamud's plugin
  installer. With the official DLL back, both toggles become no-ops.
- Relicable never calls `Service.Config.Save()` on *apply*, only on *restore*, so simply removing the
  fork cannot leave the burst rotation baked into `RotationSolver.json`.

## 8. Status / honesty

- Both `RelicBurstRotations` and `Relicable` build with **0 errors, 0 warnings**.
- **Nothing here has been run in-game.** GCD priority correctness, actual kill times, nail-phase
  behaviour, and the reflection paths into RSR internals are reasoned from source, not observed.
