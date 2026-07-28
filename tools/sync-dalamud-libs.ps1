<#
.SYNOPSIS
    Mirror the Dalamud reference assemblies XIVLauncher actually installed into
    addon\Hooks\dev, which is where both build resolvers look.

.DESCRIPTION
    XIVLauncher only extracts into Hooks\dev while you are on the RELEASE track. Opt into a
    beta/staging branch (launcherConfigV3.json -> DalamudBetaKind) and it extracts into a
    version-named folder instead -- Hooks\15.0.2.3-76-g8323a3386 -- and leaves Hooks\dev
    EMPTY. That matters right after a game patch, which is exactly when everyone switches to
    staging, because both of this repository's resolvers are pinned to dev:

      * Dalamud.NET.Sdk (Relicable.csproj) defaults to %AppData%\XIVLauncher\addon\Hooks\dev,
        overridable with the DALAMUD_HOME environment variable.
      * ECommons.csproj HARDCODES <DalamudLibPath>$(appdata)\xivlauncher\Addon\Hooks\dev\</...>
        with no condition, and only consults DALAMUD_HOME inside a Linux-only PropertyGroup.
        On Windows it ignores DALAMUD_HOME completely, so setting that variable fixes the
        plugin project and still leaves ECommons resolving into an empty folder.

    Which is why this copies rather than repointing: dev is the one path both agree on. The
    failure it prevents is ~1900 CS0246s naming ECommons files (Window, AtkUnitBase, ImGuiCol,
    IDalamudTextureWrap ...) before Relicable is even reached -- errors that look like an
    ECommons problem and are really an empty folder.

    Re-run this after every Dalamud update while you are on a beta branch. It is idempotent.

.PARAMETER Source
    Explicit source folder. Defaults to the most recently written Hooks\<version> folder that
    actually contains Dalamud.dll.

.PARAMETER WhatIf
    Report what would be copied without touching anything.

.EXAMPLE
    pwsh -File tools/sync-dalamud-libs.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Source
)

$ErrorActionPreference = 'Stop'

$hooks = Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks'
$dev   = Join-Path $hooks 'dev'

if (-not (Test-Path $hooks)) {
    throw "No Hooks folder at $hooks. Run the game once through XIVLauncher with Dalamud enabled."
}

# Pick the newest version-named folder that really holds a build. "Newest by LastWriteTime"
# rather than by parsing the version out of the name: the names carry git describe suffixes
# (15.0.2.3-76-g8323a3386) that do not sort as versions, and the folder XIVLauncher wrote
# most recently is the one it is actually loading.
if (-not $Source) {
    $candidate = Get-ChildItem $hooks -Directory |
        Where-Object { $_.Name -ne 'dev' -and (Test-Path (Join-Path $_.FullName 'Dalamud.dll')) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        if (Test-Path (Join-Path $dev 'Dalamud.dll')) {
            Write-Host "Hooks\dev is already populated and no version-named build exists - nothing to do."
            Write-Host "(That is the normal state on the release track.)"
            return
        }
        throw "No populated Dalamud build found under $hooks. Launch the game once through XIVLauncher."
    }
    $Source = $candidate.FullName
}

if (-not (Test-Path (Join-Path $Source 'Dalamud.dll'))) {
    throw "$Source does not contain Dalamud.dll."
}

$srcVer = (Get-Item (Join-Path $Source 'Dalamud.dll')).VersionInfo.FileVersion
$devVer = if (Test-Path (Join-Path $dev 'Dalamud.dll')) {
    (Get-Item (Join-Path $dev 'Dalamud.dll')).VersionInfo.FileVersion
} else { '<empty>' }

Write-Host "Source : $Source  (Dalamud $srcVer)"
Write-Host "Target : $dev  (Dalamud $devVer)"

if ($PSCmdlet.ShouldProcess($dev, "mirror $(Split-Path $Source -Leaf)")) {
    New-Item -ItemType Directory -Force -Path $dev | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $dev -Recurse -Force
    Write-Host "Copied $((Get-ChildItem $Source -File).Count) files."
}

# The exact set ECommons references by HintPath. A distrib reshuffle that drops one of these
# otherwise resurfaces as a wall of CS0246 deep inside ECommons, so name the missing file here.
$required = @(
    'Dalamud.dll', 'Dalamud.Common.dll', 'Dalamud.Bindings.ImGui.dll',
    'Dalamud.Bindings.ImPlot.dll', 'Dalamud.Bindings.ImGuizmo.dll',
    'Lumina.dll', 'Lumina.Excel.dll', 'FFXIVClientStructs.dll',
    'InteropGenerator.Runtime.dll', 'Newtonsoft.Json.dll', 'Mono.Cecil.dll',
    'Reloaded.Hooks.Definitions.dll', 'Serilog.dll', 'TerraFX.Interop.Windows.dll'
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $dev $_)) }
if ($missing) {
    throw "Missing from ${dev}: $($missing -join ', ')"
}

Write-Host ''
Write-Host 'Reference assemblies now in Hooks\dev:'
foreach ($n in @('Dalamud.dll', 'FFXIVClientStructs.dll', 'Lumina.dll', 'Lumina.Excel.dll')) {
    $f = Get-Item (Join-Path $dev $n)
    '{0,-26} {1}' -f $f.Name, $f.VersionInfo.FileVersion | Write-Host
}
