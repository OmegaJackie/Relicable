# Relicable Backlog

Remaining work and known seams, grounded in the current code. Status legend:
SEAM = needs the live game/addon to finish; DATA = content authoring;
BUILD = build/validation; DISABLED = intentionally off per request.

## Remaining

### Native seams (need the live addon; cannot be verified offline)

- [~] SEAM (experimental, needs in-game verification) - Novus affix-materia UI.
  `Steps/RelicMeld.cs` now drives the live `MateriaAttach` window: it selects the
  materia by item id from `AgentMateriaAttach` and fires the meld + confirm
  callbacks. Gated behind `Configuration.EnableAutoMeld` (off by default) because the
  meld/confirm callback argument layout cannot be verified outside the game and a
  wrong confirm could shatter materia. On first attempt (debug log on) it dumps the
  addon's AtkValues so the exact callback indices can be confirmed. Progress is judged
  by materia consumed, so an ineffective callback fails fast instead of false-melding.
  TODO once verified: confirm the FireCallback indices and switch the consumed-materia
  proxy to true per-stat success tracking (toast/log).
- [~] DONE (needs in-game check) - Retainer materia retrieve. `Steps/RetainerWithdraw.cs`
  now moves a materia stack from the open retainer to the bags via
  `InventoryManager.MoveItemSlot` (find materia in RetainerPage1..7 -> first empty
  player slot -> move). Driven by the "Fetch from Retainer" popout action and the
  route executor's auto-withdraw path. Verify MoveItemSlot's retainer->player behaviour
  on the live client; it fails safe (no empty slot / not open = no-op).
- [ ] SEAM - GuildLeve accept. `Steps/Interaction/LeveBoard.cs` `AcceptOffered`
  returns 0; reading and accepting offered leves from the `GuildLeve` board needs
  the addon's list reads + callbacks (blueprint: Battlevest RecursivelyAcceptLeves
  / Callback.Fire). The levemete category select (SelectString -> Battlecraft) and
  board close are wired; the accept-then-run flow is built around it.
- [ ] SEAM - Guildleve initiation. `Steps/Interaction/LeveRunner.cs`: an accepted
  leve must be initiated at its objective (Battlevest Utils.Initiate); once started
  the player is BoundByDuty. Kill-on-arrival works; crystal-initiated leves need an
  interaction at the Fight-phase entry.

### Content / data

- [x] DONE (partial) - Location layer (`Data/Locations.cs`). Derived from Lumina:
  territory -> aetheryte teleport, place-name -> territory, and leve levemete
  (territory + position + NPC). Leve objectives now teleport to the zone and go to
  the levemete; monster objectives teleport to the mob's zone (then engage via
  IsMonsterNoteTarget when near).
- [ ] DATA - FATE and exact mob coordinates. Not in the game sheets (Fate.Location
  is an EventRange instance id; MonsterNoteTarget stores only zone/location place
  names), so a FATE has no teleport anchor and mobs have no precise nav point. Full
  FATE/mob autonomy needs a hand-authored coordinate table (see BraveBookPositions).
  The objective JSON already supports explicit AetheryteId/Position, so such a table
  can be supplied as authored data files and merged with generation by Id.
- [ ] DATA - `RequiredWeaponItemId` per book; Atma/Novus/Nexus/Zeta and stage
  base-upgrades still exist only as sample JSON.
- [x] DONE (needs in-game check) - Escort battle leves. `LeveRunner` now runs an escort
  objective loop for leves in `Data/EscortLevePaths.cs` (a name-keyed table of the ordered
  world waypoints + the escort NPC name, the coords the game sheets do not carry -- same
  gap `BraveBookPositions` fills). A kill leve's fight loop never completes an escort (the
  objective is to lead the NPC, not clear a spawn), so `Phase.Fight` branches to `RunEscort`
  when `EscortLevePaths.ForLeveName(Sheets.LeveName(leveId))` matches: clear a nearby ambush
  (`Targeting.NearestHostile` gated to 15y so a distant mob does not pull us off route) ->
  else target the NPC + `/beckon motion` (via `ECommons.Chat`, NOT `ctx.Commands`: Dalamud's
  `ProcessCommand` drops game emotes) and walk the route on foot, pausing to re-beckon when
  the NPC lags (>8y) and forcing a beckon after each fight. Completion stays generic (the
  leve leaves the accepted list). Ships one route: "Someone's in the Doghouse" (Mine Hound,
  9 points). SEAMS to verify live: (1) the escort NPC name / leve name exactly match the
  client; (2) a single targeted `/beckon` is what advances the NPC (else adjust the emote /
  `BeckonThrottleMs` cadence); (3) the 15y engage range catches the on-path ambushes without
  stalling; (4) the authored points land on the navmesh. Author more routes by adding entries
  to `EscortLevePaths.Routes`.

### Base relic (A Relic Reborn) -- foundation landed; next passes

The data model and live prerequisite checker are in (see DESIGN Appendix K). Remaining:

- [ ] SEAM (in-game) - Per-part quest-sequence thresholds. `BaseRelic/BaseRelicData.cs`
  `QuestPart.CompletedAtSequence` is calibrated only for parts 3/4/5 (all 10; the beastmen
  value is from an in-game log, and parts 3/4 share it as a safe over-estimate so they skip
  once the hunt is passed). Parts 6-10 are still 0. Run `/relic prereq` mid-quest to read the
  live `A Relic Reborn` sequence and fill the rest; until then the repeatable post-hunt trials
  run in order once per session (an already-passed Hard primal may be re-cleared once).
- [x] DONE (verify in-game) - Part 5 beastmen-hunt automation. `BaseRelic/BaseRelicHuntGenerator`
  emits a per-job Relic-stage objective (teleport to the stronghold aetheryte + three
  KillTarget steps, 8 each) at the authored spawn coords, converted to world points by
  `Data/MapCoords` (inverse of Dalamud MapUtil; reads each zone's real Map SizeFactor/offset
  and round-trips correctly). NOTE: an attempt to use AgentMap's quest-link marker for the
  location was reverted -- that marker points to the relic ISSUER (Vesper Bay), not the kill
  objective. The true live per-objective marker would be in AgentMap's minimap markers
  (`_miniMapMarkers`), but identifying the quest-objective marker needs its DataType captured
  in-game; until then the authored coords (the user's transcribed table) are the source. `RelicController` runs only the equipped
  job's hunt (RelicObjective.Job); `Plugin.LoadObjectives` loads them. SEAMS to verify
  live: (1) KillTargetExecutor counts base-relic kills LOCALLY (the quest has no RelicNote
  counter) by watching the engaged mob die -- an assisted/stray kill or interruption can
  mis-count; objectives are procedural (re-runnable) and the in-game quest credit caps it.
  (2) The hunt is not gated to the Part 5 sequence (unknown), so /relic start should be
  used while actually on the hunt; running it at the wrong time just wastes kills. (3) The
  player must have the relic equipped for kills to credit -- not enforced. (4) Confirm the
  map->world coords land on the spawns. HIGH account-ban risk (open-world combat botting).
- [x] DONE (verify in-game) - Parts 3/4 + 6-9 duties via AutoDuty. `BaseRelicHuntGenerator`
  emits per-job Relic-stage EnterDuty objectives in quest order: p03 Chimera (A Relic Reborn:
  The Chimera) and p04 Amdapor Keep BEFORE the p05 beastmen hunt, then p06 Hydra and p07-09
  Ifrit/Garuda/Titan (Hard) after. This matches the canonical 10-task stage-1 list. The Chimera
  CFC name was corrected to "A Relic Reborn: The Chimera" (the bare "A Relic Reborn" never
  resolved). TerritoryType is resolved by `BaseRelicCatalog.DutyTerritoryId`
  (ContentFinderCondition name -> TerritoryType). NOTE: the duty-name maps MUST be case-
  insensitive -- the CFC Name stores a lowercase leading article ("the Bowl of Embers (Hard)"),
  so a case-sensitive map silently returned 0 and the trials were never generated (the run
  halted with "no objective remains" at a mid-trial sequence, even though the beastmen, which
  use a territory id directly, generated fine). The EnterDutyExecutor hands off to AutoDuty.
  NO per-part Gerolt report is emitted: the base-relic quest is one collection phase that
  advances on content completion (kills, clears, drops), so the appended reports only hit
  Gerolt's flavor text and stalled the run between trials -- removed. SEAMS: (1) each duty must
  be UNLOCKED for AutoDuty to queue it; (2) the relic must be EQUIPPED for the trial drop to
  credit -- now enforced by an `EnsureRelicEquipped` step prepended to each duty/hunt (best-
  effort auto-equips the relic from the armoury/bags via MoveItemSlot, verified, else pauses;
  `Configuration.AutoEquipRelicInDuty`, on by default); (3) completion is quest-authoritative (live sequence / one-time-duty
  cleared / ran this session), NOT the persisted procedural flag, so a trial that did not credit
  stays retryable instead of stalling the run; (4) the single collection turn-in to Gerolt, the
  Amdapor Glyph trade to Rowena (part 4), and the oil/mist vendor buys from Auriana (part 10)
  are NOT yet automated (turn-in NPC ids needed) -- a fresh relic stops there for the manual
  finish.
- [x] DONE (extend data) - Quest-path runner. `BaseRelic/QuestPath` + `QuestPathLoader`
  load qstxiv quest-path JSON from `Data/questpaths` ("{questId}_{name (weapon)}.json"; the
  job is resolved from the weapon name) and convert each quest SEQUENCE into a Relic-stage
  objective tagged `ActiveAtSequence`. The controller runs the objective whose
  ActiveAtSequence equals the game's live quest sequence and advances when the game does
  (authoritative; no re-farm). It COEXISTS with the generated hunt/trial objectives: a path
  step is used when one is mapped for the current sequence, otherwise the generated
  objective (so the path's accept/walk/turn-in framing + the generator's beastmen/trials
  combine). InteractionType maps: WalkTo->MoveTo, Accept/Interact/CompleteQuest->InteractNpc,
  Combat->KillTarget (EnemyName/KillCount), Duty->EnterDuty (DutyName/DutyTerritoryType).
  Ships the Bard path (1125): Gerolt (DataId 1003075) accept@seq0, walk Coerthas@seq1,
  interact Gerolt@seq2, complete@seq255. Path steps now AUTO-TELEPORT: the loader prepends an
  aetheryte teleport (resolved from each step's TerritoryId) before navigating, so a step in
  another zone is reachable hands-off (no-op when already in that zone; Duty steps have no
  overworld aetheryte so none is added). SEAM: the shipped path is still a STUB (no combat/
  duty sequences -> those fall back to the generator); to make the runner itself drive the
  kills/trials, capture the live sequence numbers for each part and add them to the path.
- [x] DONE (calibrate) - Quest-AUTHORITATIVE completion for base-relic parts. A Relic-stage
  generated objective (beastmen/trial) is complete ONLY when the live quest sequence passes
  it (`BaseRelicState.IsPartCompleteByQuest`), a one-time quest duty is already cleared
  (`RelicObjective.OneTimeDutyContentId` + `GameState.IsDutyCompleted`, set for the Hydra),
  or the engine ran it THIS run (in-memory `_relicRan`, cleared on Start). It deliberately
  does NOT read the PERSISTED procedural flag. That persisted flag was the "no objective
  remains at sequence N" stall: a prior run queued the trials via AutoDuty, which wrote each
  to the persisted `CompletedProceduralObjectives`, so on the next launch every part read
  "done" and the run halted at e.g. sequence 13 even though the quest had not advanced. Now a
  queued trial that did not credit (relic not equipped, wrong step) stays retryable, and a
  part the quest really passed is not re-farmed. The beastmen part's CompletedAtSequence is
  10 (from an in-game log); parts 6-9 are still 0, so until calibrated the engine attempts the
  remaining trials in order once per run and the game advances the quest on the drops -- an
  already-passed Hard primal may be re-cleared once (wasteful but harmless; the one-time Hydra
  is skipped by the duty guard). The controller logs the live sequence when working the base
  relic, so parts 6-9 `CompletedAtSequence` can be read off `BaseRelicData.GlobalParts` and
  filled in to remove the wasteful re-clears. Note: the local kill counter is still
  approximate; this quest detection backstops it.
- [x] DONE - "Not attacking the next in-range mob": RSR's operating mode is edge-triggered,
  so after a mob died (RSR AutoOffAfterCombat) `EnableManual` was a no-op for the next
  already-in-range mob. KillTargetExecutor now calls `ResyncNextDispatch` before the pull
  when out of combat. (Re-mounting between close stronghold mobs is expected: the game
  blocks mounting in combat, which lingers a few seconds; it runs on foot, then re-mounts
  for far/out-of-combat hops >30y.)
- [x] DONE - The Chimera trial CFC name. It is "A Relic Reborn: The Chimera" (with the
  suffix), not the bare "A Relic Reborn" the data used, which is why it did not resolve. The
  name is corrected and the duty-name map is case-insensitive, so the Chimera now resolves and
  generates as the p03 AutoDuty objective. Verify the unlock hint in-game.
- [ ] DEFER - In-zone routing pass. `BaseRelic` MapStops carry TerritoryType + map x/y
  (and now an optional map Z for the known stops: Chimera 32.1,7.2,Z2.1; Rowena
  21.9,5.0,Z0.5); the route builder (map -> world via Map sheet SizeFactor/offset, then
  vnavmesh PointOnFloor/NearestPoint for world Y when Z is unknown) and base-relic
  objective step lists are the next pass. Confirm the TerritoryType ids when wiring it.
- [x] DONE (verify on build) - Relic-stage trial steps. The Chimera trial (duty named
  "A Relic Reborn", same text as the quest) and Hydra trial resolve their InstanceContent
  id via `ContentFinderCondition` in `BaseRelicCatalog`, and `/relic prereq` shows each as
  unlocked (queue) or needs-unlock (examine entrance), via `GameState.IsDutyUnlocked`
  (`UIState.IsInstanceContentUnlocked`). Version-sensitive: confirm `cfc.Content.RowId`
  resolves the InstanceContent id on the installed Lumina (RowRef shape, like
  `cfc.ContentType.RowId`).
- [ ] DEFER - Configuration panel. The checker exposes a `PrerequisiteReport` and an
  ASCII formatter; the visual per-job panel (prereqs, shopping list with retainer
  counts, per-part progress, job override dropdown) binds to it in a later pass.
- [ ] DATA (verify) - Part 2 crafting-ingredient sub-lists are transcribed verbatim from
  the wiki; unresolved item names are logged by `BaseRelicCatalog` and shown Unknown.
  Confirm any that do not resolve against the live Item sheet.

### Build / validation (cannot be done offline)

- [ ] BUILD - Compile against the installed Dalamud / FFXIVClientStructs / Lumina;
  version-sensitive APIs may need single-symbol fixes. Confirm
  `Dalamud.NET.Sdk/15.0.0`.
- [ ] BUILD - In-game attended test pass. Nothing has been executed; verify the
  UI-driving pieces, chocobo/death-recovery actions, and the assumed addon names.
- [ ] BUILD - Novus routing surface. Verified against current sources:
  `RetainerManager.GetActiveRetainer` / `Retainer.RetainerId` / `Retainer.NameString`,
  `InventoryContainer.IsLoaded`, `RetainerPage1..7`, Lumina `World.DataCenter` /
  `WorldDCGroupType.Region`, and AutoRetainer's `GetRegisteredCIDs` /
  `GetOfflineCharacterData` (read by reflection). Confirm these on the installed
  versions; the materia item-id and World/DC reads resolve at runtime by name.
- [ ] BUILD - Universalis needs outbound HTTPS to `universalis.app`. The price fetch
  runs on a background task; confirm the host's network policy allows it.

### Ignored by request

- Nexus light value: now READ live from the equipped Novus relic
  (`InventoryItem.SpiritbondOrCollectability`, 0..2000). The Nexus stage shows a real
  0/2000 gauge and the AutoDuty farm auto-stops at 2000.
- Zeta Mahatma value: now READ live from the equipped Zodiac Braves weapon (same
  `SpiritbondOrCollectability`, decoded as 12 Mahatma x 40 points). The Zeta stage shows
  a real Mahatma tracker and fully automates the grind (farm + 12x Remon attach); the
  one-time Jalzahn awakening is left to the player (needs the relic unequipped). The only
  open seam is Remon's attach SelectString option text (matched on "mahatma").
- TextAdvance scoped external control: not used; Relicable relies on TextAdvance
  running globally, as requested.

### Intentionally disabled (per request - not work items)

- PauseIfTargetedByPlayer, PauseOnTell, StopAfterNRelics,
  StopWhenOutOfLeveAllowances, StopOnInventoryFull: left off.

## Resolved

Targeting filters (attackable + in-FATE); RecoverOnDeath (return + resume);
chocobo summon + healer stance; RSR FATE targeting; BossMod Reborn AoE avoidance;
equipped-relic guard; leve list cycling with filler reroll; LeveRunner
(navigate/fight/complete); relic-upgrade menu selection by text; real UseItem and
TurnInItems; MainWindow progress bars + start-failure message; `/relic reload`;
removed unused config prefs; AllStepsDone persistence; Zeta stage (count-based);
base-relic (A Relic Reborn) foundation: RelicStage.Relic, per-job content
(BaseRelicData), name->id catalog (BaseRelicCatalog), live PrerequisiteChecker
(quests/level/materials/quest-sequence), equipped-job detection + override, retainer
scan generalized to base-relic mats, and the /relic prereq report command;
generator leve entries; Healer Stance (BuddyAction 7) and Return (GeneralAction 8)
ids; FATE level sync via /levelsync; global TextAdvance; leve flow aligned to
Battlevest (SelectString category + GuildLeve board, accept-then-run); BossMod Reborn
default preset "VBM Default"; `.gitignore`.
