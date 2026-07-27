#!/usr/bin/env python3
"""Validate Relicable's authored leve data against live XIVAPI v2 game data.

Catches the two classes of silent bug the leve subsystem keeps hitting, at build
time, without the game running:

  1. Stale-TerritoryType book skips -- an authored BraveBookPositions dungeon
     territory that no longer matches the live dungeon (the 6.1 remap class, e.g.
     Sunken Temple of Qarn 163 -> 1267). A wrong territory makes the book slot
     silently skip and the engine buy the next book.
  2. SEAM id/string drift -- an authored LeveItemLures / LeveNamedTargets /
     LeveDestinations entry whose item id or BNpc/leve name no longer matches what
     the sheets say, so the special mechanic silently degrades to a plain fight.

Also runs a rule-coverage check (every Animus book leve maps to a known handler)
and, with --check, a drift check of the generated file.

Usage:
    python tools/validate_leve_tables.py            # validate authored data (exit 1 on mismatch)
    python tools/validate_leve_tables.py --check     # also fail if LeveTables.Generated.cs is stale

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
import leve_derivation as ld  # noqa: E402

DATA = os.path.join(REPO, "Relicable", "Data")
# The notorious-monster (dungeon-boss) MonsterNoteTarget ids in BraveBookPositions.
NM_IDS = range(446, 466)


class Report:
    def __init__(self) -> None:
        self.failures: list[str] = []
        self.warnings: list[str] = []
        self.oks = 0

    def ok(self, msg: str) -> None:
        self.oks += 1
        print(f"  OK    {msg}")

    def warn(self, msg: str) -> None:
        self.warnings.append(msg)
        print(f"  WARN  {msg}")

    def fail(self, msg: str) -> None:
        self.failures.append(msg)
        print(f"  FAIL  {msg}")


def _read(path: str) -> str:
    with open(path, encoding="utf-8") as f:
        return f.read()


# --------------------------------------------------------------------------- #
# Authored-C# parsers (tolerant regex over the simple dictionary literals)
# --------------------------------------------------------------------------- #
def parse_item_lures(text: str) -> dict[str, tuple[int, str, str]]:
    out: dict[str, tuple[int, str, str]] = {}
    for m in re.finditer(r'\["((?:[^"\\]|\\.)*)"\]\s*=\s*new\s+ItemLure\((.*?)\)', text, re.DOTALL):
        key, body = m.group(1), m.group(2)
        item = re.search(r"ItemId:\s*(\d+)", body)
        prime = re.search(r'PrimeTargetName:\s*"((?:[^"\\]|\\.)*)"', body)
        emerge = re.search(r'EmergeTargetName:\s*"((?:[^"\\]|\\.)*)"', body)
        if item and prime and emerge:
            out[key] = (int(item.group(1)), prime.group(1), emerge.group(1))
    return out


def parse_string_map(text: str) -> dict[str, str]:
    """['key'] = "value" entries (LeveNamedTargets / LeveDestinations)."""
    out: dict[str, str] = {}
    for m in re.finditer(r'\["((?:[^"\\]|\\.)*)"\]\s*=\s*"((?:[^"\\]|\\.)*)"', text):
        out[m.group(1)] = m.group(2)
    return out


def parse_monster_territories(text: str) -> dict[int, int]:
    """[id] = new(territory, map, x, y) entries in BraveBookPositions."""
    out: dict[int, int] = {}
    for m in re.finditer(r"\[(\d+)\]\s*=\s*new\(\s*(\d+)\s*,", text):
        out[int(m.group(1))] = int(m.group(2))
    return out


# --------------------------------------------------------------------------- #
# Checks
# --------------------------------------------------------------------------- #
def check_map(rep: Report, label: str, authored: dict, derived: dict) -> None:
    print(f"\n[{label}]  authored={len(authored)} derived={len(derived)}")
    # Case-insensitive pairing mirrors the runtime StringComparer.OrdinalIgnoreCase
    # (apostrophe-sensitive: a curly/straight mismatch is a real runtime miss).
    dlow = {k.casefold(): (k, v) for k, v in derived.items()}
    alow = {k.casefold(): (k, v) for k, v in authored.items()}
    for kf, (akey, aval) in alow.items():
        if kf not in dlow:
            rep.fail(f"{label}: authored key '{akey}' is not a derivable leve (name drift or wrong key)")
            continue
        dkey, dval = dlow[kf]
        if aval == dval:
            rep.ok(f"{label}: '{akey}' matches game data")
        else:
            rep.fail(f"{label}: '{akey}' authored {aval!r} != derived {dval!r}")
    for kf, (dkey, dval) in dlow.items():
        if kf not in alow:
            rep.warn(f"{label}: game data has '{dkey}' ({dval!r}) with no authored entry "
                     f"(fine if LeveRunner handles it generically)")


def check_territories(rep: Report, client: XIVAPIClient) -> None:
    print("\n[BraveBookPositions dungeon territories]  (the 6.1 stale-id bug class)")
    authored = parse_monster_territories(_read(os.path.join(DATA, "BraveBookPositions.cs")))
    name2terr = ld.dungeon_name_to_territory(client)
    for nm_id in NM_IDS:
        if nm_id not in authored:
            rep.warn(f"NM {nm_id}: no authored BraveBookPositions entry")
            continue
        auth = authored[nm_id]
        dungeon = ld.nm_target_dungeon_name(client, nm_id)
        derived = name2terr.get(ld._norm_name(dungeon)) if dungeon else None
        if derived is None:
            rep.warn(f"NM {nm_id}: dungeon '{dungeon}' did not resolve to a ContentType-2 territory "
                     f"(authored {auth}); verify by hand")
        elif derived == auth:
            rep.ok(f"NM {nm_id}: '{dungeon}' -> {auth}")
        else:
            rep.fail(f"NM {nm_id}: '{dungeon}' authored territory {auth} != live {derived} "
                     f"(STALE -- book slot will silently skip)")


def check_rule_coverage(rep: Report, derived: ld.Derived) -> None:
    print(f"\n[rule coverage]  {len(derived.book_leves)} book leves")
    unknown = [b for b in derived.book_leves if b.handler == "<UNKNOWN>"]
    for b in unknown:
        rep.fail(f"leve {b.leve_id} '{b.name}': rule '{b.rule}' has no handler in RULE_HANDLERS")
    if not unknown:
        rules = sorted({b.rule for b in derived.book_leves})
        rep.ok(f"every book leve maps to a known handler ({len(rules)} rules: {', '.join(rules)})")


def check_generated_drift(rep: Report, derived: ld.Derived) -> None:
    path = os.path.join(REPO, ld.GENERATED_PATH_REL)
    print(f"\n[generated file drift]  {ld.GENERATED_PATH_REL}")
    fresh = ld.render_cs(derived)
    if not os.path.exists(path):
        rep.fail(f"{ld.GENERATED_PATH_REL} does not exist -- run gen_leve_tables.py")
        return
    on_disk = _read(path)
    if on_disk == fresh:
        rep.ok("generated file is up to date")
    else:
        rep.fail("generated file is STALE -- run `python tools/gen_leve_tables.py` and commit")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--check", action="store_true",
                    help="also fail if LeveTables.Generated.cs differs from a fresh generation")
    args = ap.parse_args()

    client = XIVAPIClient()
    rep = Report()
    try:
        derived = ld.derive(client)

        check_map(rep, "LeveItemLures",
                  {k: (v[0], v[1], v[2]) for k, v in
                   parse_item_lures(_read(os.path.join(DATA, "LeveItemLures.cs"))).items()},
                  {k: (l.item_id, l.prime, l.emerge) for k, l in derived.item_lures.items()})
        check_map(rep, "LeveNamedTargets",
                  parse_string_map(_read(os.path.join(DATA, "LeveNamedTargets.cs"))),
                  derived.named_targets)
        check_map(rep, "LeveDestinations",
                  parse_string_map(_read(os.path.join(DATA, "LeveDestinations.cs"))),
                  derived.destinations)
        check_territories(rep, client)
        check_rule_coverage(rep, derived)
        if args.check:
            check_generated_drift(rep, derived)
    except XIVAPIError as e:
        print(f"error: XIVAPI request failed: {e}", file=sys.stderr)
        return 2

    for w in derived.warnings:
        rep.warn(f"derivation: {w}")

    print(f"\n{'='*60}")
    print(f"  {rep.oks} ok, {len(rep.warnings)} warning(s), {len(rep.failures)} failure(s)")
    if rep.failures:
        print("  RESULT: FAIL")
        return 1
    print("  RESULT: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
