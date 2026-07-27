# Relicable: A Questionable-Style Automation Engine for the ARR Zodiac Relic

## 1. Purpose and Scope

This document specifies Relicable: a data-driven automation engine for the
A Realm Reborn (ARR) Zodiac relic weapon line, modeled on Questionable. Relic
tooling is conventionally click-driven -- one convenience helper per task -- and
this design replaces that shape with a declarative step engine. The target is
full automation and configuration of the line, from the Atma stage through
Zodiac Zeta.

Relicable is a standalone plugin. Everything it needs either comes from the game
or is authored in this repo: the Trials of the Braves book is parsed from the
live RelicNote data, aetherytes are derived from Lumina, and the coordinates the
sheets do not carry live in Relicable's own authored tables (BraveBookPositions).

### 1.1 Stages Covered

The ARR relic line is treated as an ordered set of stages. Each stage resolves
to a list of objectives, and each objective resolves to a list of steps.

| Stage    | Objective summary                                              | Dominant step types                 |
| -------- | ------------------------------------------------------------- | ----------------------------------- |
| Atma     | Obtain 12 Atma via FATEs across 12 ARR zones                  | Teleport, MoveToFlag, ParticipateFate |
| Animus   | Fill 9 Trials of the Braves books (kill mobs, dungeons, FATEs, leves, guildhests) | Teleport, MoveToFlag, KillTarget, EnterDuty, StartLeve |
| Novus    | Upgrade with Sphere Scroll plus 75 of each materia            | InteractNpc, TurnInItems, UseItem   |
| Nexus    | Accumulate Light from FATEs, dungeons, leves, duties          | ParticipateFate, EnterDuty, WaitForCondition |
| Zeta     | Fill 9 Mahatma books (Light accumulation while in content)    | EnterDuty, ParticipateFate, WaitForCondition |
| Upgrade  | Trade and upgrade at Jalzahn or Gerolt between stages         | Teleport, InteractNpc, UpgradeRelic |

The Atma and Animus stages are the "kill random enemies" work that motivated the
redesign. They are the highest-value targets and exercise the full step
vocabulary. The Novus stage is largely menu-driven and may delegate to Artisan.

## 2. Architectural Principles Borrowed from Questionable

Questionable succeeds because it separates four concerns that a click-driven relic
helper fuses into single click handlers. Relicable adopts the same separation.

1. Data, not code. Content is declarative. Each objective is a JSON step list,
   not a hardcoded method. The engine is generic; relic knowledge lives in data
   files that can be edited, shared, and versioned independently.

2. A generic step executor. A single controller walks the active objective's
   step list, dispatches each step to a task handler keyed by step type, watches
   a per-step completion condition, then advances. Start and stop are global.

3. Delegation over reimplementation. Relicable owns no movement, combat,
   interaction, or duty code. It calls the same companion plugins Questionable
   relies on, through their inter-plugin communication (IPC) interfaces.

4. A configuration surface. A main window exposes progress and the step queue.
   A config window exposes per-feature toggles, a combat-backend selector, and
   stop conditions. State is persisted per character.

## 3. Companion Plugin Delegation

Relicable is an orchestrator. The following plugins do the actual work, mirroring
Questionable's dependency model.

| Capability             | Plugin                  | Role in Relicable                                   |
| ---------------------- | ----------------------- | --------------------------------------------------- |
| In-zone navigation     | vnavmesh                | All point-to-point movement; flag-to-point resolution |
| City fast travel       | Lifestream              | Aetheryte and aethernet routing within cities       |
| Dialogue and turn-in   | TextAdvance             | Accept and complete leves, skip cutscenes, vendor dialogue |
| Combat                 | Rotation Solver Reborn or BossMod Reborn | Selected combat backend; kills engaged targets |
| Object table targeting | Built in (Dalamud object table) | Nearest-valid-enemy resolution by name and hostility |
| Duties                 | AutoDuty                | Runs dungeons and guildhests required by objectives  |
| Crafting and materia   | Artisan (optional)      | Novus melding steps                                 |

### 3.1 vnavmesh IPC Used

The navigation surface is confirmed from vnavmesh IPCProvider.cs. Relicable uses:

    vnavmesh.Nav.IsReady                         -> bool
    vnavmesh.Query.Mesh.FlagToPoint              -> Vector3?    (current map flag)
    vnavmesh.SimpleMove.PathfindAndMoveTo        (Vector3, bool fly) -> bool
    vnavmesh.SimpleMove.PathfindAndMoveCloseTo   (Vector3, bool fly, float range) -> bool
    vnavmesh.SimpleMove.PathfindInProgress       -> bool
    vnavmesh.Path.Stop                           ()
    vnavmesh.Path.IsRunning                      -> bool

FlagToPoint is the linchpin: a map flag is already dropped at each objective
location, so the navigation destination is available without a hand-authored
coordinate table. MoveToFlag converts that flag to a navmesh point and walks to it.

### 3.2 Rotation Solver Reborn

RSR is enabled only while a valid target is engaged and disabled otherwise, so
the character does not wander into unrelated combat. RSR does not select world
mobs by name; that is the targeting layer's job (section 5.3).

## 4. The Step Model

### 4.1 Step Types

The full ARR line requires this bounded vocabulary. Each is a value of the
StepType enum and has a dedicated executor.

| Step type        | Parameters                              | Completion condition                        |
| ---------------- | --------------------------------------- | ------------------------------------------- |
| AetheryteTeleport| aetheryteId                             | Player territory equals aetheryte territory and near it |
| AethernetTravel  | aethernetShardId                        | Player near target shard                     |
| MoveTo           | position (Vector3), stopDistance        | Distance to position <= stopDistance         |
| MoveToFlag       | stopDistance                            | Distance to resolved flag point <= stopDistance |
| KillTarget       | targetName or dataId, count, fateBound  | Objective counter incremented by count       |
| ParticipateFate  | fateId or Any, count                    | Atma obtained, or FATE completion count met  |
| StartLeve        | leveId, levemeteDataId                  | Leve active                                  |
| TurnInLeve       | levemeteDataId                          | Leve allowance consumed and reward received  |
| EnterDuty        | contentFinderConditionId                | Duty complete flag set                        |
| InteractNpc      | dataId, interactionType                 | Dialogue or vendor interaction resolved       |
| TurnInItems      | itemId, quantity, npcDataId             | Inventory count decreased by quantity         |
| UseItem          | itemId, target                          | Item consumed or applied                      |
| UpgradeRelic     | npcDataId, expectedRelicItemId          | Relic item id changed to expected            |
| WaitForCondition | conditionKey, timeoutSeconds            | Named condition true (for example Light full) |

### 4.2 Objective and Stage Schema

An objective is an ordered list of steps plus metadata. A stage is an ordered or
priority-ranked list of objectives. Example, an Animus book "kill" entry:

    {
      "stage": "Animus",
      "book": 1,
      "id": "animus-1-amaljaa-thaumaturge",
      "displayName": "Amalj'aa Thaumaturge, Southern Thanalan",
      "steps": [
        { "type": "AetheryteTeleport", "aetheryteId": 17 },
        { "type": "MoveToFlag", "stopDistance": 3.0 },
        { "type": "KillTarget", "targetName": "Amalj'aa Thaumaturge", "count": 3 }
      ],
      "completion": { "kind": "BookEntry", "book": 1, "entryIndex": 0 }
    }

The flag coordinate and target name for each book entry are already known from
the book data; the data generator (section 7) emits these step lists from that
same source so the content is not authored by hand.

## 5. The Execution Engine

### 5.1 Control Loop

The controller runs on the framework update tick. It is a flat state machine:

    SelectStage     -> choose the lowest incomplete stage for the equipped relic
    SelectObjective -> choose the next incomplete objective in that stage
    RunStep         -> dispatch current step to its executor each tick
    AdvanceStep     -> on step completion, advance index; on last step, advance objective
    Stop            -> on stop condition, halt and release companion plugins

Within an Animus book, SelectObjective ranks work in the authored Atma/Books order:
enemies, then leves, then dungeons, then FATEs. FATEs are last because they only
progress while that specific FATE is up; when the target FATE has not spawned within
Configuration.FateRotateSeconds (default 180), the ParticipateFate executor returns
Rotate and selection round-robins to the next incomplete book FATE (least-recently-
tried first) instead of idling on one dead FATE.

Book auto-advance: when a book's slots are all done but the equipped weapon is still
Atma (more Trials of the Braves books remain), SelectObjective does not stop -- it
selects a synthetic BuyRelicBook objective that buys the next book from G'Jusana in
Mor Dhona (BuyRelicBookExecutor + AnimusBookData, modelled on the Remon Mahatma attach).
The book becomes the active Relic Note on purchase, so completion is the live
RelicNoteAdvanced check (RelicNote.RelicNoteId past the finished book), never persisted.
When there is no next book row (the last book is done but the weapon has not upgraded),
the final Animus weapon upgrade remains a manual step and the engine stops with guidance.

Each executor implements a single interface and returns a status each tick:
InProgress, Complete, Failed, or Rotate. The controller never blocks; long actions
such as navigation or a duty report InProgress until their completion condition holds.
Rotate is not a failure (it does not count toward the failure backoff): it means the
objective is not doable right now, so the controller re-selects a different one.

### 5.2 Executor Interface

    public enum ExecutorStatus { InProgress, Complete, Failed, Rotate }

    public interface ITaskExecutor
    {
        StepType Handles { get; }
        void Start(StepData step, ExecutionContext ctx);
        ExecutorStatus Update(StepData step, ExecutionContext ctx);
        void Stop(ExecutionContext ctx);
    }

Start is called once when a step becomes active (for example, issue the move
command). Update is called every tick to evaluate progress. Stop releases any
held resources (for example, disable RSR, call vnavmesh Path.Stop).

### 5.3 Targeting Layer

KillTarget needs a correct world target before RSR will engage. The targeting
layer queries the game object table for BattleNpc objects, filters by hostility
and by name equal to the step's targetName (the enemy name printed in the book),
and selects the nearest. For FATE-bound kills it additionally restricts to
objects inside the active FATE. This is implemented in the plugin itself, in
Data/Targeting.cs over Dalamud's object table.

Correct disambiguation here is the single most failure-prone piece and is the
main subject of testing (section 8).

### 5.4 Completion Conditions

Advancement is driven by the objective actually progressing, never by a proxy.
A KillTarget step completes when the book entry counter increments, not when a
mob dies, so a stray kill cannot desynchronize the state machine. Conditions are
sourced from: book and Mahatma counters, inventory item counts (Atma, Sphere
Scroll, materia), FATE state, duty completion flags, the Light gauge, and the
equipped relic item id.

## 6. Configuration Surface

### 6.1 Commands

    /relic            Open the main window
    /relic config     Open the configuration window
    /relic start      Begin automation from the current stage and objective
    /relic stop       Halt automation and release companion plugins
    /relic reload     Reload objective data files

### 6.2 Main Window

Shows the active relic and stage, the current objective and step, and progress
indicators: Atma collected (n of 12), Animus book fill percent, Light percent
for Nexus and Zeta, and the upcoming step queue. Provides start and stop.

### 6.3 Configuration Window

Per-feature toggles and selectors, persisted per character:

    Combat backend            : Rotation Solver Reborn, BossMod Reborn (default), or none
    Navigation                : enable vnavmesh, allow flight
    Interaction               : enable TextAdvance for leves and dialogue
    Duties                    : enable AutoDuty delegation
    Stage preferences         : preferred Atma FATE order, preferred Nexus duty
    Stop conditions           : stop on death, stop when out of leve allowances,
                                stop after N relics, stop on inventory full
    Safety                    : pause if a player targets me, pause on /tell

### 6.4 Persistence

Configuration and per-character progress are stored as JSON in the plugin config
directory. Progress is a cache; the authoritative state is always re-derived from
game memory on start so the cache can never cause an incorrect action.

## 7. Data Generation

Hand-authoring every Animus and Mahatma entry is unnecessary because the game's
own RelicNote book data is already structured. A one-time generator walks those
book definitions and emits the JSON objective files described in section 4.2: teleport
to the entry's aetheryte, move to the entry's flag, then the kill, FATE, leve, or
duty step appropriate to the entry kind. Static stages (Novus, upgrades) are
authored once as fixed step lists.

## 8. Verification Strategy

1. Targeting unit checks. Given a synthetic object table, the nearest-valid-enemy
   query must select the correct object by name, hostility, and FATE membership,
   and must reject same-name friendly or out-of-FATE objects.
2. Step executor dry runs. Each executor is exercised against a mocked execution
   context to confirm it reports Complete only when its completion condition holds.
3. Per-stage integration runs, attended. Atma first (single step type), then a
   single Animus book, then a duty objective via AutoDuty, then Novus.
4. Desync probes. Inject a stray kill and confirm the KillTarget step does not
   advance until the book counter increments.

## 9. Risk Statement

Automating movement and combat in the open world and in FATEs constitutes botting
under the FINAL FANTASY XIV User Agreement and carries a real risk of account
suspension or termination. This risk is highest for exactly the Atma, Animus,
Nexus, and Zeta stages, which occur in shared open-world content under possible
observation. This design does not reduce that risk. It should be weighed before
any development is undertaken.

## Appendix A: Verified IPC Gates

Every companion gate the code relies on was checked against current plugin
source (AutoDuty's IPC subscribers and provider, TextAdvance's IPCProvider,
Questionable's Lifestream wrapper, and vnavmesh's IPCProvider). Results:

| Plugin      | Gate                                    | Signature                          | Status in skeleton          |
| ----------- | --------------------------------------- | ---------------------------------- | --------------------------- |
| vnavmesh    | Nav.IsReady                             | Func bool                          | Correct                     |
| vnavmesh    | Query.Mesh.FlagToPoint                  | Func Vector3?                      | Correct                     |
| vnavmesh    | SimpleMove.PathfindAndMoveTo            | Func Vector3,bool -> bool          | Correct                     |
| vnavmesh    | SimpleMove.PathfindAndMoveCloseTo       | Func Vector3,bool,float -> bool    | Correct                     |
| vnavmesh    | Path.Stop / Path.IsRunning              | Action / Func bool                 | Correct                     |
| Lifestream  | Lifestream.AethernetTeleportById        | Func uint -> bool                  | Correct                     |
| Lifestream  | Lifestream.IsBusy                       | Func bool                          | Correct                     |
| AutoDuty    | AutoDuty.Run                            | Action uint,int,bool               | Fixed (was 2 params)        |
| AutoDuty    | AutoDuty.IsStopped / Stop               | Func bool / Action                 | Correct                     |
| RSR         | RotationSolverReborn.ChangeOperatingMode| Action StateCommandType (byte enum)| Fixed (was Action string)   |
| TextAdvance | TextAdvance.IsEnabled / IsBusy          | Func bool                          | Correct                     |
| TextAdvance | TextAdvance.EnableExternalControl       | Func string,ExternalTerritoryConfig -> bool | Re-scoped (see note) |
| TextAdvance | TextAdvance.DisableExternalControl      | Func string -> bool                | Correct                     |

Three corrections were required and have been applied:

1. AutoDuty.Run takes three parameters (territoryType, loops, bareMode). The
   two-parameter subscription would have thrown at invocation.
2. RSR's ChangeOperatingMode takes a StateCommandType byte enum, not a string.
   The enum is replicated locally (matching values) and a "/rotation" command
   fallback is kept; this mirrors how AutoDuty drives RSR.
3. TextAdvance has no SetPluginEnabled gate. The real control is
   EnableExternalControl / DisableExternalControl. EnableExternalControl's second
   parameter is TextAdvance's own ExternalTerritoryConfig type, which cannot be
   referenced cross-plugin; constructing it needs reflection. The skeleton
   therefore defaults to verifying TextAdvance is enabled rather than taking
   scoped external control, with the advanced path left as a documented TODO.

Two readiness subtleties worth noting: RSR's own readiness check uses the
internal name "RotationSolver" (without the "Reborn" suffix), and all gate calls
must tolerate the plugin being unloaded, so every wrapper catches invocation
exceptions and treats them as "unavailable, retry".

## Appendix B: Verified Game-Memory and Lumina Surface

Checked against current FFXIVClientStructs (aers/main), the xivdev/EXDSchema
RelicNote definition, and the modern Lumina Excel API as bundled in recent
Dalamud. The findings reshaped the completion model and the targeting layer.

### B.1 FFXIVClientStructs

| Accessor                                          | Used for                                  |
| ------------------------------------------------- | ----------------------------------------- |
| RelicNote.Instance()                              | Active Trials of the Braves book          |
| RelicNote.RelicNoteId                             | Which book is active (no "book param")    |
| RelicNote.GetMonsterProgress(int slot) -> byte    | Kills done for a monster slot (0..3)      |
| RelicNote.IsDungeonComplete/IsFateComplete/IsLeveComplete(int) | Per-slot objective flags      |
| RelicNote.IsMonsterNoteTarget(Character*) -> bool | Whether a mob counts for the active book  |
| InventoryManager.Instance()->GetInventoryItemCount(uint,...) | Atma, Sphere Scroll, materia counts |
| FateManager.Instance()->GetCurrentFateId / SyncedFateId      | FATE presence and sync state        |
| FateManager.TryGetFatePosition(ushort, Vector3*)             | Navigate to a FATE                  |

Two design consequences:

1. The completion model is now slot-based (MonsterSlot, DungeonSlot, FateSlot,
   LeveSlot) reading RelicNote directly, replacing the earlier abstract
   "BookEntry / MahatmaEntry counter" placeholders. There is only one active
   relic note, so objectives reference a slot index, not a book number; the
   active book is read live from RelicNote.RelicNoteId.

2. Targeting for monster slots now calls RelicNote.IsMonsterNoteTarget on each
   candidate, so the engine asks the game whether a mob counts rather than
   matching the clipboard name. This is robust to localization and to identically
   named non-target mobs. Name and dataId matching remain the fallback for FATE
   and leve kills that are not relic-note monster targets.

Two accessors had no clean struct field and remain honest TODOs rather than
guesses: the Nexus "Light" intensity (tracked server-side per item) and any
direct FATE-completion state (needs FateContext reading). The equipped-relic id
is read from the EquippedItems container, main-hand slot 0.

### B.2 Lumina

The data generator reads the RelicNote Excel sheet (schema confirmed: EventItem,
MonsterNoteTargetCommon[10], MonsterNoteTargetNM[3], Fate[3], PlaceNameFate[3],
Leve[3], MonsterCount[10]) and emits objectives, so Animus content is not
hand-authored. The four objective kinds map as: MonsterNoteTargetCommon -> MonsterSlot
(open-world kills), MonsterNoteTargetNM -> DungeonSlot (the notorious-monster bosses are
the final bosses of instanced ARR dungeons, run via an unsynced AutoDuty EnterDuty), Fate
-> FateSlot, Leve -> LeveSlot. A DungeonSlot's TerritoryType is resolved from the boss's
zone (with the authored BraveBookPositions territory as a fallback) and validated against the
ContentType=2 dungeon list; an entry that does not resolve to a known dungeon is skipped.

The code uses the current Lumina API, which differs from the older
GeneratedSheets it replaced. The relevant changes the implementation must follow:

- Sheets come from IDataManager.GetExcelSheet<T>() in the Lumina.Excel.Sheets
  namespace (not Lumina.Excel.GeneratedSheets).
- Rows are value structs accessed by GetRow / TryGetRow / GetRowOrDefault, not
  nullable reference classes.
- Links are RowRef<T> exposing RowId, IsValid, Value, and ValueNullable, in place
  of the old LazyRow<T>.
- Array columns are Collection<T> that are indexed or enumerated.

Code written against the old GeneratedSheets API will not compile against current
Dalamud; the generator in Data/RelicNoteDataGenerator.cs is written to the new
API as a reference.

## Appendix C: IPC Integration Hardening

The controller tick drives every dependency in real time. Two failure modes are
addressed explicitly in the External layer.

### C.1 Per-frame query cost

Status gates (vnavmesh IsReady / PathfindInProgress / IsRunning, AutoDuty
IsStopped, Lifestream IsBusy, TextAdvance IsEnabled / IsBusy) are read through
Cached<T> (External/Ipc/Cached.cs), a TTL memoizer. Polling a gate every tick
collapses to one underlying cross-plugin call per TTL window:

| Gate class                         | TTL    | Rationale                          |
| ---------------------------------- | ------ | ---------------------------------- |
| vnavmesh PathfindInProgress/IsRunning | 15 ms | a step acts on it within the frame |
| vnavmesh IsReady                   | 100 ms | changes rarely                     |
| AutoDuty IsStopped, Lifestream IsBusy | 50 ms | slow-changing duty/travel state    |
| TextAdvance IsEnabled/IsBusy       | 100 ms | user setting, slow                 |

A command that changes a polled value invalidates the relevant cache so the next
read is fresh (for example issuing a move invalidates IsRunning).

### C.2 Command re-firing

Commands are edge-triggered, not level-triggered. Executors may call a command on
every tick; the wrapper sends it to the plugin only when the request changes:

- RSR mode: EnableAuto / Disable go through EdgeTrigger, so RSR receives a mode
  change only on an actual Off<->Auto transition, not every frame. KillTarget and
  ParticipateFate call ResyncNextDispatch in Start so a fresh fight re-arms RSR
  even if it self-disabled after the previous combat.
- vnavmesh movement: MoveCloseTo is deduplicated on (destination, fly, range) and
  only re-issued when the request changes or movement has stopped short, so
  holding a destination does not restart pathfinding.
- AutoDuty.Run: latched per territory; re-calling for the same duty is a no-op
  until ResetRun (invoked when the duty step ends), preventing a duty restart.

### C.3 Readiness

Every gate call is guarded by ICallGateSubscriber.HasFunction, so an absent or
unloaded dependency never throws; queries return a safe fallback and commands
become no-ops. Each wrapper exposes an Available flag (its key gate's
HasFunction). The controller's Start refuses to begin and reports
MissingRequiredDependencies when a configured-on dependency's gate is not live,
so a run never starts blind. The config window's Dependencies tab is the visual
counterpart: it lists every companion with its installed / loaded / IPC-live
status (resolved by DependencyRegistry from InstalledPlugins plus each wrapper's
Available flag) and, for anything missing, offers an Open-GitHub button and a
Copy-Dalamud-repo button so the user can install it without leaving the game. RSR additionally has a "/rotation" command fallback,
so it can still drive combat mid-run even if only the command path is available;
the Available gate check is about confirming the real-time IPC path before start.

### C.4 Threading note

All wrapper calls are made from the controller Tick, which runs on the Dalamud
framework Update thread; IPC and game-memory reads must stay on that thread. No
wrapper spawns its own thread or schedules async continuations, so this invariant
holds by construction. If a future step needs vnavmesh's async Pathfind (Task
returning), it must marshal the result back onto the framework thread before
acting on it.

## Appendix D: Diagnostics and Teleporter

### D.1 Debug logging

Diagnostics/DebugLog.cs is a gated facade over IPluginLog. Verbose and Info are
emitted only when Configuration.EnableDebugLog is on (toggled live from the
config window); Warn and Error always emit. Every line is prefixed "[Relicable]"
for easy filtering.

Log points are placed where they answer "what is the engine doing right now"
without per-frame spam: controller Start/Stop, objective selection, step
begin/complete/failed, and — importantly for the real-time-data concern — the
actual IPC command dispatches after edge-trigger filtering (RSR mode changes,
vnavmesh MoveCloseTo issues, AutoDuty Run). Because those log calls sit past the
EdgeTrigger, the log shows the true command stream sent to each plugin, not the
per-tick calls the executors make, which is exactly what you want when verifying
that real-time data is flowing correctly and not flooding.

### D.2 Teleporter

AetheryteTeleportExecutor implements teleportation via Telepo (verified against
current FFXIVClientStructs):

- TryGetDestinationTerritory calls UpdateAetheryteList and reads the destination
  TerritoryId straight from the TeleportList entry, so the arrival check is exact
  and needs no Lumina lookup. If the aetheryte is not in the list (locked), the
  step fails fast with a logged reason.
- The phase machine polls each tick: skip if already in the destination territory;
  otherwise issue Telepo.Teleport, wait through the cast and the BetweenAreas zone
  transition, and complete when in the destination territory and controllable.
- Robustness: up to three attempts with a 15 s per-attempt timeout, then Failed,
  so a cancelled or interrupted teleport re-issues rather than hanging.

All Telepo and condition reads happen on the framework tick thread, satisfying
the threading invariant in Appendix C.

## Appendix E: NPC Interaction

NPC steps (InteractNpc, StartLeve, TurnInLeve, UpgradeRelic) share one phase
machine, NpcInteractor (Steps/Interaction), verified against current
FFXIVClientStructs (TargetSystem.InteractWithObject, SetHardTarget).

Phases polled each tick: Locating (NPC not in the object table yet, so move toward
its approach position to stream it in) -> Approaching (navigate within 4 yalms via
vnavmesh) -> Interacting (set hard target and fire InteractWithObject, throttled to
once per 600 ms) -> InDialogue (TextAdvance carries the conversation) -> Done when
the in-event condition clears, or Failed on a 30 s timeout.

The "am I in a conversation" question is answered by EventConditions over Dalamud
condition flags (OccupiedInQuestEvent, OccupiedInEvent, OccupiedSummoningBell,
OccupiedInCutSceneEvent, cutscene flags). List menus that TextAdvance does not
auto-pick (SelectIconString for leve choice, SelectString for the relic upgrade
option) are handled by DialogueMenu, which fires the addon callback with the entry
index.

Completion is per step: InteractNpc, StartLeve, and TurnInLeve complete when the
conversation ends (the leve-kill objective is confirmed separately by the
RelicNote LeveSlot flag); UpgradeRelic completes only when the equipped relic item
id becomes the expected value, and fails if the dialogue ends without the upgrade
taking, so the controller re-plans.

The upgrade-option index remains a targeted TODO (which SelectString entry is the
correct upgrade for a given relic/stage; currently index 0).

### F.3 Leve list cycling

The leves a Trials-of-the-Braves book asks for are not always on the levemete's
current offering; the offered set must be rerolled by completing other leves.
StartLeveExecutor handles this as a loop whose completion is the book's LeveSlot
(RelicNote): if a leve is accepted it runs to completion (LeveRunner), otherwise it
opens the levemete and accepts the target if offered, or accepts a filler leve to
reroll when it is not. Filler choice skips non-leve entries (cancel/quit/reset) and
the target, rotating by cycle. The loop is gated by leve allowances
(QuestManager.NumLeveAllowances) and a cycle cap, failing with an actionable log
when out of allowances.

The flow is aligned to Battlevest (NightmareXIV): the levemete is opened, the
Battlecraft category is chosen from its SelectString menu, offered leves are
accepted from the GuildLeve board, and each accepted leve is run by LeveRunner.
LeveRunner resolves the objective position from Leve.LevelStart -> Level, navigates
via vnavmesh, engages with the combat backend plus CombatAssist (matching
Battlevest's RSR + BossMod Reborn during the leve), and completes when the leve leaves the
active list (after confirming it became active, avoiding accept-latency
false-positives), with a 300 s per-leve timeout. The reroll the user described
happens naturally: accept the offered set, run them, re-open the board.

Two seams remain, both in LeveBoard/LeveRunner: AcceptOffered (reading and
accepting leves from the GuildLeve board -- blueprint is Battlevest's
RecursivelyAcceptLeves / Callback.Fire), and guildleve initiation for leves that do
not start on arrival (Battlevest Utils.Initiate; BoundByDuty marks a started leve).
The category select and board close are wired.

## Appendix F: FATE and Leve Handling

### F.1 FATE participation

ParticipateFateExecutor (Atma and Nexus stages) resolves a FATE, navigates into
its ring, and clears it. FATE state is read through Dalamud's IFateTable and was
verified against current FFXIVClientStructs (FateContext.State/Progress/Location/
Radius; FateState Running/Ended/Failed).

Per tick: pick the target FATE (the specific FateId for a book FATE, else the
nearest Running FATE for Atma's "any FATE in zone"); if outside ~0.7x the ring
radius, move toward the center via vnavmesh with the combat backend idle; once
inside, stop and engage the nearest hostile (Targeting.EngageNearestHostile, added
because the name/dataId Engage path rejects an empty query) with RSR on. The step
completes when the FATE reaches 100 percent or leaves Running. Because the stage
objective is confirmed separately (Atma by ItemCount, book FATEs by
RelicNote.IsFateComplete), one FATE per step plus controller re-selection forms
the repeat loop.

Book-FATE rotation: a book FATE objective is generated with an AetheryteTeleport to
the FATE's zone (from the authored BraveBookPositions territory table) ahead of the
ParticipateFate step, so a FATE in a different zone from the enemy/leve work is
actually reachable. The rotation clock starts when the ParticipateFate step begins
(i.e. after that teleport), so it measures the in-zone wait for the FATE to spawn.
If the FATE has not gone active within Configuration.FateRotateSeconds (default 180;
0 or less disables it), the executor returns Rotate and the controller round-robins
to the next incomplete book FATE. The Atma "any active FATE" mode (FateId 0) never
rotates, since there is nothing else to fall back to.

Level sync: once inside the ring, if not already synced to the FATE the executor
issues the native "/levelsync" command (it toggles, so it is only sent when
unsynced, throttled to let it register). Sync state is read from FateManager
(SyncedFateId vs current FATE).

### F.2 Leve selection

StartLeveExecutor interacts with the levemete via the shared NpcInteractor, then
selects the correct leve from the SelectIconString list by name rather than a
guessed index. DialogueMenu.SelectByText reads the addon's AtkValue string entries
(verified AtkValueType: String/ManagedString/ConstString), matches the leve name
resolved from the Lumina Leve sheet, and fires the list callback with the matching
ordinal. Selection is one-shot per step; TextAdvance carries the difficulty and
confirmation prompts. TurnInLeve is a plain interact-and-confirm.

Caveat documented in code: SelectByText assumes the Nth string AtkValue maps to
callback index N, which holds for SelectString and the leve SelectIconString
lists; an addon that interleaves non-entry strings would need Select(index) with a
known index instead.

## Appendix G: Equipped-Relic Guard

The Trials of the Braves (Animus) and Mahatma (Zeta) books only advance while the
matching relic weapon is equipped; RelicNote.Instance() reflects the equipped
relic, so with the wrong or no weapon equipped the kill/FATE/leve loop would find
no valid targets and make no progress -- silently.

The controller guards against this at objective selection (EquippedRelicOk):

- Selection is biased toward the objective whose book matches the active relic
  note (RelicNote.RelicNoteId), so the engine grinds the book that is actually
  equipped rather than the lowest-numbered one.
- Before running a relic-note objective (MonsterSlot/DungeonSlot/FateSlot/
  LeveSlot), it checks that a relic note is active and that its book matches the
  objective. If no note is active, or a different relic is equipped, it logs an
  actionable warning ("equip the matching relic, then /relic start") and stops
  rather than grinding uselessly.
- Objectives may also carry an explicit RequiredWeaponItemId; if set, the equipped
  main-hand item id (read from the EquippedItems container) must match.

Auto-equipping is deliberately not done: equipping the wrong job's weapon or a
glamoured item is riskier than pausing for the user to equip the intended relic.
A gearset-based auto-equip could be added later behind a config toggle.

## Appendix H: Novus and Nexus

The Novus per-stat allocation is not exposed by FFXIVClientStructs (the ARR stat
bars are legacy and unmapped), so that stage is built on a readable signal (materia
consumed) rather than an invented read. The Nexus Light value, however, IS readable:
it lives in the equipped Novus relic's InventoryItem.SpiritbondOrCollectability
(0..2000), verified against the in-game Light readout -- so Nexus uses a
real Light gauge (see H.2). The Zeta Mahatma progress is likewise readable from the
equipped Zodiac Braves weapon's SpiritbondOrCollectability (see H.3).

### H.1 Novus (materia melding)

Base upgrade reuses InteractNpc + UpgradeRelic (trade the Sphere Scroll at
Jalzahn; completion by the equipped relic item id changing).

Stat allocation is the new MeldMateria step. Because the per-stat bars are not
readable, completion is tracked by materia consumed from the inventory (readable):
the step finishes once Count materia have been attached, and fails if the player
runs out first so they can restock. A 120 s timeout prevents a hang. The single
remaining seam is RelicMeld.TryAttachOne, which must drive the live affix UI to
attach one materia; with it stubbed the step simply times out (no false progress).

### H.2 Nexus (light farming)

Light IS readable: the equipped Novus relic stores its current Light (0..2000) in
InventoryItem.SpiritbondOrCollectability (GameState.TryGetNexusLight), so Nexus uses
a real gauge. The objective's completion is CompletionKind.LightGauge, full at 2000
(GameState.IsLightGaugeFull), and the main window shows a live 0/2000 bar whenever a
Novus relic is equipped.

The farm is delegated to AutoDuty. The Nexus objective is a single EnterDuty step;
EnterDutyExecutor recognises the LightGauge objective and runs the configured farm
duty (Configuration.NexusFarmTerritoryType, default 295 = the Bowl of Embers
(Extreme)) UNSYNCED / UNRESTRICTED -- it sets AutoDuty's DutyMode=Trial and Unsynced
(so a max-level character can solo the level-50 trial), then Run(territory, loops)
with NexusFarmLoops as a safety cap (~65 fills 2000 worst-case). Because Light is read
live, the farm auto-stops the instant the gauge reaches 2000 (mid-loop if necessary),
and the LightGauge completion re-arms the farm until then. The farm duty is
configurable, so the user can point it at whichever duty currently carries the
rotating Light bonus. The non-Nexus EnterDuty path (Animus dungeons) still queues the
step's own TerritoryType and Loops, synced, via AutoDuty's default mode.

### H.3 Zeta (Mahatma charging)

Like Nexus, Zeta progress IS readable: the equipped IL125 "Zodiac Braves" weapon packs
it into SpiritbondOrCollectability, decoded against the in-game Mahatma readout
(GameState.TryGetMahatma): completed = sb / 500 (0..12 Mahatma awakened), the current
Mahatma's fill = (sb % 500) / 2 (0..40, with the raw==1 "attached at 0 points" sentinel),
and attached = sb % 500 != 0. There are 12 Mahatma at 40 points each, charged one at a
time; the main window shows a live 12-Mahatma + current-fill tracker whenever a Braves
weapon is equipped.

Because Light only charges an attached Mahatma and each next Mahatma is bought from Remon
(Swiftperch, 50 Poetics), the stage is one re-arming objective (CompletionKind.MahatmaGauge,
complete at 12) with two steps: AttachMahatma then EnterDuty. AttachMahatma
(Steps/AttachMahatmaExecutor.cs) is a no-op while a Mahatma is attached and otherwise
teleports to Remon (ZetaData resolves his aetheryte + position from Lumina), picks the
"mahatma" SelectString option, and confirms the Poetics cost; success is verified from
memory (the remainder becoming non-zero), never a proxy. EnterDuty farms the configured
unsynced duty (Configuration.ZetaFarm*, default 172 = the Aurum Vale; DutyMode is
auto-resolved from the content type via Data/DutyInfo.cs) and auto-stops the instant a
Mahatma awakens, so the loop re-attaches the next. The grind (farm + 12x Remon attach) is
fully automated; the one-time final Jalzahn awakening is left to the player because it
requires the relic unequipped, and the tracker flags when all 12 are charged.

The one seam is Remon's SelectString option text, which no offline source exposes; it is
matched on the "mahatma" substring and the step fails (rather than false-completing) if
the attach does not register, so a wrong needle stalls safely.

## Appendix I: Combat Assist and Death Recovery

### I.1 Death recovery

RecoverOnDeath (config; replaces the old StopOnDeath) turns death into a recovery
rather than a halt. DeathRecovery (Steps/Combat) detects death (player HP 0 and not
zoning), issues the Return general action to send the player to a home aetheryte,
and on revival signals the controller to resume. The controller re-selects the
current objective, which begins with a teleport, so the character re-navigates and
picks up the section it was on. The Return action is GeneralAction 8 (verified
against the GeneralAction sheet: 7 Teleport, 8 Return).

The other stop/pause conditions (PauseIfTargetedByPlayer, PauseOnTell,
StopAfterNRelics, StopWhenOutOfLeveAllowances) are intentionally left unwired and
default to off, per request.

### I.2 Chocobo companion

During the kill grind and FATEs the combat executors call CombatAssist, which keeps
the chocobo summoned (Gysahl Greens via ActionManager) and in Healer stance
(ActionType.BuddyAction), throttled and skipped inside instanced duties. Presence is
read from Dalamud's IBuddyList. Healer Stance is BuddyAction 7 (verified against the
BuddyAction sheet: 5 Defender, 6 Attacker, 7 Healer).

### I.3 RSR rotation and BossMod Reborn avoidance in FATEs

In FATEs under the RSR backend, RSR OWNS targeting: ICombatBackend.OwnsFateTargeting is
true for RSR, so ParticipateFate navigates into the ring, level-syncs, and grounds, then
calls ConfigureForFate + EnableAuto and lets RSR auto-detect the active FATE and
auto-select/attack its mobs. Relicable does NOT hard-target or Attack1-mark each FATE mob
under RSR -- that fought RSR's own FATE target selection. ConfigureForFate applies RSR's
FATE settings from Configuration (HostileType, IgnoreNonFateInFate, TargetFatePriority via
OtherCommand Settings, mirroring AutoDuty's verified pattern, plus a FATE-scoped TargetFreely
via RSR's EnableTargetFreelyOverride/DisableTargetFreelyOverride IPC so it never leaks into
the neutral relic-note grind). Level-sync stays load-bearing: RSR's IgnoreNonFateInFate drops
any mob whose FateId does not match the player's synced fate. Under BossMod Reborn
(OwnsFateTargeting false) Relicable keeps setting the hard target itself and BMR only
rotates on it.

AoE avoidance is delegated to BossMod Reborn (FFXIV-CombatReborn/BossmodReborn) through
BossModRebornIpc (verified gates BossMod.Presets.SetActive / ClearActive / GetActive --
BMR keeps the "BossMod." IPC prefix). CombatAssist activates a configured avoidance
preset while fighting and clears it on stop, edge-triggered so it is sent once; the
clear is guarded by Presets.GetActive so a preset the user activated by hand survives.
The preset must be created in BMR and named in config (BossModRebornAvoidancePreset,
default BMR's shipped "VBM Multibox"), and should be avoidance-only (its strategy
tracks set so it does not run the rotation) so it does not fight RSR's rotation.
BossMod Reborn appears in the Dependencies tab.

### I.4 Pluggable combat backend (RSR or BossMod Reborn)

The combat driver is abstracted behind ICombatBackend (EnableAuto / EnableManual /
Disable / ConfigureForFate / ResyncNextDispatch) so RSR is not hard-wired. Two
implementations exist: RotationSolverIpc (RSR) and BossModRebornCombatBackend (BMR's
own autorotation). CombatRouter dispatches ctx.Rotation to whichever Configuration.Backend
selects (RSR, BossMod Reborn, or a NullCombatBackend for "None") at call time, so the choice
changes live and switching away disables the previously-selected driver. The executors
are backend-agnostic: they already set the hard target themselves (KillTarget marks and
hard-targets the mob; the FATE/leve executors call EngageNearestHostile before
enabling), so a backend only has to run the rotation on the current target.

Under the BossMod Reborn backend, EnableAuto/EnableManual both activate a rotation
preset via BossMod.Presets.SetActive and Disable clears it; BossModRebornCombatBackend
shares the BossModRebornPresetControl helper with the avoidance path. The preset is
Relicable's own shipped, auto-installed "Relicable Combat" (BossModRebornRelicPreset --
rotation-only job modules, every Targeting = Manual) unless the user names a valid
custom one (Configuration.BossModRebornCombatPreset). Activating a preset is sufficient
to run the rotation -- BMR executes the active preset's modules on the player's hard
target every frame, with no separate "autorotation on" toggle needed (verified in
RotationModuleManager / ActionManagerEx). The backend force-sends /bmrai off on every
engage (edge-triggered): under BMR the AI loop CANNOT coexist with an IPC-activated
preset -- /bmrai on nulls the active preset via SwitchToIdle, and while engaged
AIBehaviour reassigns the active preset every frame from the AI's own preset slot
(AIManager.cs / AIBehaviour.cs:113), which would stomp the rotation. FATEs need no AI
help: the FATE executor navigates into the ring and closes on each mob itself, and the
Manual-targeting preset rotates on that hard target. Because the preset is
rotation-only, BMR never
repositions the character and vnavmesh keeps full navigation control. A consequence is
that the BossMod Reborn backend does NOT provide AoE avoidance (avoidance is the
NormalMovement module, i.e. movement control, which would fight vnavmesh); this is an
accepted trade for the trivial ARR relic content. CombatAssist therefore does not
activate the separate avoidance preset in this mode -- not because the combat preset
avoids, but because SetActive is exclusive and a second preset would clobber the
rotation. With BossMod Reborn selected, RSR is no longer a required dependency and can
be uninstalled.

The neutral-mob pull -- the crux of dropping RSR -- is source-confirmed to work: the
open-world grind targets note mobs that never aggro, and BMR assigns a not-in-combat
mob priority -3 ("can be attacked if targeted manually by a player"), which single-target
actions ARE allowed to cast (only priority -4 "forbidden" is blocked in ActionManagerEx);
the job modules explicitly handle an out-of-combat hard target. Because Relicable always
hard-targets the note mob, BMR pulls it. This is still worth an in-game smoke test as
the acceptance gate. (Note a rotation-only preset does no auto-targeting, so it acts
strictly on Relicable's hard target -- which is exactly what the executors set each tick,
including re-targeting successive FATE mobs. A user who wants BMR to auto-select
FATE/neutral mobs can instead use a preset that adds MiscAI.AutoTarget with Everything/
FATE enabled and still omits the movement modules.)

## Appendix J: Stage Selection and Novus Materia Routing

Three features were added on top of the original engine: a manual stage override, a
cheapest-route materia planner for the Novus stage, and retainer materia sourcing.

### J.1 Manual vs auto stage selection

The controller originally always worked the lowest incomplete stage, so once a stage
was passed it could not be revisited. Configuration gained StageMode (Auto, Manual)
and ManualStage (a RelicStage the user inserts). In SelectNextObjective, Manual mode
restricts the objective pool to ManualStage, which lets a farmable stage that the
engine considers complete (more Atma, Alexandrite, Light) be re-run; Animus still
honours the equipped-book guard. RelicController.Replan re-selects immediately when
the UI changes the mode or stage, so the choice applies without waiting for the
current step. Auto mode is unchanged.

### J.2 Novus route optimizer

The Novus stage fills a Sphere Scroll with 75 materia (Paladin: 53 on Curtana + 22 on
Holy Shield). The rules, verified from the FFXIV ConsoleGamesWiki Sphere Scroll and
Novus/Quest pages, are encoded in Data/MateriaCatalog.cs:

- Each successful meld is +1 point in one secondary stat; grade does not change the
  point value. Seven materia types map to seven stats (Heavens' Eye -> Direct Hit,
  Quickarm -> Skill Speed, Savage Aim -> Critical Hit, Piety, Savage Might ->
  Determination, Quicktongue -> Spell Speed, Battledance -> Tenacity).
- A stat fills grades in ascending order in fixed tier sizes (standard 11/11/11/11,
  cap 44; Piety 11/11/9, cap 31; healer Direct Hit 2/11/11/11 with a +9 base, cap 35;
  Paladin Curtana 7/8/8/8, Holy Shield 4/3/3/3). This makes the "max 11 per type and
  grade" and "go in order" rules hold by construction.
- Failed melds destroy the materia but never the Alexandrite. Expected materia per
  tier is derived from the per-position success curve (standard 100x6 then
  96/90/82/72/60; Paladin curves differ).

MateriaRouteOptimizer.cs solves, per scroll, an exact dynamic program over (stats
used, points placed) that minimises gil to land the required melds, choosing any
stats (the user picked "cheapest to finish, any stats"), at most MaxMateriaStats
distinct stats. The cost of placing p points in a stat is the sum over its grade
tiers of (unit price) x (expected materia for that tier). Unpriced grades take a
large finite penalty so the solver prefers priced options yet still completes. The
DP is pure and was validated against a Python reference: points always sum to the
target, caps and the stat-count limit always hold, the model spreads stats when
grade prices are flat (minimising fail-waste) and concentrates into two stats when
deep grades are cheap (matching the wiki's "focus on 2-3 stats" advice).

The result (Model/Materia.cs: MateriaRoute -> ScrollRoute -> RouteLine) is an ordered
route with, per line, the stat, grade (material level), successful melds, expected
materia to stock, unit price, and line cost, plus per-grade subtotals and a grand
total. The NovusWindow renders it and the held-vs-needed counts.

### J.3 Universalis pricing

External/UniversalisClient.cs fetches cheapest listings from the Universalis
aggregated endpoint (api/v2/aggregated/{market}/{ids}, reading nq.minListing.{scope})
at world, data-centre (default), or region scope. The market name is auto-resolved
from the logged-in character's home world / data centre / region, or overridden in
config. The HTTP call runs on a background task (the one deliberate exception to the
framework-thread rule in Appendix C.4): it touches no game memory or IPC, only a
concurrent dictionary of prices that the framework thread reads. Results are TTL
cached (30 min) and refetched on demand or when the market/scope changes.

### J.4 Retainer materia (AutoRetainer + native scan)

The user asked to source materia from retainers. AutoRetainer's IPC was verified to
expose retainer NAMES, gil, and venture state but NOT item-level inventory
(OfflineRetainerData carries only an MBItems count). So:

- External/AutoRetainerIpc.cs enumerates retainers (GetRegisteredCIDs +
  GetOfflineCharacterData, read by reflection since the type is cross-ALC) and can
  suppress AutoRetainer while Relicable drives the bell. It is optional.
- Novus/RetainerScanner.cs reads the active retainer's bags from game memory whenever
  a retainer is open at the bell (whether the player or AutoRetainer opened it) and
  caches catalog-materia counts per retainer in Configuration, so the planner reports
  retainer stock even offline. It is ticked every frame, throttled, with debounced
  saves.

### J.5 Novus melding execution and seams

StepType.MeldNovusRoute (Steps/NovusExecutors.cs: MeldNovusRouteExecutor) computes
the route from the planner on Start and melds it strictly in order, advancing per
line by materia consumed. When a line's materia is short in bags and
AutoWithdrawFromRetainers is on and a retainer holds it, it drives the retainer
retrieve; otherwise it stops with an actionable message pointing at the Novus window.

Auto-meld (Steps/RelicMeld.cs) is implemented but EXPERIMENTAL and opt-in
(Configuration.EnableAutoMeld, off by default). When enabled and the player has the
Materia Melding window open on the Sphere Scroll, it selects the route's materia by
item id from AgentMateriaAttach and fires the meld + confirm callbacks. It is gated
because the confirm callback layout cannot be verified outside the game and a wrong
confirm could shatter materia; on the first attempt with the debug log on it dumps
the addon's AtkValues so the exact indices can be confirmed. Progress is still judged
by materia consumed (the per-stat success bar is unreadable, per Appendix H.1), so an
ineffective callback stops the step fast rather than reporting false progress.

Retainer retrieve (Steps/RetainerWithdraw.cs) is implemented: it moves a materia stack
from the open retainer into the bags via InventoryManager.MoveItemSlot (find the
materia in RetainerPage1..7, move to the first empty player slot). It fails safe (no
retainer open or no free slot = no-op) and needs an in-game check of MoveItemSlot's
retainer->player behaviour.

The planner is also a self-contained tool. Novus/NovusActionRunner.cs is ticked by the
plugin independently of RelicController, so the Novus popout (/relic novus) can Infuse
(drive the meld window via RelicMeld) and Fetch from Retainer (pull the route's materia
via RetainerWithdraw) without starting the main automation. Both run from the
progress-aware route; progress is judged by real inventory change, so an ineffective
live-UI call stops fast.

### J.6 Farmable targets (Alexandrite)

Farmable quantities are user-set numbers rather than fixed content. Configuration
gained AlexandriteTarget; the treasure-map farm (TreasureMapExecutor) runs until the
held Alexandrite reaches it, and the farm objective's completion is the dynamic
CompletionKind.AlexandriteCount (held >= target) instead of a one-time procedural
flag -- so raising the number re-arms the farm and lets a "finished" farmable stage
be worked again. A positive target is authoritative over the endless-farm toggle.

## Appendix K: Base Relic (A Relic Reborn)

The base 2-star weapon -- the "A Relic Reborn" quest that precedes Atma -- is modeled
as a new lowest stage, RelicStage.Relic (inserted before Atma; the enum is persisted
by string so the renumber is safe, and the MainWindow manual-stage combo string was
updated to match). This first pass is the data + readiness foundation only:
authoring the per-job content and a live "is it done?" checker. In-zone routing and a
configuration panel are deliberately deferred to a later pass.

### K.1 Content model

Content is authored as typed C# (Relicable/BaseRelic), the same posture as
MateriaCatalog/NovusData rather than the engine's step-list JSON, because the
base-relic data is reference data, not yet an objective step list. BaseRelicData holds
the global prerequisites, the ten ordered quest parts (shared structure), and a
per-job table (JobRelicData) for all ten relics: the finished weapon name (Curtana,
Bravura, ...), the Part 1 broken-weapon stronghold, the Part 2 class weapon plus its
two Grade III meld materia and crafting ingredients, and the Part 5 beastmen (three
targets, eight each). Every NPC/stronghold/trial location is captured as a MapStop
(TerritoryType id plus the wiki's map x/y); the world height (FFXIV world Y) is left
to be resolved at navigation time by vnavmesh PointOnFloor/NearestPoint, so it is not
stored. Those MapStops are the authored input the deferred routing pass consumes.

### K.2 Id resolution

BaseRelicCatalog resolves every referenced English name to a game id once, cached,
exactly like MateriaCatalog: a name -> row-id map is built from the Item sheet (normal
items), the EventItem sheet (the Amdapor Glyph key item), and the Quest sheet
(prerequisite and relic quests). Matching is case- and apostrophe-insensitive (U+2019
and U+2018 fold to U+0027) so "Wildling's Cesti" resolves regardless of which
apostrophe glyph each side uses. Unresolved names are collected and surfaced as
Unknown rather than throwing.

### K.3 Job detection

The reported job is the BaseRelicJobOverride when set, otherwise auto-detected from the
equipped job via RelicJobs.FromClassJobId (read from GameState.ActiveClassJobId, which
reads IPlayerCharacter.ClassJob off IObjectTable -- IClientState does not expose
LocalPlayer in this Dalamud). Reading the job, not the base class, disambiguates
Arcanist into Summoner or Scholar whenever a job stone is on; bare Arcanist resolves to
None and relies on the override. This implements the requested "show the job whose
weapon you are holding, with a manual override."

### K.4 The prerequisite checker

PrerequisiteChecker.Build reads everything live and returns a PrerequisiteReport. What
is authoritative: the global prerequisite quests (QuestManager.IsQuestComplete -- the
static member function verified to mask the full Quest row id to a ushort), level 50 on
the relic job (exact when the job is active), material availability for the shopping
list (inventory count plus the cached retainer scan), and the live relic-quest sequence
(QuestManager.GetQuestSequence). Per-part drop possession is a positive-only signal:
holding White-Hot Ember proves Part 7's fight was done (pending turn-in), but not
holding it does not prove the opposite, since the item is consumed on turn-in. The
report's PrerequisitesMet flag gates only on the quest prerequisites and the level
requirement; materials and per-part progress are reported but never gate it.

### K.5 Retainer material sourcing

The "is this material on a retainer" check reuses the Novus bell-scan machinery.
GameState.ScanOpenRetainerMateria was generalized to ScanOpenRetainerItems (any item-id
set), and RetainerScanner now scans both the Novus materia catalog and the base-relic
material catalog in the same bag pass, caching base-relic counts in
Configuration.RetainerBaseRelicItems (a generic RetainerItemCache mirroring the materia
cache). AutoRetainer's IPC still cannot supply item-level inventory, so this native scan
remains the source; the cache lets the checker report retainer stock while offline.

### K.6 Seams (confirm on the live client)

1. Per-part quest-sequence thresholds. QuestPart.CompletedAtSequence is 0 for every
   part (uncalibrated). The exact "A Relic Reborn" sequence value at which each part
   finishes is read off in-game -- the report surfaces the raw live sequence precisely
   so these can be filled in, after which per-part completion for the no-drop parts
   becomes exact. Until then those parts report Unknown with the live sequence shown.
2. Routing TerritoryType ids and map coordinates feed the deferred navigation pass and
   should be confirmed when that pass converts MapStop -> world point.
3. Version-sensitive Dalamud surface: IPlayerCharacter.ClassJob (RowRef) .RowId and
   .Level, consistent with the codebase's RowRef usage but worth a glance on build.

### K.7 Command

/relic prereq writes the formatted readiness report (prerequisites, materials with
inventory/retainer counts, and per-part progress) to the Dalamud log. It is the
foundation's in-game test hook; the visual panel is the next pass.

## 10. Build Order

1. IPC wrappers for vnavmesh, RSR, Lifestream, TextAdvance, AutoDuty.
2. Targeting layer and KillTarget executor (the core of the kill grind).
3. Controller state machine and the executor interface.
4. MoveToFlag, AetheryteTeleport, ParticipateFate executors (Atma end to end).
5. Leve, EnterDuty, InteractNpc, UpgradeRelic executors (Animus and upgrades).
6. Data generator from the in-game RelicNote book definitions.
7. Main and configuration windows.
8. Novus and Nexus and Zeta stage data and WaitForCondition executor.
