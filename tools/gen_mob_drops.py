#!/usr/bin/env python3
"""Generate the mob-drop allowlist that gates Relicable's auto-discard.

WHY THIS EXISTS
---------------
Auto-discard used to decide what to delete with a NEGATIVE filter: "white, stackable,
vendor price <= 100, not equippable, not usable" was taken to mean "mob-drop clutter".
It does not. That description also fits ~5,400 items, including the entire Trials of the
Braves crafting pipeline (Aged Eye of Fire, Aged Pestle Pieces, Pumice, Belah'dian
Silver, Electrum Sand, ...), which the plugin duly destroyed.

The rule is now positive and provable: an item is discardable ONLY if it is known to drop
from an enemy. Everything else -- crafted, gathered, fished, vendored, desynthed, quest,
unknown -- is left alone. A missing entry means "not discarded", so an incomplete table
fails SAFE.

SOURCE
------
Garland Tools' per-item document exposes `item.drops`: the list of BNpc ids that drop it.
That field is empty for every crafted/gathered/vendored/desynthed item and populated for
genuine enemy drops, which is exactly the distinction wanted:

    Boar Hide          drops=5    craft=n nodes=n   -> discardable
    Diremite Web       drops=16   craft=n nodes=n   -> discardable
    Silver Ingot       drops=0    craft=Y           -> protected
    Pumice             drops=0    nodes=Y           -> protected
    Aged Eye of Fire   drops=0    (desynth only)    -> protected

CANDIDATE SET
-------------
Only items that could ever reach the discard call are classified -- everything else is
already excluded by AutoDiscard's hard rules, so asking Garland about it is wasted
traffic. The candidate filter here MUST stay in step with AutoDiscard.PassesHardRules /
MatchesMode; it is deliberately a superset (the vendor-price bound is the configurable
maximum, not the default).

USAGE
-----
    pip install -r tools/requirements.txt
    python tools/gen_mob_drops.py

Writes Relicable/Data/catalogs/mob_drop_items.json. Responses are cached under
tools/.cache/garland/, so a re-run costs no network. Use --refresh to re-fetch.
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import date
from pathlib import Path

import requests

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "Relicable" / "Data" / "catalogs" / "mob_drop_items.json"
CACHE = Path(__file__).resolve().parent / ".cache" / "garland"

XIVAPI = "https://v2.xivapi.com/api"
GARLAND = "https://garlandtools.org/db/doc/item/en/3/{id}.json"

# Politeness: Garland is a volunteer-run community service and this walks a few thousand items.
# The ceiling is a GLOBAL request rate, held regardless of how many workers are running, so
# raising --workers only hides round-trip latency (~600ms each) and never increases the load
# Garland sees. Serial fetching left the run latency-bound at ~0.8 req/s -- a two-hour
# regeneration for a table that has to be rebuilt every patch.
REQUESTS_PER_SECOND = 5.0
WORKERS = 4
TIMEOUT_S = 30
RETRIES = 3


class RateLimiter:
    """Global token bucket: no more than `rate` requests per second across all threads."""

    def __init__(self, rate: float) -> None:
        self._interval = 1.0 / rate
        self._lock = threading.Lock()
        self._next = 0.0

    def wait(self) -> None:
        with self._lock:
            now = time.monotonic()
            due = max(now, self._next)
            self._next = due + self._interval
        delay = due - now
        if delay > 0:
            time.sleep(delay)


# requests.Session is not documented thread-safe, so each worker gets its own.
_local = threading.local()


def worker_session() -> requests.Session:
    s = getattr(_local, "session", None)
    if s is None:
        s = requests.Session()
        s.headers["User-Agent"] = UA
        _local.session = s
    return s

# Mirrors AutoDiscard: the largest vendor price the user can configure as "clutter".
# Kept above the 100 default so lifting the setting never needs a table regeneration.
MAX_VENDOR_PRICE = 1000

UA = "Relicable-mobdrop-tooling/1.0 (Dalamud plugin build tool)"

ITEM_FIELDS = ",".join([
    "Name", "Rarity", "StackSize", "PriceLow", "IsUntradable", "IsUnique",
    "IsIndisposable", "EquipSlotCategory", "ItemAction", "MateriaSlotCount",
])


def log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def fetch_candidates(session: requests.Session) -> list[tuple[int, str]]:
    """Every item that AutoDiscard's hard rules + LowValueMaterials mode could select."""
    out: list[tuple[int, str]] = []
    after = 0
    scanned = 0
    while True:
        r = session.get(
            f"{XIVAPI}/sheet/Item",
            params={"fields": ITEM_FIELDS, "limit": 500, "after": after},
            timeout=TIMEOUT_S,
        )
        r.raise_for_status()
        rows = r.json().get("rows", [])
        if not rows:
            break
        for row in rows:
            f = row["fields"]
            scanned += 1
            if not f["Name"]:
                continue
            # --- must match AutoDiscard.PassesHardRules ---
            if f["IsUntradable"] or f["IsUnique"] or f["IsIndisposable"]:
                continue
            if f["EquipSlotCategory"]["row_id"] != 0:
                continue
            if f["ItemAction"]["row_id"] != 0:
                continue
            if f["MateriaSlotCount"] != 0:
                continue
            # --- must match AutoDiscard.MatchesMode (LowValueMaterials) ---
            if f["Rarity"] > 1 or f["StackSize"] <= 1:
                continue
            if f["PriceLow"] > MAX_VENDOR_PRICE:
                continue
            out.append((row["row_id"], f["Name"]))
        after = rows[-1]["row_id"]
    log(f"  scanned {scanned} items -> {len(out)} candidates")
    return out


def garland_item(limiter: RateLimiter, item_id: int, refresh: bool) -> dict | None:
    """Garland's document for one item, disk-cached. None when the lookup failed."""
    cached = CACHE / f"{item_id}.json"
    if cached.exists() and not refresh:
        try:
            return json.loads(cached.read_text(encoding="utf-8"))
        except Exception:
            pass  # corrupt cache entry -> re-fetch

    last: Exception | None = None
    for attempt in range(RETRIES):
        try:
            limiter.wait()
            r = worker_session().get(GARLAND.format(id=item_id), timeout=TIMEOUT_S)
            if r.status_code == 404:
                cached.write_text("{}", encoding="utf-8")
                return {}
            r.raise_for_status()
            doc = r.json()
            cached.write_text(json.dumps(doc), encoding="utf-8")
            return doc
        except Exception as exc:  # network hiccup / rate limit -> back off and retry
            last = exc
            time.sleep(1.5 * (attempt + 1))
    log(f"  ! item {item_id}: {last}")
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--refresh", action="store_true", help="ignore the on-disk cache")
    ap.add_argument("--limit", type=int, default=0, help="classify at most N candidates (smoke test)")
    ap.add_argument("--workers", type=int, default=WORKERS, help=f"parallel fetchers (default {WORKERS})")
    ap.add_argument("--rate", type=float, default=REQUESTS_PER_SECOND,
                    help=f"global requests/second ceiling (default {REQUESTS_PER_SECOND})")
    args = ap.parse_args()

    CACHE.mkdir(parents=True, exist_ok=True)
    OUT.parent.mkdir(parents=True, exist_ok=True)

    session = requests.Session()
    session.headers["User-Agent"] = UA

    log("Deriving the candidate set from XIVAPI v2 ...")
    candidates = fetch_candidates(session)
    if args.limit:
        candidates = candidates[: args.limit]

    log(f"Classifying {len(candidates)} candidates against Garland Tools "
        f"({args.workers} workers, {args.rate}/s ceiling) ...")
    limiter = RateLimiter(args.rate)
    drops: list[dict] = []
    unknown = 0
    done = 0
    t0 = time.time()
    lock = threading.Lock()

    def classify(entry: tuple[int, str]) -> None:
        nonlocal unknown, done
        item_id, name = entry
        doc = garland_item(limiter, item_id, args.refresh)
        mobs = (doc.get("item") or {}).get("drops") or [] if doc is not None else []
        with lock:
            done += 1
            if doc is None:
                unknown += 1      # lookup failed -> absent from the table -> NOT discarded
            elif mobs:
                drops.append({"id": item_id, "name": name, "mobs": len(mobs)})
            if done % 500 == 0:
                rate = done / max(1e-6, time.time() - t0)
                log(f"  {done}/{len(candidates)}  ({len(drops)} drops so far, {rate:.1f}/s)")

    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        list(pool.map(classify, candidates))

    drops.sort(key=lambda d: d["id"])
    payload = {
        "_comment": (
            "Items known to drop from enemies. AutoDiscard will not delete anything absent "
            "from this list. Generated by tools/gen_mob_drops.py -- do not hand-edit."
        ),
        "source": "garlandtools.org item.drops",
        "generated": date.today().isoformat(),
        "candidates": len(candidates),
        "unresolved": unknown,
        "items": drops,
    }
    OUT.write_text(json.dumps(payload, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")

    log("")
    log(f"Wrote {OUT.relative_to(REPO)}")
    log(f"  {len(drops)} enemy-drop items out of {len(candidates)} candidates")
    if unknown:
        log(f"  {unknown} candidate(s) could not be classified -> left protected")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
