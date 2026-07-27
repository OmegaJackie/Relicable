#!/usr/bin/env python3
"""Generate the derivable Zodiac base-relic quest data from XIVAPI v2.

Pulls the ten ARR `A Relic Reborn (<weapon>)` forge quests and writes, under
`tools/generated/` (never the shipped plugin tree):

  * `zodiac_relic_quests.json`   -- the full API dump (deterministic)
  * `questpaths/<masked>_...json` -- one bookend quest-path skeleton per job
  * `quest_gap_checklist.md`      -- exactly what still needs an in-game capture

Nothing here ships in the plugin; promote a skeleton into
`Relicable/Data/questpaths/` by hand once you have filled its trial sequences.

Usage:
    python tools/gen_quest_tables.py            # write all three artifacts
    python tools/gen_quest_tables.py --stdout   # print the JSON dump only
    python tools/gen_quest_tables.py --check    # exit 1 if the JSON dump is stale

Needs: `pip install -r tools/requirements.txt` and internet (v2.xivapi.com).
"""

from __future__ import annotations

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)

from xivapi_client import XIVAPIClient, XIVAPIError  # noqa: E402
import quest_derivation as qd  # noqa: E402


def _write(path_rel: str, text: str) -> None:
    path = os.path.join(REPO, path_rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {path_rel}", file=sys.stderr)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stdout", action="store_true",
                    help="print the base-relic JSON dump to stdout instead of writing files")
    ap.add_argument("--check", action="store_true",
                    help="exit 1 if either committed JSON dump is stale")
    ap.add_argument("--worksheet", metavar="WEAPON", default=None,
                    help="also write a live-capture worksheet for this weapon's base-relic "
                         "quest (e.g. --worksheet Curtana)")
    args = ap.parse_args()

    client = XIVAPIClient()
    try:
        versions = client.versions()
        derived = qd.derive(client)
        stages = qd.derive_stage_quests(client)
    except XIVAPIError as e:
        print(f"error: XIVAPI request failed: {e}", file=sys.stderr)
        return 2

    data_json = qd.render_data_json(derived)
    stage_json = qd.render_stage_data_json(stages)

    ver = versions[0].get("names", versions[0]) if versions else "?"
    print(f"game version: {ver}", file=sys.stderr)
    print(f"relic quests: {len(derived.quests)}   stage quests: {len(stages.quests)}   "
          f"global parts parsed: {len(derived.global_parts)}", file=sys.stderr)
    for w in derived.warnings + stages.warnings:
        print(f"  warning: {w}", file=sys.stderr)

    if args.check:
        stale = False
        for rel, text in ((qd.DATA_JSON_REL, data_json), (qd.STAGE_DATA_JSON_REL, stage_json)):
            path = os.path.join(REPO, rel)
            current = ""
            if os.path.exists(path):
                with open(path, encoding="utf-8") as f:
                    current = f.read()
            if current != text:
                print(f"error: {rel} is stale -- run: python tools/gen_quest_tables.py", file=sys.stderr)
                stale = True
            else:
                print(f"{rel} is up to date", file=sys.stderr)
        return 1 if stale else 0

    if args.stdout:
        sys.stdout.write(data_json)
        return 0

    # Base-relic per-job artifacts.
    _write(qd.DATA_JSON_REL, data_json)
    _write(qd.CHECKLIST_REL, qd.render_checklist(derived))
    for q in derived.quests:
        _write(os.path.join(qd.SKELETON_DIR_REL, f"{q.masked_id}_{q.name}.json"),
               qd.render_skeleton(q))

    # Stage-intro (Atma..Zeta) artifacts.
    _write(qd.STAGE_DATA_JSON_REL, stage_json)
    _write(qd.STAGE_CHECKLIST_REL, qd.render_stage_checklist(stages))
    for q in stages.quests:
        _write(os.path.join(qd.STAGE_SKELETON_DIR_REL, f"{q.masked_id}_{q.name}.json"),
               qd.render_stage_skeleton(q))

    # Optional per-job live-capture worksheet.
    if args.worksheet:
        weapon = args.worksheet
        match = next((q for q in derived.quests if q.weapon.lower() == weapon.lower()), None)
        if not match:
            print(f"warning: no base-relic quest for weapon '{weapon}' -- worksheet skipped "
                  f"(known: {', '.join(q.weapon for q in derived.quests)})", file=sys.stderr)
        else:
            job = qd._parse_job_block(match.weapon)
            text = qd.render_capture_worksheet(match.weapon, match.masked_id, derived.global_parts, job)
            _write(os.path.join(qd.GENERATED_DIR_REL, f"capture_worksheet_{match.weapon.replace(' ', '_')}.md"),
                   text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
