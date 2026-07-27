# Relicable

A data-driven automation engine for the **A Realm Reborn Zodiac relic weapon line**, built as a
[Dalamud](https://github.com/goatcorp/Dalamud) plugin for FINAL FANTASY XIV.

Relicable works the relic line the way you would: it reads your actual progress out of game
memory, picks the next objective, and drives the companion plugins you already run — vnavmesh
for movement, BossMod Reborn or Rotation Solver Reborn for combat, AutoDuty for instances,
Lifestream for city travel, TextAdvance for dialogue — through each step. Nothing about your
progress is cached; it is re-derived every time it starts, so a stale state file can never make
it do the wrong thing.

---

> ## ⚠️ Read this before anything else
>
> **Using this plugin violates the FINAL FANTASY XIV User Agreement.**
>
> Square Enix prohibits third-party tools that automate gameplay. Relicable automates movement,
> combat, FATE participation, duty queuing, NPC interaction and vendor purchases.
>
> This is unofficial software, not affiliated with or endorsed by Square Enix. You are solely
> responsible for what you run and what happens to your account. The authors accept no
> liability — see [LICENSE](LICENSE).

---

## Early Alpha

**Relicable is in Early Alpha and requires an access code to run.** Without one the plugin
loads, shows the access window, and does nothing else.

Codes are issued individually by the developer. Each carries the name it was issued to and an
expiry date, and **the name it was issued to is shown in the plugin window while it is in
use** — so a code passed on to someone else displays the original owner's name on their screen.
Codes are not transferable and are revoked when they are found being shared.

To request access, contact the developer. Please do not open a GitHub issue asking for a code,
and **never paste a code into an issue, a log, or a screenshot** — it is a bearer token.

Alpha means alpha. Expect steps that stall, objectives that need a nudge, and behaviour that
changes between builds. Bug reports with a debug log attached are genuinely useful; see
[Reporting bugs](#reporting-bugs).

## What it covers

| Stage | What Relicable does |
| --- | --- |
| **A Relic Reborn** (base) | Drives the quest line step by step from a sequence-accurate quest path. The one step it cannot automate — buying/crafting the class weapon and melding two Grade III materia onto it — is surfaced as an annotated task with market-board search links and an Artisan crafting list. |
| **Zenith** | Reports the item gate (3× Thavnairian Mist at the Furnace) and the per-weapon trade costs. |
| **Atma** | Farms FATEs across the twelve atma zones, one zone at a time, then runs the Jalzahn enhancement. Can optionally delegate the farm to Croizat's Bundle of Tweaks Fate Tool Kit. |
| **Animus** (Trials of the Braves) | Buys each book, then works its enemy, dungeon, FATE and leve entries — including FATEs that must be started by talking to an NPC, and FATEs gated behind a predecessor FATE. |
| **Novus** | A cheapest-route materia optimizer over the Sphere Scroll rules, priced live from Universalis, sourcing materia from your bags and retainers. Includes an Alexandrite treasure-map farm. |
| **Nexus** | Reads the real 0/2000 Light gauge off the equipped relic and farms a configurable duty until it fills. |
| **Braves** (iLvl 125) | Plans the material quests and runs their dungeons. |
| **Zeta** | Reads the 12-Mahatma tracker off the equipped weapon and runs the farm-and-attach loop. |

## Installing

### From the plugin repository (recommended)

1. In game, run `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add:

   ```
   https://raw.githubusercontent.com/OmegaJackie/Relicable/main/repo.json
   ```

3. **Save**, then open `/xlplugins` and install **Relicable**.
4. Run `/relic` and enter your Early Alpha access code.

Relicable is marked testing-exclusive, so you may need **Get plugin testing builds** enabled in
`/xlsettings` → Experimental.

### From source

See [BUILDING.md](BUILDING.md). You will need Windows, the .NET 10 SDK, and a working Dalamud
development setup.

## Companion plugins

Relicable orchestrates other plugins rather than reimplementing them. Install them from their
own repositories — `/relic config` → **Dependencies** shows live status and has copy-repo
buttons for each.

**Required:**

- **vnavmesh** — all movement and pathfinding.
- **A combat backend**, one of:
  - **BossMod Reborn** (the default — no other combat plugin needed),
  - **Rotation Solver Reborn**, or
  - **[Wrath Combo](https://github.com/PunishXIV/WrathCombo)**.

  Wrath Combo is lease-based: Relicable registers for control while it runs and hands it back when
  it unloads, and Wrath's own window marks which settings Relicable is driving. By default Relicable
  clears Wrath's in-combat gating so the rotation will open on a *neutral* relic-note enemy — you can
  turn that off in `/relic config` to keep your Wrath setup untouched.

**Strongly recommended:**

- **TextAdvance** — dialogue and quest turn-ins. Enable it globally.
- **AutoDuty** — everything that happens inside an instance.
- **Lifestream** — city and aethernet travel.

**Optional:**

- **AutoRetainer** — lets the Novus planner see materia held on your retainers.
- **Artisan** — builds a crafting list for the base relic's class-weapon step.
- **Croizat's Bundle of Tweaks** — an alternative Atma FATE-farm backend.

## Using it

| Command | Opens |
| --- | --- |
| `/relic` | Main window — progress, stage, start/stop |
| `/relic config` | Settings and dependency status |
| `/relic novus` | Novus materia planner |
| `/relic braves` | Braves material-quest planner |
| `/relic questmap` | A Relic Reborn quest map |

Press **Start** and it works the lowest incomplete stage. To revisit a farmable stage you have
already passed, switch stage selection to **Manual** in the main window.

## Reporting bugs

Open an issue using the bug report template. It asks for the things that actually determine
whether a report is actionable:

- Relicable and Dalamud versions
- Your job, and the stage and objective it was on
- Which combat backend you are using
- **The `/xllog` output with the debug log turned on** — enable it in `/relic config` →
  Diagnostics, reproduce the problem, then attach the log

Reports without a log are usually not actionable, because most failures are a specific step
disagreeing with live game state and the log is the only record of what it saw.

**Do not paste your access code into an issue.**

## Building and contributing

[BUILDING.md](BUILDING.md) covers the toolchain. Notes for contributors:

- `Relicable/` — the plugin. `Steps/` holds one executor per step type; `Data/` holds the
  objective JSON and generated tables; `External/` holds the companion-plugin IPC wrappers.
- `tools/` — Python generators that derive the leve and quest tables from XIVAPI, plus their
  validators. Regenerate rather than hand-editing `Data/*.Generated.cs`.
- ECommons is a separate clone, not a submodule — see BUILDING.md. It is gitignored.
- The plugin version in `Relicable.csproj` is bumped every change (all three fields), so a dev
  install visibly reloads.

## License

[AGPL-3.0](LICENSE). If you run a modified version as a network service, you must publish your
source.
