#!/usr/bin/env python3
"""Generate Relicable/Data/LeveTables.Generated.cs from XIVAPI v2 game data.

Walks the RelicNote -> Leve -> BattleLeve/CompanyLeve chain and emits the
derivable leve mechanic tables (item-lure / named-target / round marker) plus a
rule -> handler dispatch reference for every Animus book leve. Deterministic:
the same game data produces byte-identical output, so `validate_leve_tables.py
--check` can fail CI on drift.

Usage:
    python tools/gen_leve_tables.py                 # write the generated .cs
    python tools/gen_leve_tables.py --stdout        # print instead of writing
    python tools/gen_leve_tables.py --out PATH      # write elsewhere

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
import leve_derivation as ld  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", default=os.path.join(REPO, ld.GENERATED_PATH_REL),
                    help="output path for the generated .cs (default: %(default)s)")
    ap.add_argument("--stdout", action="store_true", help="print to stdout instead of writing a file")
    args = ap.parse_args()

    client = XIVAPIClient()
    try:
        versions = client.versions()
        derived = ld.derive(client)
    except XIVAPIError as e:
        print(f"error: XIVAPI request failed: {e}", file=sys.stderr)
        return 2

    text = ld.render_cs(derived)

    ver = versions[0].get("names", versions[0]) if versions else "?"
    print(f"game version: {ver}", file=sys.stderr)
    print(f"book leves: {len(derived.book_leves)}   "
          f"item-lures: {len(derived.item_lures)}   "
          f"named-targets: {len(derived.named_targets)}   "
          f"round-markers: {len(derived.destinations)}", file=sys.stderr)
    for w in derived.warnings:
        print(f"  warning: {w}", file=sys.stderr)

    if args.stdout:
        sys.stdout.write(text)
        return 0

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {os.path.relpath(args.out, REPO)}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
