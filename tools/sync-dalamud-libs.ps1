<#
.SYNOPSIS
    Mirror the Dalamud reference assemblies XIVLauncher actually installed into
    addon\Hooks\dev, which is where both build resolvers look.

.DESCRIPTION
    XIVLauncher installs Dalamud into a VERSION-NAMED folder -- Hooks\15.0.3.0 -- and can
    leave Hooks\dev empty. That has been observed on both the release track and a beta
    branch, so do not assume a populated dev just because you are on stable; check.

    It matters because both of this repository's resolvers are pinned to dev:

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

    Re-run after every Dalamud update. It is idempotent: if dev already holds the build being
    offered, it does nothing rather than recopying.

.PARAMETER Source
    Explicit source folder. Defaults to the most recently written Hooks\<version> folder that
    actually contains Dalamud.dll.

.PARAMETER Force
    Copy even when dev already holds the same build.

.EXAMPLE
    pwsh -File tools/sync-dalamud-libs.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Source,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$hooks = Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks'
$dev   = Join-Path $hooks 'dev'

if (-not (Test-Path $hooks)) {
    throw "No Hooks folder at $hooks. Run the game once through XIVLauncher with Dalamud enabled."
}

# Pick the newest version-named folder that really holds a build. "Newest by LastWriteTime"
# rather than by parsing the version out of the name: the names can carry git describe
# suffixes (15.0.2.3-76-g8323a3386) that do not sort as versions, and the folder XIVLauncher
# wrote most recently is the one it is actually loading.
if (-not $Source) {
    $candidate = Get-ChildItem $hooks -Directory |
        Where-Object { $_.Name -ne 'dev' -and (Test-Path (Join-Path $_.FullName 'Dalamud.dll')) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        if (Test-Path (Join-Path $dev 'Dalamud.dll')) {
            Write-Host 'Hooks\dev is populated and no version-named build exists - nothing to do.'
            return
        }
        throw "No populated Dalamud build found under $hooks. Launch the game once through XIVLauncher."
    }
    $Source = $candidate.FullName
}

$srcDll = Join-Path $Source 'Dalamud.dll'
if (-not (Test-Path $srcDll)) {
    throw "$Source does not contain Dalamud.dll."
}
$devDll = Join-Path $dev 'Dalamud.dll'

# Identity is (FileVersion, LastWriteTime), not FileVersion alone: a staging build and the
# stable build it came from report the SAME FileVersion (15.0.2.3 vs 15.0.2.3-76-g8323a3386
# both read 15.0.2.3), so comparing versions alone would call them identical. Copy-Item
# preserves file timestamps, so a synced dev matches its source exactly on both.
function Get-BuildId([string] $dll) {
    if (-not (Test-Path $dll)) { return $null }
    $f = Get-Item $dll
    return "$($f.VersionInfo.FileVersion)@$($f.LastWriteTimeUtc.Ticks)"
}

$srcId = Get-BuildId $srcDll
$devId = Get-BuildId $devDll

Write-Host "Source : $Source  (Dalamud $((Get-Item $srcDll).VersionInfo.FileVersion))"
Write-Host "Target : $dev  ($(if ($devId) { "Dalamud $((Get-Item $devDll).VersionInfo.FileVersion)" } else { 'empty' }))"

# Guard against the inverse mistake: XIVLauncher freshly populating dev while an OLDER
# version-named folder lingers on disk, where a blind copy would overwrite the current build
# with a stale one.
$upToDate = $srcId -and $devId -and ($srcId -eq $devId)
if ($upToDate -and -not $Force) {
    Write-Host 'Already in sync - nothing to copy. (Use -Force to copy anyway.)'
}
elseif ($PSCmdlet.ShouldProcess($dev, "mirror $(Split-Path $Source -Leaf)")) {
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
