"""Derive Relicable's leve mechanic tables from XIVAPI v2 game data.

Shared by gen_leve_tables.py (writes the C#) and validate_leve_tables.py
(diffs the C# against this). Deriving lives here so the generator and validator
can never disagree about what the sheets say.

The derivation chain (all verified against the live API):

  RelicNote/{book}.Leve[]            -> the 3 leve ids per Animus book (rows 1..9)
  Leve/{id}.{Name, DataId}           -> DataId resolves to BattleLeve or CompanyLeve
  BattleLeve.Rule.Rule               -> "BattleLeveHunt" | "BattleLeveRound" | ...
  CompanyLeve.Rule.Type              -> "CompanyLeveSummon" | "CompanyLeveProtection" | ...

  BattleLeveHunt   (item-lure): LeveData[] where
       source = the entry with ItemsInvolvedQty > 0    (the ENEMY killed for the key item)
       ItemId = that entry's ItemsInvolved             (the EventItem key item)
       emerge = the entry with ToDoNumberInvolved > 0 and a BNpcName  (the enemy to slay)
       prime  = the shared "prime location" EObj marker (PRIME_MARKER, a constant like
                ROUND_MARKER), the object the key item is used ON -- verified id 2000610
                for all three book lure leves (645 / 650 / 658)
  BattleLeveRound  (make the rounds): marker object is the constant "Destination"
  CompanyLeveSummon (named target): highest-EnemyLevel CompanyLeveStruct BNpcName

Everything else (Sweep, Orb/Necrologos, Guide/escort, Interception, Protection,
Penetration) has no derivable mechanic table -- see RULE_HANDLERS.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional

# The marker object name for BattleLeveRound "make the rounds" leves. This is a
# behavioural/object-table constant (not a sheet field), confirmed in-game; kept
# here so the one place it is asserted is explicit.
ROUND_MARKER = "Destination"

# The marker object the key item is USED ON in BattleLeveHunt item-lure leves: the
# "prime location" EObj (id 2000610), shared by every book lure leve (645 / 650 / 658)
# and NOT a per-leve BNpcName -- BattleLeve.LeveData records it only as a nameless
# summary row (BNpcName 0, BaseID = the EObj), so like ROUND_MARKER it is asserted here
# as the one behavioural constant. Re-verify if a future hunt leve uses a different marker.
PRIME_MARKER = "prime location"

# rule name -> (LeveRunner handler, note). The authoritative map of which of the 9
# Animus leve rules dispatches to which runner loop. New/unknown rules are flagged
# by the validator's coverage check.
RULE_HANDLERS: dict[str, tuple[str, str]] = {
    "BattleLeveSweep":        ("RunFight",              "plain kill leve"),
    "BattleLeveOrb":          ("RunFight+Parchment",    "Necrologos: read a Parchment to summon each wave"),
    "BattleLeveHunt":         ("RunItemLure",           "use a key item on a prime object to lure the target"),
    "BattleLeveRound":        ("RunDestination",        "travel to Destination markers to spring ambushes"),
    "BattleLeveGuide":        ("RunEscort",             "guide an NPC along an authored waypoint route"),
    "CompanyLeveSummon":      ("RunFight+NamedTarget",  "slay the summoner (highest-level struct enemy)"),
    "CompanyLeveInterception": ("RunFight",             "kill the objective enemies"),
    "CompanyLeveProtection":  ("RunFight+Anchor",       "defend: hold at the authored anchor"),
    "CompanyLevePenetration": ("RunFight (DEFERRED)",   "real poke/reveal mechanic unconfirmed -- verify in-game"),
}


def F(o: Any) -> Any:
    """Unwrap an XIVAPI row/ref object to its resolved `fields` (or itself)."""
    return o.get("fields", o) if isinstance(o, dict) else o


def _ref_id(x: Any) -> int:
    return x.get("row_id", 0) if isinstance(x, dict) else (x or 0)


@dataclass
class ItemLure:
    item_id: int
    prime: str
    emerge: str
    source: str = ""


@dataclass
class BookLeve:
    leve_id: int
    name: str
    rule: str
    handler: str


@dataclass
class Derived:
    item_lures: dict[str, ItemLure] = field(default_factory=dict)      # leve name -> lure
    named_targets: dict[str, str] = field(default_factory=dict)        # leve name -> BNpc
    destinations: dict[str, str] = field(default_factory=dict)         # leve name -> marker
    book_leves: list[BookLeve] = field(default_factory=list)           # every book leve
    warnings: list[str] = field(default_factory=list)                  # derivation anomalies


def enumerate_book_leves(client) -> list[tuple[int, list[int]]]:
    """Every RelicNote book (row with leves) -> its leve ids, in row order."""
    books: list[tuple[int, list[int]]] = []
    after: Optional[int] = None
    scanned = 0
    while scanned < 1000:
        page = client.rows("RelicNote", fields=["Leve"], limit=100, after=after)
        rows = page.get("rows", [])
        if not rows:
            break
        for r in rows:
            leves = [i for i in (_ref_id(x) for x in (F(r).get("Leve") or [])) if i]
            if leves:
                books.append((r["row_id"], leves))
        scanned += len(rows)
        nxt = rows[-1]["row_id"]
        if nxt == after:
            break
        after = nxt
    return books


def _rule_of(sheet: str, data_fields: dict) -> Optional[str]:
    """BattleLeve.Rule.Rule or CompanyLeve.Rule.Type -> the rule name string."""
    rule = F(data_fields.get("Rule", {}))
    if not isinstance(rule, dict):
        return None
    return rule.get("Rule") or rule.get("Type")


def _derive_item_lure(leve_id: int, name: str, data_fields: dict, out: Derived) -> None:
    entries = [F(e) for e in (data_fields.get("LeveData") or [])]

    def _named(e):
        return F(e.get("BNpcName", {})).get("Singular", "")

    # Three roles in a hunt leve's LeveData (verified against 645 / 650 / 658):
    #   source = the entry carrying the key item (ItemsInvolvedQty>0): the ENEMY you kill
    #            for it; its ItemsInvolved is the item id. (The earlier model wrongly used
    #            this entry's BNpcName as the "prime" -- but you FIGHT it, you don't use the
    #            item on it, so the loop never spawned the target and stalled.)
    #   emerge = the entry with ToDoNumberInvolved>0 AND a BNpcName: the enemy to slay.
    #            LeveData[0] also has ToDoNumberInvolved>0 but no BNpcName (the summary row),
    #            so the name requirement isolates the real slay target.
    #   prime  = the shared "prime location" EObj marker (PRIME_MARKER) the item is used ON.
    #            It is a nameless EObj summary row, not a BNpcName, so it is a constant.
    sources = [e for e in entries if (e.get("ItemsInvolvedQty") or 0) > 0 and _named(e)]
    emerges = [e for e in entries if (e.get("ToDoNumberInvolved") or 0) > 0 and _named(e)]
    if len(sources) != 1 or len(emerges) != 1:
        out.warnings.append(
            f"Leve {leve_id} '{name}': item-lure derivation ambiguous "
            f"(sources={len(sources)}, emerges={len(emerges)}); left out of ItemLures")
        return
    source = sources[0]
    emerge = emerges[0]
    item_id = _ref_id(source.get("ItemsInvolved"))
    source_name = F(source.get("BNpcName", {})).get("Singular", "")
    emerge_name = F(emerge.get("BNpcName", {})).get("Singular", "")
    if not (item_id and source_name and emerge_name):
        out.warnings.append(f"Leve {leve_id} '{name}': item-lure missing item/source/emerge; skipped")
        return
    out.item_lures[name] = ItemLure(item_id, PRIME_MARKER, emerge_name, source_name)


def _derive_named_target(leve_id: int, name: str, data_fields: dict, out: Derived) -> None:
    entries = [F(e) for e in (data_fields.get("CompanyLeveStruct") or [])]
    named = [(e, F(e.get("BNpcName", {})).get("Singular", ""), e.get("EnemyLevel") or 0) for e in entries]
    named = [t for t in named if t[1]]
    if not named:
        out.warnings.append(f"Leve {leve_id} '{name}': CompanyLeveSummon has no named struct enemy; skipped")
        return
    # Highest-level struct enemy is the summoner (verified: it is index 0 for all three).
    target = max(named, key=lambda t: t[2])[1]
    out.named_targets[name] = target


def derive(client) -> Derived:
    """Walk RelicNote -> Leve -> BattleLeve/CompanyLeve and derive every table."""
    out = Derived()
    seen: set[int] = set()
    for _book, leves in enumerate_book_leves(client):
        for leve_id in leves:
            if leve_id in seen:
                continue
            seen.add(leve_id)

            lf = F(client.row("Leve", leve_id, fields=["Name", "DataId"]))
            name = lf.get("Name") or f"Leve {leve_id}"
            di = lf.get("DataId", {})
            sheet = di.get("sheet")
            drow = di.get("row_id")
            if not sheet or not drow:
                out.warnings.append(f"Leve {leve_id} '{name}': no DataId; skipped")
                continue

            fields = ["Rule", "LeveData"] if sheet == "BattleLeve" else ["Rule", "CompanyLeveStruct"]
            data = F(client.row(sheet, drow, fields=fields))
            rule = _rule_of(sheet, data)
            if not rule:
                out.warnings.append(f"Leve {leve_id} '{name}': could not resolve {sheet}.Rule; skipped")
                continue

            handler = RULE_HANDLERS.get(rule, ("<UNKNOWN>", "no handler mapped for this rule"))[0]
            out.book_leves.append(BookLeve(leve_id, name, rule, handler))

            if rule == "BattleLeveHunt":
                _derive_item_lure(leve_id, name, data, out)
            elif rule == "BattleLeveRound":
                out.destinations[name] = ROUND_MARKER
            elif rule == "CompanyLeveSummon":
                _derive_named_target(leve_id, name, data, out)

    out.book_leves.sort(key=lambda b: b.leve_id)
    return out


# --------------------------------------------------------------------------- #
# Dungeon-territory derivation (for the validator's stale-TerritoryType check)
# --------------------------------------------------------------------------- #
import re
import unicodedata


def _norm_name(s: Optional[str]) -> str:
    """Normalise a place name for matching: NFKD-fold diacritics, unify dash and
    apostrophe variants, drop a leading article, casefold. So "The Tam-Tara
    Deepcroft" (PlaceName) matches "the Tam-Tara Deepcroft" (ContentFinderCondition)
    across en-dash / curly-apostrophe / accent differences."""
    s = s or ""
    s = unicodedata.normalize("NFKD", s)
    s = "".join(c for c in s if not unicodedata.combining(c))
    s = s.replace("’", "'").replace("‘", "'")
    for dash in ("‐", "‑", "‒", "–", "—", "―"):
        s = s.replace(dash, "-")
    s = s.strip().lower()
    s = re.sub(r"^the\s+", "", s)
    return s


def dungeon_name_to_territory(client) -> dict[str, int]:
    """Map normalised dungeon name -> lowest (original) TerritoryType, from every
    ContentFinderCondition with ContentType 2 (Dungeon). Mirrors the runtime
    RelicNoteDataGenerator 'standardByName' normalisation (original ARR instance)."""
    res = client.search("ContentType=2", ["ContentFinderCondition"],
                        fields=["Name", "TerritoryType.RowId"], limit=500)
    rows = res.get("results", res.get("rows", []))
    name2terr: dict[str, int] = {}
    for r in rows:
        f = F(r.get("fields", r))
        nm = _norm_name(f.get("Name"))
        tt = f.get("TerritoryType", {})
        terr = _ref_id(tt)
        if nm and terr and (nm not in name2terr or terr < name2terr[nm]):
            name2terr[nm] = terr
    return name2terr


def nm_target_dungeon_name(client, nm_id: int) -> Optional[str]:
    """A notorious-monster MonsterNoteTarget's dungeon place name (PlaceNameLocation[0])."""
    mnt = F(client.row("MonsterNoteTarget", nm_id, fields=["PlaceNameLocation"]))
    loc = mnt.get("PlaceNameLocation") or []
    return F(loc[0]).get("Name") if loc else None


# --------------------------------------------------------------------------- #
# C# rendering
# --------------------------------------------------------------------------- #
GENERATED_PATH_REL = "Relicable/Data/LeveTables.Generated.cs"


def _cs(s: str) -> str:
    """Escape a Python string for a C# double-quoted verbatim-safe literal."""
    return s.replace("\\", "\\\\").replace('"', '\\"')


def render_cs(d: Derived) -> str:
    """Render the derived tables as a deterministic C# source file. Deliberately
    carries NO wall-clock timestamp or volatile version so `--check` only flags
    real data changes (regenerate to pick up game-data changes)."""
    lines: list[str] = []
    W = lines.append
    W("// <auto-generated />")
    W("// AUTO-GENERATED by tools/gen_leve_tables.py from XIVAPI v2 game data. DO NOT EDIT BY HAND.")
    W("// Regenerate:  python tools/gen_leve_tables.py")
    W("// Validate:    python tools/validate_leve_tables.py   (add --check to fail CI on drift)")
    W("//")
    W("// This is currently REFERENCE + VALIDATION data: the hand-authored Data/Leve*.cs tables")
    W("// remain the runtime source of truth. Wiring LeveRunner to dispatch off BookLeves.Rule")
    W("// (keyed by leve id, name as fallback) is the planned P0 rule-dispatch refactor.")
    W("")
    W("using System.Collections.Generic;")
    W("")
    W("namespace Relicable.Data;")
    W("")
    W("public static class GeneratedLeveTables")
    W("{")
    W("    public sealed record ItemLure(uint ItemId, string PrimeTargetName, string EmergeTargetName, string ItemSourceName);")
    W("    public sealed record BookLeve(uint LeveId, string Name, string Rule, string Handler);")
    W("")
    # ItemLures
    W("    // BattleLeveHunt item-lure leves. Derived from BattleLeve.LeveData: source = ItemsInvolvedQty>0")
    W("    // entry (the enemy killed for the item), ItemId = its ItemsInvolved, emerge = ToDoNumberInvolved>0")
    W("    // + BNpcName entry (the slay target), prime = the shared \"prime location\" EObj marker.")
    W("    public static readonly IReadOnlyDictionary<string, ItemLure> ItemLures =")
    W("        new Dictionary<string, ItemLure>(System.StringComparer.OrdinalIgnoreCase)")
    W("        {")
    for name in sorted(d.item_lures):
        l = d.item_lures[name]
        W(f'            ["{_cs(name)}"] = new ItemLure({l.item_id}u, "{_cs(l.prime)}", "{_cs(l.emerge)}", "{_cs(l.source)}"),')
    W("        };")
    W("")
    # NamedTargets
    W("    // CompanyLeveSummon named targets. Value = highest-EnemyLevel CompanyLeveStruct BNpcName.")
    W("    public static readonly IReadOnlyDictionary<string, string> NamedTargets =")
    W("        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)")
    W("        {")
    for name in sorted(d.named_targets):
        W(f'            ["{_cs(name)}"] = "{_cs(d.named_targets[name])}",')
    W("        };")
    W("")
    # Destinations
    W("    // BattleLeveRound \"make the rounds\" leves. Value is the marker object name (a constant).")
    W("    public static readonly IReadOnlyDictionary<string, string> Destinations =")
    W("        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)")
    W("        {")
    for name in sorted(d.destinations):
        W(f'            ["{_cs(name)}"] = "{_cs(d.destinations[name])}",')
    W("        };")
    W("")
    # BookLeves (the rule -> handler dispatch reference)
    W("    // Every Animus (Trials of the Braves) book leve, its game rule, and the LeveRunner handler")
    W("    // that rule dispatches to. The seed for the rule-dispatch refactor and coverage validation.")
    W("    public static readonly IReadOnlyList<BookLeve> BookLeves = new BookLeve[]")
    W("    {")
    for b in d.book_leves:
        W(f'        new({b.leve_id}u, "{_cs(b.name)}", "{_cs(b.rule)}", "{_cs(b.handler)}"),')
    W("    };")
    W("}")
    return "\n".join(lines) + "\n"
