# A Relic Reborn (Curtana) — live capture worksheet (Paladin)

**Contributor worksheet, not a personal note.** Fill in a copy of it while playing.

The per-sequence trial gauntlet is the one thing XIVAPI cannot supply —
`Quest.TodoParams` is empty on the Quest sheet — so these values have to be captured
from a live playthrough and folded back into the tables by hand. If you are running
A Relic Reborn on Paladin, this is the sheet to fill in.

At each step, run `/relic questwork` (it needs *Enable debug log* on in `/relic config`)
and read the live quest sequence, plus the six work bytes it prints for the active quest.

## Priority — the only GlobalParts sequences still uncalibrated

These are **shared by all 10 jobs**, so capturing them once on this Curtana run closes them for every future weapon:

- **Part 1 — Broken Weapon** → record `CompletedAtSequence`: `____`  (run `/relic questwork` the moment this step reports done)
- **Part 2 — Class Weapon** → record `CompletedAtSequence`: `____`  (run `/relic questwork` the moment this step reports done)
- **Part 10 — Radz-at-Han Quenching Oil** → record `CompletedAtSequence`: `____`  (run `/relic questwork` the moment this step reports done)

## Play-ordered sequence map (expected values from GlobalParts — verify each)

| Part | Step | Expected seq | Your capture |
|---|---|---|---|
| 1 | Broken Weapon | done ? | **record seq ___** |
| 2 | Class Weapon | done ? | **record seq ___** |
| 3 | Complete A Relic Reborn: The Chimera | done 7 | verify seq 7 |
| 4 | Complete Amdapor Keep | done 8 | verify seq 8 |
| 5 | Beastmen Hunt | active 10, done 11 | verify seq 11 |
| 6 | Hydra | active 12, done 13 | verify seq 13 |
| 7 | White-Hot Ember | active 15, done 16 | verify seq 16 |
| 8 | Howling Gale | active 16, done 17 | verify seq 17 |
| 9 | Hyperfused Ore | active 17, done 18 | verify seq 18 |
| 10 | Radz-at-Han Quenching Oil | done ? | **record seq ___** |

## Paladin / Curtana specifics (the exact targets to hit)

- **Stronghold** (broken weapon + the 24-beastman cull, Part 5): Zahar'ak
- **Beastmen — exact names the quest text gates on** (killing the wrong look-alike never credits): `Zahar'ak Lancer`, `Zahar'ak Pugilist`, `Zahar'ak Thaumaturge`
- **Class weapon** (Part 2): Aeolian Scimitar  ·  **meld ×2:** Battledance Materia III

## Folding your captures back in

1. Put each recorded `CompletedAtSequence` (and `ActiveFromSequence` for a gated
   trial) into `Relicable/BaseRelic/BaseRelicData.cs` → `GlobalParts[part]`.
   These are shared, so you only ever do this once.
2. *(optional, for the full sequence-accurate path)* fill the middle sequences of
   `tools/generated/questpaths/1120_A Relic Reborn (Curtana).json` from your captures, then move it into `Relicable/Data/questpaths/`.
3. Bump the csproj version (all 3 fields), then rerun
   `python tools/validate_quest_tables.py --check`.

_Tip: `/relic questwork` also prints the active quest's six work bytes. Jot the
`high`/`low` nibble values at each step if you want to author
`CompletionQuestVariablesFlags` for sub-sequence precision (optional)._
