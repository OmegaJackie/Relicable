"""Derive the Zodiac base-relic quest data that XIVAPI v2 *can* produce.

Scope: the ten ARR per-job "A Relic Reborn (<weapon>)" forge quests. For each,
the game sheets give the bookend NPC (Gerolt), his verified world position (via
the Level sheet), and the quest metadata (level, journal genre, prerequisite
chain, turn-in NPC). They do NOT give the per-sequence trial gauntlet: Quest's
`TodoParams` is empty for these quests, so the Chimera -> class weapon ->
Amdapor Keep -> 24 beastmen -> Hydra -> 3 primals -> forge flow is authored in
`BaseRelicData.GlobalParts` (calibrated from `/relic questwork`), not derivable.

This module fetches the derivable half and emits three artifacts under
`tools/generated/` (NOT the plugin's shipped `Relicable/Data/questpaths`, so
loading it never runs a half-authored path):

  * `zodiac_relic_quests.json`  -- the full "everything the API gave us" dump,
    deterministic so `validate_quest_tables.py --check` can gate drift.
  * `questpaths/<masked>_A Relic Reborn (<weapon>).json` -- a bookend quest-path
    skeleton per job (accept @ Gerolt seq 0, complete @ Gerolt seq 255), in the
    qstxiv schema the plugin's QuestPathLoader consumes. The trial-gauntlet
    sequences in between are left as annotated TODOs.
  * `quest_gap_checklist.md` -- exactly what still needs an in-game capture,
    separating the ONE-TIME shared calibration (GlobalParts) from the per-job
    hand-authored facts, so you fill the minimum, once.

Reference: https://v2.xivapi.com/docs/welcome/
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from typing import Optional

from xivapi_client import XIVAPIClient

# ------------------------------------------------------------------ #
# Constants
# ------------------------------------------------------------------ #

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# Everything this tool writes lives here, OUTSIDE the plugin's compiled tree, so
# a generated skeleton never loads at runtime until you deliberately promote it.
GENERATED_DIR_REL = os.path.join("tools", "generated")
DATA_JSON_REL = os.path.join(GENERATED_DIR_REL, "zodiac_relic_quests.json")
SKELETON_DIR_REL = os.path.join(GENERATED_DIR_REL, "questpaths")
CHECKLIST_REL = os.path.join(GENERATED_DIR_REL, "quest_gap_checklist.md")

# Stage-intro (Atma..Zeta) artifacts -- see derive_stage_quests.
STAGE_DATA_JSON_REL = os.path.join(GENERATED_DIR_REL, "zodiac_stage_quests.json")
STAGE_SKELETON_DIR_REL = os.path.join(GENERATED_DIR_REL, "questpaths_stages")
STAGE_CHECKLIST_REL = os.path.join(GENERATED_DIR_REL, "stage_gap_checklist.md")

# The live plugin quest-path directory -- read-only here, only to flag jobs that
# already have a hand-authored file so the checklist can say "don't double-promote".
LIVE_QUESTPATH_DIR_REL = os.path.join("Relicable", "Data", "questpaths")

# The base-relic NPC for every job (accept + every turn-in). Used to sanity-check
# that each quest really is issued by Gerolt; a mismatch is surfaced, not assumed.
GEROLT_DATA_ID = 1003075

# Every "A Relic Reborn (<weapon>)" forge quest. `~` is XIVAPI's partial match.
RELIC_QUEST_QUERY = 'Name~"A Relic Reborn"'

# The C# the validator/checklist reads back, so this tooling stays in step with
# the hand-authored source of truth instead of hard-coding a second copy.
REGISTRY_CS_REL = os.path.join("Relicable", "Data", "ZodiacQuestRegistry.cs")
BASE_RELIC_DATA_CS_REL = os.path.join("Relicable", "BaseRelic", "BaseRelicData.cs")


# ------------------------------------------------------------------ #
# Model
# ------------------------------------------------------------------ #

@dataclass
class Npc:
    data_id: int
    name: Optional[str] = None
    # World position from the Level sheet (None when the NPC has no Level row).
    x: Optional[float] = None
    y: Optional[float] = None
    z: Optional[float] = None
    territory: Optional[int] = None


@dataclass
class RelicQuest:
    full_id: int
    masked_id: int
    name: str
    weapon: str
    issuer: Npc
    target: Npc
    level: int
    journal_genre_id: Optional[int]
    journal_genre_name: Optional[str]
    previous_quests: list  # list[[id, name]]
    instance_content: list  # list[[id, name]] -- non-zero only
    has_live_questpath: bool = False


@dataclass
class QuestPull:
    """The fields both the per-job and the stage-intro derivations read from a
    Quest row -- pulled once by `_pull_quest`."""
    name: str
    weapon: str
    issuer: Npc
    target: Npc
    level: int
    journal_genre_id: Optional[int]
    journal_genre_name: Optional[str]
    previous_quests: list
    instance_content: list


@dataclass
class StageQuest:
    full_id: int
    masked_id: int
    name: str
    stage: str
    role: str
    issuer: Npc
    target: Npc
    level: int
    journal_genre_id: Optional[int]
    journal_genre_name: Optional[str]
    previous_quests: list
    instance_content: list


@dataclass
class GlobalPart:
    part: int
    name: str
    completed_at_sequence: int
    active_from_sequence: int
    has_completion_flags: bool


@dataclass
class Derived:
    quests: list = field(default_factory=list)
    global_parts: list = field(default_factory=list)  # list[GlobalPart]
    warnings: list = field(default_factory=list)


# ------------------------------------------------------------------ #
# XIVAPI helpers
# ------------------------------------------------------------------ #

def _rel(value):
    """Split a relation field into (id, name). Accepts the {value, fields}
    shape XIVAPI returns for a relation column, a bare int, or None."""
    if isinstance(value, dict):
        rid = value.get("value")
        sub = value.get("fields") or {}
        name = sub.get("Singular") or sub.get("Name") or None
        return rid, (name.strip() if isinstance(name, str) and name.strip() else None)
    if isinstance(value, int):
        return value, None
    return None, None


def _npc_position(client: XIVAPIClient, npc_id: int, cache: dict) -> Npc:
    """Resolve an ENpcResident's name and its Level-sheet spawn position. The
    Level row is how Questionable (and the existing 1125 Bard path) get the
    accept/turn-in coordinate; many NPCs have exactly one, some have none."""
    if npc_id in cache:
        return cache[npc_id]

    npc = Npc(data_id=npc_id)
    try:
        row = client.row("ENpcResident", npc_id, fields=["Singular"])
        singular = (row.get("fields") or {}).get("Singular")
        if isinstance(singular, str) and singular.strip():
            npc.name = singular.strip()
    except Exception:
        pass

    try:
        res = client.search(f"Object={npc_id}", "Level",
                            fields=["X", "Y", "Z", "Territory"], limit=1)
        rows = res.get("results") or []
        if rows:
            f = rows[0].get("fields") or {}
            npc.x, npc.y, npc.z = f.get("X"), f.get("Y"), f.get("Z")
            terr, _ = _rel(f.get("Territory"))
            npc.territory = terr
    except Exception:
        pass

    cache[npc_id] = npc
    return npc


_WEAPON_RE = re.compile(r"\(([^)]+)\)\s*$")


def _weapon_of(name: str) -> str:
    m = _WEAPON_RE.search(name or "")
    return m.group(1).strip() if m else ""


# ------------------------------------------------------------------ #
# Derivation
# ------------------------------------------------------------------ #

def _live_questpath_masked_ids() -> set:
    """Masked ids that already have a hand-authored file in the shipped plugin
    dir, so the checklist can warn against promoting a generated duplicate."""
    ids = set()
    live = os.path.join(REPO, LIVE_QUESTPATH_DIR_REL)
    if not os.path.isdir(live):
        return ids
    for fn in os.listdir(live):
        if fn.lower().endswith(".json"):
            m = re.match(r"(\d+)_", fn)
            if m:
                ids.add(int(m.group(1)))
    return ids


def _parse_global_parts() -> list:
    """Best-effort read of BaseRelicData.GlobalParts so the gap checklist can
    list precisely which trial parts still need a live sequence capture. Regex,
    not a C# parser -- if the shape ever changes this quietly yields [] and the
    checklist falls back to its generic guidance."""
    path = os.path.join(REPO, BASE_RELIC_DATA_CS_REL)
    try:
        with open(path, encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return []

    # Isolate the GlobalParts array so we don't match QuestPart-shaped text elsewhere.
    start = text.find("GlobalParts")
    if start < 0:
        return []
    region = text[start:text.find("SharedConsumables", start) if "SharedConsumables" in text[start:] else len(text)]

    parts = []
    for block in re.split(r"new QuestPart", region)[1:]:
        pm = re.search(r"Part\s*=\s*(\d+)", block)
        nm = re.search(r'Name\s*=\s*"([^"]*)"', block)
        if not pm or not nm:
            continue
        cm = re.search(r"CompletedAtSequence\s*=\s*(\d+)", block)
        am = re.search(r"ActiveFromSequence\s*=\s*(\d+)", block)
        parts.append(GlobalPart(
            part=int(pm.group(1)),
            name=nm.group(1),
            completed_at_sequence=int(cm.group(1)) if cm else 0,
            active_from_sequence=int(am.group(1)) if am else 0,
            has_completion_flags="CompletionQuestVariablesFlags" in block,
        ))
    parts.sort(key=lambda p: p.part)
    return parts


def _pull_quest(client: XIVAPIClient, qid: int, npc_cache: dict) -> QuestPull:
    """Read the derivable fields of one Quest row (used by both derivations)."""
    row = client.row("Quest", qid, fields=[
        "Name", "IssuerStart", "TargetEnd", "ClassJobLevel",
        "JournalGenre", "PreviousQuest", "InstanceContent",
    ])
    f = row.get("fields") or {}
    name = f.get("Name") or ""

    issuer_id, issuer_name = _rel(f.get("IssuerStart"))
    target_id, target_name = _rel(f.get("TargetEnd"))
    issuer = _npc_position(client, issuer_id, npc_cache) if issuer_id else Npc(0)
    target = _npc_position(client, target_id, npc_cache) if target_id else Npc(0)
    # Prefer the auto-resolved relation name if the row lookup missed it.
    issuer.name = issuer.name or issuer_name
    target.name = target.name or target_name

    cj = f.get("ClassJobLevel") or []
    level = next((v for v in cj if isinstance(v, int) and v > 0), 0)
    jg_id, jg_name = _rel(f.get("JournalGenre"))

    prev = []
    for p in f.get("PreviousQuest") or []:
        pid, pname = _rel(p)
        if pid:
            prev.append([pid, pname])
    inst = []
    for ic in f.get("InstanceContent") or []:
        iid, iname = _rel(ic)
        if iid:
            inst.append([iid, iname])

    return QuestPull(name=name, weapon=_weapon_of(name), issuer=issuer, target=target,
                     level=level, journal_genre_id=jg_id, journal_genre_name=jg_name,
                     previous_quests=prev, instance_content=inst)


def derive(client: XIVAPIClient) -> Derived:
    out = Derived()
    npc_cache: dict = {}
    live_ids = _live_questpath_masked_ids()

    res = client.search(RELIC_QUEST_QUERY, "Quest", fields=["Name"], limit=100)
    rows = res.get("results") or []
    ids = sorted(r["row_id"] for r in rows)
    if not ids:
        out.warnings.append("no 'A Relic Reborn (<weapon>)' quests matched -- API schema or name changed?")
        return out

    for qid in ids:
        p = _pull_quest(client, qid, npc_cache)
        if not p.weapon:
            # A bare "A Relic Reborn" with no weapon is the shared story quest,
            # not a per-job forge quest -- skip it (the ten forge quests all
            # carry a "(<weapon>)" suffix).
            continue

        masked = qid & 0xFFFF
        if p.issuer.data_id and p.issuer.data_id != GEROLT_DATA_ID:
            out.warnings.append(f"{p.name} (id {qid}) issuer is {p.issuer.name or p.issuer.data_id}, "
                                f"not Gerolt ({GEROLT_DATA_ID}) -- accept step needs review")
        if p.issuer.x is None:
            out.warnings.append(f"{p.name}: no Level position for issuer {p.issuer.name or p.issuer.data_id} "
                                f"-- accept-step Position left null (fill in-game)")

        out.quests.append(RelicQuest(
            full_id=qid, masked_id=masked, name=p.name, weapon=p.weapon,
            issuer=p.issuer, target=p.target, level=p.level,
            journal_genre_id=p.journal_genre_id, journal_genre_name=p.journal_genre_name,
            previous_quests=p.previous_quests, instance_content=p.instance_content,
            has_live_questpath=masked in live_ids,
        ))

    out.quests.sort(key=lambda q: q.masked_id)
    out.global_parts = _parse_global_parts()
    return out


# The Zodiac stage order, so the stage checklist reads top-to-bottom in play order.
_STAGE_ORDER = ["Relic", "Atma", "Animus", "Novus", "Nexus", "Braves", "Zeta", "Zenith"]


def _parse_registry(text: str):
    """(id, name, stage, role) for every ZodiacQuest(...) plus the LineUnlock const."""
    out = []
    m = re.search(r"WeaponsmithOfLegendId\s*=\s*(\d+)", text)
    if m:
        out.append((int(m.group(1)), "The Weaponsmith of Legend", "Relic", "LineUnlock"))
    for qm in re.finditer(
            r'new ZodiacQuest\(\s*RelicStage\.(\w+)\s*,\s*"([^"]+)"\s*,\s*(\d+)\s*,\s*ZodiacQuestRole\.(\w+)',
            text):
        out.append((int(qm.group(3)), qm.group(2), qm.group(1), qm.group(4)))
    return out


def derive_stage_quests(client: XIVAPIClient) -> Derived:
    """Derive the one-time stage-opening quests (Atma..Zeta and the line unlock)
    from ZodiacQuestRegistry. Unlike the per-job forge quests these are not
    job-specific and each stage's grind (12 Atma, 9 books, Light, ...) is already
    plugin-driven, so the only derivable path data is the accept/turn-in bookends."""
    out = Derived()
    npc_cache: dict = {}
    entries = _parse_registry(_read_cs(REGISTRY_CS_REL))
    if not entries:
        out.warnings.append("could not parse ZodiacQuestRegistry.cs -- stage quests skipped")
        return out

    for qid, name, stage, role in entries:
        p = _pull_quest(client, qid, npc_cache)
        if p.issuer.x is None:
            out.warnings.append(f"{name} ({stage}): no Level position for issuer "
                                f"{p.issuer.name or p.issuer.data_id} -- accept Position left null")
        out.quests.append(StageQuest(
            full_id=qid, masked_id=qid & 0xFFFF, name=name, stage=stage, role=role,
            issuer=p.issuer, target=p.target, level=p.level,
            journal_genre_id=p.journal_genre_id, journal_genre_name=p.journal_genre_name,
            previous_quests=p.previous_quests, instance_content=p.instance_content,
        ))

    out.quests.sort(key=lambda q: (_STAGE_ORDER.index(q.stage) if q.stage in _STAGE_ORDER else 99,
                                   q.masked_id))
    return out


def _read_cs(path_rel: str) -> str:
    try:
        with open(os.path.join(REPO, path_rel), encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


# ------------------------------------------------------------------ #
# Rendering
# ------------------------------------------------------------------ #

def _npc_json(npc: Npc) -> dict:
    d = {"DataId": npc.data_id, "Name": npc.name}
    if npc.x is not None:
        d["Position"] = {"X": npc.x, "Y": npc.y, "Z": npc.z}
        d["TerritoryId"] = npc.territory
    return d


def render_data_json(derived: Derived) -> str:
    """The deterministic 'everything the API gave us' dump (no timestamps)."""
    quests = []
    for q in derived.quests:
        quests.append({
            "MaskedId": q.masked_id,
            "FullId": q.full_id,
            "Name": q.name,
            "Weapon": q.weapon,
            "Level": q.level,
            "JournalGenre": {"Id": q.journal_genre_id, "Name": q.journal_genre_name},
            "Issuer": _npc_json(q.issuer),
            "TurnIn": _npc_json(q.target),
            "PreviousQuests": q.previous_quests,
            "InstanceContent": q.instance_content,
            "HasLiveQuestPath": q.has_live_questpath,
        })
    doc = {
        "_note": "Generated by tools/gen_quest_tables.py from XIVAPI v2. "
                 "The per-sequence trial gauntlet is NOT here (Quest.TodoParams is "
                 "empty for these quests); it lives in BaseRelicData.GlobalParts.",
        "Source": "https://v2.xivapi.com/api/sheet/Quest",
        "Quests": quests,
    }
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def render_skeleton(q: RelicQuest) -> str:
    """A bookend quest-path skeleton (qstxiv schema) with the trial-gauntlet
    middle left as an annotated TODO. Emitted with `//` comments, which the
    plugin's QuestPathLoader tolerates (ReadCommentHandling = Skip)."""
    def step(interaction: str) -> str:
        npc = q.issuer if interaction == "AcceptQuest" else q.target
        lines = [
            "        {",
            f'          "DataId": {npc.data_id},',
        ]
        if npc.x is not None:
            lines += [
                '          "Position": {',
                f'            "X": {npc.x},',
                f'            "Y": {npc.y},',
                f'            "Z": {npc.z}',
                "          },",
                f'          "TerritoryId": {npc.territory},',
            ]
        else:
            lines.append('          // TODO: no Level position in the sheets -- capture in-game.')
        lines += [
            f'          "InteractionType": "{interaction}",',
            '          "Fly": true',
            "        }",
        ]
        return "\n".join(lines)

    return f"""{{
  "$schema": "https://qstxiv.github.io/schema/quest-v1.json",
  "Author": "Relicable gen_quest_tables (XIVAPI v2 derivable bookends only)",
  // GENERATED SKELETON for {q.name} (masked id {q.masked_id}).
  // Only the bookends are derivable from the game sheets: accept + turn-in at
  // {q.issuer.name or 'the issuer'} (id {q.issuer.data_id}). The trial gauntlet in between
  // (Chimera, class weapon, Amdapor Keep, 24 beastmen, Hydra, 3 primals, oils) is
  // NOT in Quest.TodoParams; it is driven by BaseRelicData.GlobalParts, calibrated
  // from /relic questwork. Fill sequences 1..N below from an in-game capture, then
  // move this file into Relicable/Data/questpaths/ to activate it (and bump the
  // csproj version). See quest_gap_checklist.md.
  "QuestSequence": [
    {{
      "Sequence": 0,
      "Steps": [
{step("AcceptQuest")}
      ]
    }},
    // TODO: sequences 1..N -- the trial gauntlet. Capture each live sequence with
    // /relic questwork (giver/target DataId, Position, TerritoryId, InteractionType,
    // and for Combat/Duty the enemy name + count / duty territory).
    {{
      "Sequence": 255,
      "Steps": [
{step("CompleteQuest")}
      ]
    }}
  ]
}}
"""


def render_checklist(derived: Derived) -> str:
    lines = [
        "# Zodiac base-relic quest gap checklist",
        "",
        "Generated by `tools/gen_quest_tables.py`. It lists what XIVAPI v2 could **not**",
        "give for the ten ARR `A Relic Reborn (<weapon>)` forge quests, and therefore",
        "still needs an in-game `/relic questwork` capture or a hand-authored value.",
        "",
        "The API fully covers: quest id, name, weapon/job, level, journal genre,",
        "prerequisite chain, and the accept/turn-in NPC (Gerolt) with his verified",
        "world position. Those are in `zodiac_relic_quests.json` and the skeletons.",
        "",
        "## 1. Shared trial gauntlet — capture ONCE, applies to all 10 jobs",
        "",
        "`Quest.TodoParams` is empty for these quests, so the per-sequence flow is not",
        "derivable. It is authored in `Relicable/BaseRelic/BaseRelicData.cs` -> `GlobalParts`",
        "and is the same for every job (only the beastmen/stronghold differ, see §2).",
        "Run `/relic questwork` while the quest is active and read the live sequence to",
        "fill these:",
        "",
    ]
    if derived.global_parts:
        lines.append("| Part | Name | CompletedAtSequence | ActiveFromSequence | Work-byte flags | Still needs capture? |")
        lines.append("|---|---|---|---|---|---|")
        for p in derived.global_parts:
            need = []
            if p.completed_at_sequence == 0:
                need.append("seq")
            if not p.has_completion_flags:
                need.append("flags(optional)")
            lines.append(
                f"| {p.part} | {p.name} | {p.completed_at_sequence or '—'} | "
                f"{p.active_from_sequence or '—'} | {'yes' if p.has_completion_flags else 'no'} | "
                f"{', '.join(need) if need else 'calibrated'} |")
        uncal = [p.part for p in derived.global_parts if p.completed_at_sequence == 0]
        lines += [
            "",
            f"**Sequence still uncalibrated:** {uncal or 'none — all parts have a CompletedAtSequence'}.",
            "Work-byte flags (`CompletionQuestVariablesFlags`) are optional — the sequence",
            "gate already drives completion; flags only add sub-sequence precision.",
        ]
    else:
        lines.append("_(Could not parse GlobalParts from BaseRelicData.cs — fill the sequence"
                     " values there by reading `/relic questwork` at each trial.)_")

    lines += [
        "",
        "## 2. Per-job facts the API can only partially validate",
        "",
        "These live in `BaseRelicData.ByJob` and come from the wiki, not the sheets:",
        "the broken-weapon stronghold + coords, the three beastman target **names**",
        "(the quest text gates on exact names — e.g. Dragoon needs *Swiftbeak*, not",
        "*Windtalon*) and their coords, the class weapon, and craft materials.",
        "`validate_quest_tables.py` checks the weapon name resolves to a live quest;",
        "the coordinates and beastman names still need eyes.",
        "",
        "## 3. Optional: fully author a quest-path per job",
        "",
        "The skeletons in `questpaths/` cover only sequence 0 (accept) and 255",
        "(turn-in). Authoring the middle sequences is optional — the trial gauntlet",
        "already runs from GlobalParts. Only flesh a skeleton out if you want the",
        "sequence-accurate quest-path model to drive that job end-to-end.",
        "",
        "## Per-job status",
        "",
        "| Masked id | Weapon | Issuer | Turn-in | Issuer pos | Live path exists |",
        "|---|---|---|---|---|---|",
    ]
    for q in derived.quests:
        pos = "yes" if q.issuer.x is not None else "**MISSING**"
        lines.append(
            f"| {q.masked_id} | {q.weapon} | {q.issuer.name or q.issuer.data_id} | "
            f"{q.target.name or q.target.data_id} | {pos} | "
            f"{'yes (do not double-promote)' if q.has_live_questpath else 'no'} |")

    if derived.warnings:
        lines += ["", "## Warnings from generation", ""]
        lines += [f"- {w}" for w in derived.warnings]
    lines.append("")
    return "\n".join(lines)


# ------------------------------------------------------------------ #
# Stage-intro rendering (Atma..Zeta)
# ------------------------------------------------------------------ #

def render_stage_data_json(derived: Derived) -> str:
    quests = []
    for q in derived.quests:
        quests.append({
            "MaskedId": q.masked_id,
            "FullId": q.full_id,
            "Name": q.name,
            "Stage": q.stage,
            "Role": q.role,
            "Level": q.level,
            "JournalGenre": {"Id": q.journal_genre_id, "Name": q.journal_genre_name},
            "Issuer": _npc_json(q.issuer),
            "TurnIn": _npc_json(q.target),
            "PreviousQuests": q.previous_quests,
            "InstanceContent": q.instance_content,
        })
    doc = {
        "_note": "Generated by tools/gen_quest_tables.py from XIVAPI v2. The one-time "
                 "stage-opening quests. Each stage's grind is plugin-driven, so only the "
                 "accept/turn-in bookends are derivable path data.",
        "Source": "https://v2.xivapi.com/api/sheet/Quest",
        "Quests": quests,
    }
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def render_stage_skeleton(q: StageQuest) -> str:
    """Bookend skeleton for a stage-opening quest: accept @ issuer seq 0, turn-in
    @ target seq 255. NOTE: the current QuestPathLoader only consumes per-job
    Relic-stage paths (it resolves a job from a weapon in the filename), so these
    are authoring/reference artifacts until the loader is extended for other stages."""
    def step(interaction: str) -> str:
        npc = q.issuer if interaction == "AcceptQuest" else q.target
        out = ["        {", f'          "DataId": {npc.data_id},']
        if npc.x is not None:
            out += ['          "Position": {', f'            "X": {npc.x},',
                    f'            "Y": {npc.y},', f'            "Z": {npc.z}', "          },",
                    f'          "TerritoryId": {npc.territory},']
        else:
            out.append('          // TODO: no Level position in the sheets -- capture in-game.')
        out += [f'          "InteractionType": "{interaction}",', '          "Fly": true', "        }"]
        return "\n".join(out)

    return f"""{{
  "$schema": "https://qstxiv.github.io/schema/quest-v1.json",
  "Author": "Relicable gen_quest_tables (XIVAPI v2 bookends only)",
  // GENERATED SKELETON for {q.name} ({q.stage} stage, masked id {q.masked_id}).
  // Accept @ {q.issuer.name or q.issuer.data_id}, turn-in @ {q.target.name or q.target.data_id}.
  // The {q.stage} grind between them is already plugin-driven (not a quest path).
  // NOT loader-ready: QuestPathLoader currently only consumes per-job Relic-stage
  // paths. This is a reference/authoring artifact -- see stage_gap_checklist.md.
  "QuestSequence": [
    {{
      "Sequence": 0,
      "Steps": [
{step("AcceptQuest")}
      ]
    }},
    // TODO: the {q.stage} grind parks the quest at a final sequence (commonly 255)
    // until the stage's objective is met -- the plugin already drives that part.
    {{
      "Sequence": 255,
      "Steps": [
{step("CompleteQuest")}
      ]
    }}
  ]
}}
"""


def render_stage_checklist(derived: Derived) -> str:
    lines = [
        "# Zodiac stage-intro quest gap checklist",
        "",
        "Generated by `tools/gen_quest_tables.py`. The one-time stage-opening quests",
        "(`ZodiacQuestRegistry`). XIVAPI fully covers the derivable path data — the",
        "accept + turn-in NPCs and their positions — because each stage's grind (12 Atma,",
        "9 Braves books, 75 materia, Light farm, 12 Mahatma, ...) is already driven by the",
        "plugin, not by a quest path. So there is little to fill here beyond verifying the",
        "bookend positions in-game.",
        "",
        "**Loader note:** the current `QuestPathLoader` only consumes per-job Relic-stage",
        "paths (it resolves a job from the weapon in the filename). These stage skeletons",
        "are reference/authoring artifacts until the loader is extended for other stages.",
        "",
        "| Stage | Role | Quest | Issuer | Turn-in | Issuer pos | Turn-in pos |",
        "|---|---|---|---|---|---|---|",
    ]
    for q in derived.quests:
        ip = "yes" if q.issuer.x is not None else "**MISSING**"
        tp = "yes" if q.target.x is not None else "**MISSING**"
        lines.append(
            f"| {q.stage} | {q.role} | {q.name} | {q.issuer.name or q.issuer.data_id} | "
            f"{q.target.name or q.target.data_id} | {ip} | {tp} |")
    if derived.warnings:
        lines += ["", "## Warnings from generation", ""]
        lines += [f"- {w}" for w in derived.warnings]
    lines.append("")
    return "\n".join(lines)


# ------------------------------------------------------------------ #
# Per-job live capture worksheet (for the quest you are ON right now)
# ------------------------------------------------------------------ #

def _parse_job_block(weapon: str) -> Optional[dict]:
    """Pull the per-job specifics (stronghold, exact beastman names, class weapon,
    meld materia) for a weapon from BaseRelicData.ByJob, so the worksheet embeds
    the right targets. Regex over the C#; None if the shape drifts."""
    text = _read_cs(BASE_RELIC_DATA_CS_REL)
    if not text:
        return None
    for chunk in text.split("Job = RelicJob.")[1:]:
        wm = re.search(r'RelicWeaponName\s*=\s*"([^"]+)"', chunk)
        if not wm or wm.group(1).lower() != weapon.lower():
            continue

        def g(pat):
            m = re.search(pat, chunk)
            return m.group(1) if m else None

        jm = re.match(r"(\w+)", chunk)
        return {
            "job": jm.group(1) if jm else "?",
            "weapon": wm.group(1),
            "class_weapon": g(r'ClassWeaponName\s*=\s*"([^"]+)"'),
            "broken_weapon": g(r'BrokenWeapon\s*=\s*new MapStop\("([^"]+)"'),
            "materia": g(r'Meld\("([^"]+)"\)'),
            "beastmen": re.findall(r'new BeastmanTarget\("([^"]+)"', chunk),
        }
    return None


def render_capture_worksheet(weapon: str, masked_id: Optional[int],
                             global_parts: list, job: Optional[dict]) -> str:
    jobname = job["job"] if job else "?"
    lines = [
        f"# A Relic Reborn ({weapon}) — live capture worksheet ({jobname})",
        "",
        "You're on this quest now. The per-sequence trial gauntlet is the one thing",
        "XIVAPI can't give (`Quest.TodoParams` is empty), so **this playthrough is where",
        "you capture it**. At each step, run `/relic questwork` and read the live quest",
        "sequence (and, optionally, the six work bytes it prints for the active quest).",
        "",
        "## Priority — the only GlobalParts sequences still uncalibrated",
        "",
        "These are **shared by all 10 jobs**, so capturing them once on this "
        f"{weapon} run closes them for every future weapon:",
        "",
    ]
    uncal = [p for p in global_parts if p.completed_at_sequence == 0]
    if uncal:
        for p in uncal:
            lines.append(f"- **Part {p.part} — {p.name}** → record `CompletedAtSequence`: "
                         f"`____`  (run `/relic questwork` the moment this step reports done)")
    else:
        lines.append("- _none — every GlobalPart already has a CompletedAtSequence._")

    lines += [
        "",
        "## Play-ordered sequence map (expected values from GlobalParts — verify each)",
        "",
        "| Part | Step | Expected seq | Your capture |",
        "|---|---|---|---|",
    ]
    for p in global_parts:
        exp = []
        if p.active_from_sequence:
            exp.append(f"active {p.active_from_sequence}")
        exp.append(f"done {p.completed_at_sequence}" if p.completed_at_sequence else "done ?")
        cap = "**record seq ___**" if p.completed_at_sequence == 0 else f"verify seq {p.completed_at_sequence}"
        lines.append(f"| {p.part} | {p.name} | {', '.join(exp)} | {cap} |")

    if job:
        lines += [
            "",
            f"## {jobname} / {weapon} specifics (the exact targets to hit)",
            "",
            f"- **Stronghold** (broken weapon + the 24-beastman cull, Part 5): {job['broken_weapon']}",
            "- **Beastmen — exact names the quest text gates on** (killing the wrong "
            "look-alike never credits): " + ", ".join(f"`{b}`" for b in job["beastmen"]),
            f"- **Class weapon** (Part 2): {job['class_weapon']}  ·  **meld ×2:** {job['materia']}",
        ]

    skel = f"`tools/generated/questpaths/{masked_id}_A Relic Reborn ({weapon}).json`" if masked_id \
        else "the matching skeleton in `tools/generated/questpaths/`"
    lines += [
        "",
        "## Folding your captures back in",
        "",
        "1. Put each recorded `CompletedAtSequence` (and `ActiveFromSequence` for a gated",
        "   trial) into `Relicable/BaseRelic/BaseRelicData.cs` → `GlobalParts[part]`.",
        "   These are shared, so you only ever do this once.",
        f"2. *(optional, for the full sequence-accurate path)* fill the middle sequences of",
        f"   {skel} from your captures, then move it into `Relicable/Data/questpaths/`.",
        "3. Bump the csproj version (all 3 fields), then rerun",
        "   `python tools/validate_quest_tables.py --check`.",
        "",
        "_Tip: `/relic questwork` also prints the active quest's six work bytes. Jot the",
        "`high`/`low` nibble values at each step if you want to author",
        "`CompletionQuestVariablesFlags` for sub-sequence precision (optional)._",
        "",
    ]
    return "\n".join(lines)
