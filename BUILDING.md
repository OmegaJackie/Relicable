# Building Relicable

Relicable is a Dalamud plugin. It builds against the Dalamud reference assemblies, so a working
Dalamud development environment has to be in place before `dotnet build` will do anything useful.

## Prerequisites

- **Windows.** Dalamud is Windows-only and the target framework is `net10.0-windows`; this
  cannot be built on Linux or macOS.
- **XIVLauncher** ([FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher)), with
  the game launched through it at least once with Dalamud enabled.
- **.NET 10 SDK (x64).** Dalamud API 15 targets `net10.0-windows`; API 14 was the move to
  .NET 10.
- An editor: Visual Studio 2022 with the *.NET desktop development* workload, JetBrains Rider,
  or VS Code with the C# Dev Kit.

## 1. Clone the repository and ECommons

Relicable references [ECommons](https://github.com/NightmareXIV/ECommons) as a project, not a
NuGet package and not a submodule. Clone ECommons **inside** the repository root:

```bash
git clone https://github.com/OmegaJackie/RelicAssist
cd RelicAssist
git clone https://github.com/NightmareXIV/ECommons
```

The result must look exactly like this. `Relicable.csproj` has a `ProjectReference` to
`..\ECommons\ECommons\ECommons.csproj`, which resolves relative to `RelicAssist\Relicable\`, so
ECommons has to sit at the repository root — not beside it:

```
RelicAssist/            <- this repository (the repo root)
  Relicable/            <- the plugin project
    Relicable.csproj
  tools/
  ECommons/             <- clone it HERE
    ECommons/
      ECommons.csproj
```

ECommons is listed in `.gitignore` on purpose, so the clone will not show up as an untracked
change.

## 2. Get the Dalamud dev libraries

Run FFXIV through XIVLauncher with Dalamud enabled once. That populates:

```
%AppData%\XIVLauncher\addon\Hooks\dev\
```

with `Dalamud.dll`, `ImGui.NET.dll`, `Lumina.dll`, `Lumina.Excel.dll`, `FFXIVClientStructs.dll`
and friends. `Dalamud.NET.Sdk` finds that path automatically. To use a different location, set
the `DALAMUD_HOME` environment variable to the folder containing `Dalamud.dll`.

## 3. Build

```bash
dotnet build Relicable/Relicable.csproj -c Release
```

The first build restores `Dalamud.NET.Sdk/15.0.0` from NuGet, so it needs internet access. Output
lands under `Relicable/bin/Release/` as `Relicable.dll`, with `Relicable.json` and the `Data/`
JSON files copied alongside. The SDK also writes a packaged plugin zip to
`Relicable/bin/Release/Relicable/latest.zip`.

**Match the SDK to your Dalamud.** If your installed Dalamud's API level differs from 15, change
`Dalamud.NET.Sdk/15.0.0` at the top of `Relicable.csproj` to the matching version and rebuild —
otherwise some FFXIVClientStructs or Lumina members will not line up.

## 4. Set up the Early Alpha signing key

A build from a fresh clone has **no signing key compiled in**, so no access code will validate
against it. If you are building for yourself, generate your own keypair once:

```bash
dotnet run --project tools/RelicableKeygen -- init
dotnet run --project tools/RelicableKeygen -- mint --owner "Your Name" --days 365
```

`init` writes the private key to `keys/` (gitignored) and patches the matching public key into
`Relicable/Licensing/AlphaCode.cs`. Rebuild, then paste the minted code into the plugin's access
window. See [tools/RelicableKeygen/README.md](tools/RelicableKeygen/README.md) for the full
scheme.

Do not commit the patched public key back if you are contributing a pull request — it would
invalidate the codes issued from the official key.

## 5. Load it in-game as a dev plugin

1. In game, run `/xlsettings` and open the **Experimental** tab.
2. Under **Dev Plugin Locations**, add the full path to the folder containing the built
   `Relicable.dll`, then **Save**.
3. Open `/xlplugins`, find Relicable under installed/dev plugins, and enable it.
4. Run `/relic` to open the window (`/relic config` for settings).

After a rebuild, reload the dev plugin by toggling it off and on in the installer.

## 6. Install the companion plugins

The IPC integrations need these. `/relic config` → **Dependencies** shows live status and has
copy-repo buttons. Paste a repo URL into `/xlsettings` → Experimental → *Custom Plugin
Repositories*, **Save**, then install from `/xlplugins`.

| Plugin | Repository |
| --- | --- |
| vnavmesh | `https://puni.sh/api/repository/veyn` |
| Rotation Solver Reborn | `https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json` |
| BossMod Reborn | *(same CombatReborn repo as above)* |
| Wrath Combo | `https://love.puni.sh/ment.json` |
| Lifestream | `https://love.puni.sh/ment.json` |
| TextAdvance | `https://love.puni.sh/ment.json` — enable it globally |
| AutoDuty | `https://love.puni.sh/ment.json` |

## Regenerating the data tables

`Relicable/Data/*.Generated.cs` and the leve/quest tables are derived from XIVAPI by the Python
scripts in `tools/`, not hand-written. To regenerate:

```bash
pip install -r tools/requirements.txt
python tools/gen_leve_tables.py
python tools/gen_quest_tables.py
python tools/validate_leve_tables.py
python tools/validate_quest_tables.py
```

Run the validators after a game patch — they catch territory IDs that moved and names that
drifted, which otherwise fail silently at runtime by skipping a slot.

## Troubleshooting

**"Type or namespace not found" for Dalamud / ImGui / Lumina / FFXIVClientStructs**
The dev libraries are not where the SDK expects. Confirm step 2, or set `DALAMUD_HOME`.

**`NETSDK1045: ... does not support targeting .NET 10`**
Install the .NET 10 SDK.

**Single-symbol errors (`CS1061`, `CS0117`) on one member**
A version-sensitive API name differs in your installed Dalamud / Lumina / FFXIVClientStructs.
These are usually one-line fixes.

**`error MSB4025: An XML comment cannot contain '--'`**
Something added a double hyphen inside an XML comment in a `.csproj`. Use an en dash or reword.

**The plugin loads but every command opens the access window**
The build has no valid signing key compiled in, or your code does not match it. See step 4.

**Access code rejected after a rebuild**
You regenerated the keypair. Every previously minted code is now invalid — mint a new one.
