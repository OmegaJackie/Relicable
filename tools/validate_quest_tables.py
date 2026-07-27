#!/usr/bin/env python3
"""Validate the hand-authored Zodiac quest data against live XIVAPI v2 sheets.

Catches the recurring silent-bug class in this project: a hard-coded quest id or
NPC constant that no longer matches the game sheets (the stale-id / territory-remap
class), so detection quietly points at the wrong quest.

Checks:
  1. ZodiacQuestRegistry.cs   -- every quest id resolves live and its Name matches.
  2. BaseRelicData.cs         -- GeroltDataId resolves to "Gerolt", his hard-coded
                                 position/territory match the Level sheet, and every
                                 per-job RelicWeaponName maps to a live
                                 "A Relic Reborn (<weapon>)" forge quest.
  3. --check                  -- tools/generated/zodiac_relic_quests.json is not stale.

Usage:
    python tools/validate_quest_tables.py           # exit 1 on any mismatch
    python tools/validate_quest_tables.py --check    # also fail if the JSON dump is stale

Needs: `pip install -r tools/requirements.txt` and internet (v2.xivapi.com).
"""

from __future__ import annotations

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)

from xivapi_client import XIVAPIClient, XIVAPIError  # noqa: E402
import quest_derivation as qd  # noqa: E402

POS_TOL = 0.01  # world-coordinate float tolerance


def _read(path_rel: str) -> str:
    with open(os.path.join(REPO, path_rel), encoding="utf-8") as f:
        return f.read()


def _registry_quests(text: str):
    """(id, name) for every ZodiacQuest(...) plus the WeaponsmithOfLegend const."""
    out = []
    m = re.search(r"WeaponsmithOfLegendId\s*=\s*(\d+)", text)
    if m:
        out.append((int(m.group(1)), "The Weaponsmith of Legend"))
    # new ZodiacQuest(RelicStage.X, "Name", <id>, ...)
    for qm in re.finditer(r'new ZodiacQuest\(\s*RelicStage\.\w+\s*,\s*"([^"]+)"\s*,\s*(\d+)', text):
        out.append((int(qm.group(2)), qm.group(1)))
    return out


def _base_relic_constants(text: str):
    gerolt_id = int(re.search(r"GeroltDataId\s*=\s*(\d+)", text).group(1))
    gerolt_terr = int(re.search(r"GeroltTerritory\s*=\s*(\d+)", text).group(1))
    pm = re.search(r"GeroltPosition\s*=\s*new\(\s*([\-0-9.]+)f\s*,\s*([\-0-9.]+)f\s*,\s*([\-0-9.]+)f", text)
    gerolt_pos = tuple(float(pm.group(i)) for i in (1, 2, 3))
    weapons = re.findall(r'RelicWeaponName\s*=\s*"([^"]+)"', text)
    return gerolt_id, gerolt_terr, gerolt_pos, weapons


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true",
                    help="also fail if tools/generated/zodiac_relic_quests.json is stale")
    args = ap.parse_args()

    client = XIVAPIClient()
    errors: list = []
    notes: list = []

    try:
        # ---- 1. Registry ids resolve + names match --------------------------
        for qid, name in _registry_quests(_read(qd.REGISTRY_CS_REL)):
            try:
                live = ((client.row("Quest", qid, fields=["Name"]).get("fields") or {})
                        .get("Name") or "").strip()
            except XIVAPIError:
                live = ""
            if not live:
                errors.append(f"registry: quest id {qid} ('{name}') does not resolve on the live sheet")
            elif live.lower() != name.strip().lower():
                errors.append(f"registry: id {qid} is '{live}' live but registry says '{name}'")
            else:
                notes.append(f"ok  registry {qid} = '{live}'")

        # ---- 2. BaseRelicData Gerolt + per-job weapons ----------------------
        gid, gterr, gpos, weapons = _base_relic_constants(_read(qd.BASE_RELIC_DATA_CS_REL))
        singular = ((client.row("ENpcResident", gid, fields=["Singular"]).get("fields") or {})
                    .get("Singular") or "").strip()
        if singular != "Gerolt":
            errors.append(f"BaseRelicData: GeroltDataId {gid} resolves to '{singular}', not 'Gerolt'")
        lvl = client.search(f"Object={gid}", "Level", fields=["X", "Y", "Z", "Territory"], limit=1)
        rows = lvl.get("results") or []
        if not rows:
            errors.append(f"BaseRelicData: no Level row for Gerolt ({gid}) -- cannot verify GeroltPosition")
        else:
            lf = rows[0].get("fields") or {}
            live_pos = (lf.get("X"), lf.get("Y"), lf.get("Z"))
            live_terr, _ = qd._rel(lf.get("Territory"))
            if any(a is None or abs(a - b) > POS_TOL for a, b in zip(live_pos, gpos)):
                errors.append(f"BaseRelicData: GeroltPosition {gpos} != live Level {live_pos}")
            else:
                notes.append(f"ok  GeroltPosition matches live Level {live_pos}")
            if live_terr != gterr:
                errors.append(f"BaseRelicData: GeroltTerritory {gterr} != live {live_terr}")

        derived = qd.derive(client)
        live_weapons = {q.weapon.lower(): q for q in derived.quests}
        for w in weapons:
            if w.lower() not in live_weapons:
                errors.append(f"BaseRelicData: RelicWeaponName '{w}' has no live "
                              f"'A Relic Reborn ({w})' quest")
            else:
                notes.append(f"ok  weapon '{w}' -> quest {live_weapons[w.lower()].full_id}")
        for w in derived.warnings:
            errors.append(f"derivation: {w}")

        # ---- 3. Generated JSON drift ---------------------------------------
        if args.check:
            path = os.path.join(REPO, qd.DATA_JSON_REL)
            current = _read(qd.DATA_JSON_REL) if os.path.exists(path) else ""
            if current != qd.render_data_json(derived):
                errors.append(f"{qd.DATA_JSON_REL} is stale -- run: python tools/gen_quest_tables.py")

    except XIVAPIError as e:
        print(f"error: XIVAPI request failed: {e}", file=sys.stderr)
        return 2

    for n in notes:
        print(n, file=sys.stderr)
    if errors:
        print(f"\nFAILED with {len(errors)} problem(s):", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1
    print(f"\nOK: {len(notes)} checks passed against live XIVAPI.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
