# Relicable — architecture

Orientation for people working on the plugin. For installation and usage see the
[repository README](../README.md); for the toolchain see [BUILDING.md](../BUILDING.md).

## The shape of it

Relicable is an **objective engine**, not a script. Nothing is a fixed sequence of actions.

- An **objective** is a list of steps plus a *completion condition* expressed in terms of game
  state — an item count, a quest sequence, a relic-note slot, a Light value.
- The **controller** picks the lowest incomplete objective for the current stage, runs its steps
  in order, and re-checks the completion condition against live game memory every tick.
- An **executor** handles one step type. It is a state machine that returns `InProgress`,
  `Complete`, `Failed` or `Rotate` each frame and never blocks.

Because completion is always re-derived from the game rather than from a progress file, the
engine converges on the right thing after a crash, a manual detour, or work you did by hand
between sessions. This is the single most important property to preserve when adding to it.

## Layout

```
Plugin.cs                  Entry point: service wiring, the alpha gate, commands, the tick.
Configuration.cs           Persisted per-character settings.

Controllers/
  RelicController.cs       The non-blocking objective/step state machine.
  AtmaCbtDriver.cs         Delegation to Bundle of Tweaks' Fate Tool Kit for the Atma farm.

Licensing/

Model/                     Plain records: StepType, RelicStage, StepData, RelicObjective,
                           ExecutionContext (the per-tick bundle handed to every executor),
                           ExecutorStatus, RelicJob, Materia.

Steps/                     One executor per step type. The interesting ones:
  KillTargetExecutor.cs      The kill grind: target selection, line-of-sight, outward search.
  ParticipateFateExecutor.cs FATE staging, level sync, NPC-started and prerequisite-gated FATEs.
  MoveToFlagExecutor.cs      Map flag -> navmesh floor point -> travel.
  EnterDutyExecutor.cs       AutoDuty hand-off.
  TreasureMapExecutor.cs     The Alexandrite farm, including map restock.
  Interaction/               NPC approach/target/interact, dialogue menus, leve board flows.
  Combat/                    Mount, chocobo, death recovery, combat-backend assist.

External/                  Companion-plugin IPC wrappers. Every one is HasFunction-guarded and
                           degrades to a no-op when the plugin is absent.
  ICombatBackend.cs          Backend abstraction + router (BossMod Reborn / RSR / none).
  NavmeshIpc.cs              vnavmesh.
  DependencyRegistry.cs      Live required/optional status behind the Dependencies tab.

Data/                      Static tables and objective JSON. Data/*.Generated.cs is derived
                           from XIVAPI by tools/ — regenerate, do not hand-edit.
  relics/*.json              Authored objectives (Atma, Novus, the Jalzahn upgrades).
  questpaths/*.json          Sequence-accurate A Relic Reborn quest paths.

BaseRelic/                 A Relic Reborn: quest state, prerequisites, the hunt generator.
Braves/                    Trials of the Braves books and the iLvl 125 material quests.
Novus/                     Materia route optimizer, retainer scanning, the meld runner.
Windows/                   ImGui windows. Ui.cs holds the shared primitives.
Diagnostics/DebugLog.cs    Logging facade; Verbose/Info gated by the config toggle.
```

## Adding a step type

1. Add the member to `Model/StepType.cs`.
2. Add whatever fields it needs to `Model/StepData.cs` (flat and JSON-friendly).
3. Write the executor in `Steps/`, implementing `ITaskExecutor`. Return `InProgress` and come
   back next frame rather than looping or sleeping — the tick must never block.
4. Register it in the executor list in `Plugin.cs`.

## Conventions that matter

**Never block the framework tick.** Every executor is re-entered each frame. State lives in
executor fields, not in loops.

**Derive progress, never cache it.** If you find yourself persisting "did we finish X", check
whether the game already knows. `Configuration.CompletedProceduralObjectives` exists only for
objectives with no observable game-state signal at all.

**Guard every IPC call.** Companion plugins may be missing, mid-update, or a version whose gate
does not exist. `HasFunction` first, `try`/`catch` around the invoke, sensible fallback.

**Comments explain the *why*, especially for anything calibrated against live game behaviour.**
A lot of this code encodes a fact that took a session in-game to discover — which sequence a
quest step actually sits at, that a combat backend drops an unsynced FATE mob, that a general
action no-ops during an NPC event. Those comments are the expensive part; keep them current.

**Bump the version on every change.** All three fields in `Relicable.csproj`
(`Version`, `AssemblyVersion`, `FileVersion`), so a dev install visibly reloads.

## Known rough edges

- **Auto-meld** (`Configuration.EnableAutoMeld`) drives the live materia-meld window and is off
  by default. The callback layout cannot be verified outside the game and a wrong confirm can
  shatter materia. Leave it off unless you are testing it deliberately.
- **Retainer withdrawal** uses a native sig-scanned command. It works, but it is the most
  version-fragile code in the repo.
- **The base relic's class-weapon step** (buy/craft the weapon, meld two Grade III materia)
  is not automatable and is surfaced as an annotated manual task instead.
