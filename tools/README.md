# Relicable data tooling

Offline tools that treat the FFXIV game sheets (via the read-only
[XIVAPI v2](https://v2.xivapi.com) "Boilmaster" API) as the source of truth for
the *derivable* Zodiac data — the Animus (Trials of the Braves) leve tables and
the base-relic quest metadata — so those stop being hand-typed and drift is
caught at build time instead of failing silently in-game.

Built on a vendored copy of the [OmegaJackie/XIVAPI-GUI](https://github.com/OmegaJackie/XIVAPI-GUI)
client (`xivapi_client.py`) so the tooling is self-contained — no need to clone
or pip-install that repo. Nothing here ships in the plugin; the tools **emit
committed C#**, and the runtime stays fully offline.

## Setup

```sh
python -m pip install -r tools/requirements.txt   # just `requests`
```
Needs internet (calls `https://v2.xivapi.com`). Python 3.9+.

## Tools

### `gen_leve_tables.py` — generate
Walks `RelicNote/{book}.Leve[]` → `Leve.DataId` → `BattleLeve`/`CompanyLeve` and
writes `Relicable/Data/LeveTables.Generated.cs`:

| Derived table | Rule | Derivation |
|---|---|---|
| `ItemLures` | `BattleLeveHunt` | prime = `LeveData` entry with `ItemsInvolvedQty > 0`; `ItemId` = its `ItemsInvolved`; emerge = the `ToDoNumberInvolved > 0` entry |
| `NamedTargets` | `CompanyLeveSummon` | highest-`EnemyLevel` `CompanyLeveStruct` `BNpcName` |
| `Destinations` | `BattleLeveRound` | the `"Destination"` marker constant |
| `BookLeves` | *(all 23)* | `leveId, name, rule, handler` — the rule→handler dispatch reference |

Output is deterministic (no timestamp/version in the file) so `--check` can gate CI.

```sh
python tools/gen_leve_tables.py            # write the .cs
python tools/gen_leve_tables.py --stdout   # preview
```

> The generated file is currently **reference + validation data**. The
> hand-authored `Data/Leve*.cs` tables remain the runtime source of truth.
> Wiring `LeveRunner` to dispatch off `BookLeves.Rule` (keyed by leve id, name as
> fallback) is the planned rule-dispatch refactor — it retires the hand-typed
> tables and the English-name-key localisation fragility in one move.

### `validate_leve_tables.py` — validate
Checks the authored data against live game data. Catches the two recurring
silent-bug classes:

1. **Stale `TerritoryType` book skips** — an authored `BraveBookPositions`
   dungeon territory that no longer matches the live dungeon (the 6.1 remap class,
   e.g. Sunken Temple of Qarn `163 → 1267`). Resolves each notorious-monster
   target via `MonsterNoteTarget.PlaceNameLocation` → `ContentFinderCondition`
   (ContentType 2) → `TerritoryType`, normalised to the original instance.
2. **SEAM id/string drift** — an authored `LeveItemLures` / `LeveNamedTargets` /
   `LeveDestinations` entry whose item id or BNpc/leve name no longer matches the
   sheets, so the mechanic would silently degrade to a plain fight.

Plus a **rule-coverage** check (every book leve maps to a known handler) and, with
`--check`, a **drift** gate on the generated file.

```sh
python tools/validate_leve_tables.py           # exit 1 on any mismatch
python tools/validate_leve_tables.py --check   # also fail if the generated .cs is stale
```

### `gen_quest_tables.py` — base-relic quest data
Pulls the ten ARR `A Relic Reborn (<weapon>)` forge quests and writes, under
`tools/generated/` (**never** the shipped `Relicable/Data`, so a half-authored
skeleton can't load at runtime):

| Artifact | Contents |
|---|---|
| `zodiac_relic_quests.json` | the deterministic "everything the API gave" dump — id, name, weapon, level, journal genre, prerequisite chain, and the accept/turn-in NPC with its `Level`-sheet position |
| `questpaths/<masked>_A Relic Reborn (<weapon>).json` | one bookend quest-path skeleton per job (accept @ Gerolt seq 0, turn-in seq 255) in the qstxiv schema `QuestPathLoader` reads; the trial gauntlet in between is an annotated TODO |
| `quest_gap_checklist.md` | exactly what still needs an in-game `/relic questwork` capture, separating the **one-time** shared `GlobalParts` sequence calibration from the per-job hand-authored facts |

It also emits the **stage-intro** quests (`ZodiacQuestRegistry`, Atma→Zeta + the
line unlock) to `zodiac_stage_quests.json`, `questpaths_stages/`, and
`stage_gap_checklist.md`. Those quests' grinds are already plugin-driven, so only
the accept/turn-in bookends are derivable — and `QuestPathLoader` currently only
consumes per-job Relic-stage paths, so the stage skeletons are reference artifacts
until the loader is extended.

The per-sequence trial gauntlet is **not** derivable — `Quest.TodoParams` is empty
for these quests — so it stays in `BaseRelicData.GlobalParts` (see below). To
activate a per-job skeleton, fill its middle sequences, move it into
`Relicable/Data/questpaths/`, and bump the csproj version.

`--worksheet <weapon>` writes a play-ordered **live-capture worksheet** for the
quest you are on (`capture_worksheet_<weapon>.md`): the priority `GlobalParts`
sequences still uncalibrated, the expected sequence for every trial to verify, and
that job's exact beastman targets — so a single playthrough closes the gaps.

```sh
python tools/gen_quest_tables.py                     # base-relic + stage artifacts
python tools/gen_quest_tables.py --worksheet Curtana # + a capture worksheet for the quest you're on
python tools/gen_quest_tables.py --stdout            # preview the base-relic JSON dump
python tools/gen_quest_tables.py --check             # fail if either JSON dump is stale
```

### `validate_quest_tables.py` — validate
Checks the hand-authored `ZodiacQuestRegistry` ids/names and the `BaseRelicData`
Gerolt constants + per-job weapon→quest mappings against the live sheets, so a
stale quest id or moved NPC fails the build instead of silently mis-detecting a
stage (the same silent-bug class the leve validator guards against).

```sh
python tools/validate_quest_tables.py           # exit 1 on any mismatch
python tools/validate_quest_tables.py --check   # also fail if the JSON dump is stale
```

## What stays authored (the API cannot produce it)
- **Base-relic per-sequence trial gauntlet** — `Quest.TodoParams` is empty for the
  `A Relic Reborn` quests, so the Chimera → class weapon → Amdapor Keep → 24
  beastmen → Hydra → 3 primals → forge flow (and its `CompletionQuestVariablesFlags`
  work bytes) is authored in `BaseRelicData.GlobalParts`, calibrated from
  `/relic questwork`. The generator emits only the derivable accept/turn-in bookends.
- `EscortLevePaths` **waypoints** — a leve objective is an EventRange/script; no
  coordinate path exists in any sheet. (The escort NPC *name* is validatable.)
- `LeveStartOverrides` **Y** values — Excel has no walkable-floor height; a
  below/above-floor anchor can only be corrected by in-game vnavmesh capture.
- **Behavioural facts** — that the bell is a usable EObj, trigger ranges, etc.
  The validator flags these seams but cannot close them (needs the game running).

## CI wiring (suggested)
Run before `dotnet build` so drift fails the build:

```sh
python -m pip install -r tools/requirements.txt
python tools/validate_leve_tables.py --check
python tools/validate_quest_tables.py --check
```
On a game-data patch, regenerate and commit:
```sh
python tools/gen_leve_tables.py
python tools/gen_quest_tables.py
# then bump the csproj version's 3 fields per the project convention
```
