using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;

namespace Relicable.External;

// The list of BossMod Reborn autorotation preset names, for the preset dropdowns in the config
// window. UI-only: nothing in the run path reads this.
//
// WHY IT SCRAPES FILES INSTEAD OF ASKING OVER IPC:
//   BMR's preset IPC is Get / Create / Delete / SetActive / ClearActive / GetActive /
//   Get+SetForceDisabled plus five transient-strategy gates (verified against the installed
//   7.5.5.9 assembly). NONE of them enumerates preset names -- Presets.Get can only confirm a
//   name you already know. So the only way to offer a chooser is to read the same files BMR
//   itself loads, which is exactly what SetActive resolves against
//   (PresetDatabase.FindPresetByName over DefaultPresets + UserPresets).
//
// TWO SOURCES, because BMR merges two:
//   * User presets   <pluginConfigs>/<internal>/autorot/presets.db.json
//   * Default presets <XIVLauncher>/installedPlugins/<internal>/<version>/*Presets*.json
//     (7.5.5.9 ships DefaultRotationPresets.json: VBM Default / VBM AI / VBM Multibox; newer
//     builds add RebornPresets.json). BMR hides these from its own chooser when
//     AutorotationConfig.HideDefaultPresets is set -- except the two AI/movement ones, which it
//     always shows -- and SetActive resolves them either way, so mirror that rule rather than
//     inventing one.
//
// Everything here is best-effort by design. Another plugin's on-disk format is not a contract:
// BMR rewrites presets.db.json in place (FileMode.Create), so a read can catch a truncated file,
// and the version envelope has changed across forks. Any IO or JSON failure yields an empty list,
// and the config window falls back to the plain text box it used before -- a wrong dropdown would
// be worse than no dropdown.
internal sealed class BossModPresetCatalog
{
    // BMR first, the older fork second: both can be installed side by side with different
    // databases, and the rest of Relicable targets Reborn (DependencyRegistry passes only
    // "BossModReborn").
    private static readonly string[] InternalNames = { "BossModReborn", "BossMod" };

    // The dropdown is redrawn every frame while the window is open; re-reading two small JSON
    // files at 4Hz would be silly. Long enough to be cheap, short enough that a preset made in
    // BMR shows up without hunting for the Refresh button.
    private const long TtlMs = 5000;

    private readonly IDalamudPluginInterface _pi;
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private long _scannedAt;
    private bool _scanned;

    public BossModPresetCatalog(IDalamudPluginInterface pi) => _pi = pi;

    // Preset names, user presets first (alphabetical), then the visible defaults.
    public IReadOnlyList<string> Names => _names;

    // True when we actually found presets to offer. False means "draw the text box instead":
    // BMR absent, a dev install with an unexpected layout, or an unreadable file.
    public bool Available => _names.Count > 0;

    // Re-scan if the cache has aged out. Cheap enough to call once per frame from Draw.
    public void EnsureFresh()
    {
        if (_scanned && Environment.TickCount64 - _scannedAt < TtlMs)
            return;
        Refresh();
    }

    public void Refresh()
    {
        _scanned = true;
        _scannedAt = Environment.TickCount64;
        try
        {
            _names = Scan();
        }
        catch (Exception ex)
        {
            // Never let a sibling plugin's file shape take down the config window.
            Diagnostics.DebugLog.Verbose($"BossMod preset catalog: scan failed ({ex.Message}); using the text box.");
            _names = Array.Empty<string>();
        }
    }

    private IReadOnlyList<string> Scan()
    {
        // ConfigFile is <pluginConfigs>/Relicable.json. Deliberately NOT ConfigDirectory: that
        // one CREATES <pluginConfigs>/Relicable/ as a side effect, and Relicable has no use for it.
        var configRoot = Path.GetDirectoryName(_pi.ConfigFile.FullName);
        if (string.IsNullOrEmpty(configRoot))
            return Array.Empty<string>();

        var installed = _pi.InstalledPlugins
            .Where(p => InternalNames.Contains(p.InternalName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.IsLoaded)
            .ThenBy(p => Array.IndexOf(InternalNames, p.InternalName))
            .FirstOrDefault();
        var internalName = installed?.InternalName
            ?? InternalNames.FirstOrDefault(n => Directory.Exists(Path.Combine(configRoot, n)));
        if (string.IsNullOrEmpty(internalName))
            return Array.Empty<string>();

        return Collect(configRoot, internalName, installed?.Version?.ToString());
    }

    // Everything after "which BMR install, and where" -- split out so it depends only on paths.
    internal static IReadOnlyList<string> Collect(string configRoot, string internalName, string? version)
    {
        var user = ReadPresetNames(Path.Combine(configRoot, internalName, "autorot", "presets.db.json"));

        var defaults = ReadDefaultPresetNames(configRoot, internalName, version);
        if (HideDefaultPresets(Path.Combine(configRoot, internalName + ".json")))
            // BMR's own visibility rule: hidden defaults still list the movement/AI ones, because
            // they are the only defaults anyone selects deliberately.
            defaults = defaults
                .Where(n => n.Equals("VBM Multibox", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("Movement Only", StringComparison.OrdinalIgnoreCase))
                .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var n in user.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Concat(defaults))
            if (!string.IsNullOrWhiteSpace(n) && seen.Add(n))
                names.Add(n);
        return names;
    }

    // Presets ship as either a bare array or a {version, payload} envelope, and the envelope
    // version has changed across forks (v0/v1/v2/v7 files all exist on disk). The one thing that
    // has been stable is the {Name, Modules} element, so key off that and ignore the version.
    private static List<string> ReadPresetNames(string path)
    {
        var names = new List<string>();
        if (!File.Exists(path))
            return names;
        try
        {
            // FileShare.ReadWrite: BMR truncates and rewrites this file in place, so an exclusive
            // open can fail outright while it saves.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var payload))
                root = payload;
            if (root.ValueKind != JsonValueKind.Array)
                return names;
            foreach (var entry in root.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("Name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } s)
                    names.Add(s);
        }
        catch (Exception ex)
        {
            // A torn read mid-save is expected, not exceptional. Next EnsureFresh picks it up.
            Diagnostics.DebugLog.Verbose($"BossMod preset catalog: could not read '{path}': {ex.Message}");
        }
        return names;
    }

    // installedPlugins sits NEXT TO pluginConfigs under the XIVLauncher root, and each plugin
    // keeps one folder per installed version. Prefer the version Dalamud reports; otherwise take
    // the newest folder. Glob for *Presets*.json rather than naming files: 7.5.5.9 ships only
    // DefaultRotationPresets.json, later builds add RebornPresets.json.
    private static List<string> ReadDefaultPresetNames(string configRoot, string internalName, string? version)
    {
        var names = new List<string>();
        try
        {
            var launcherRoot = Path.GetDirectoryName(configRoot);
            if (string.IsNullOrEmpty(launcherRoot))
                return names;
            var pluginRoot = Path.Combine(launcherRoot, "installedPlugins", internalName);
            if (!Directory.Exists(pluginRoot))
                return names;

            var versionDir = !string.IsNullOrEmpty(version) && Directory.Exists(Path.Combine(pluginRoot, version))
                ? Path.Combine(pluginRoot, version)
                : Directory.GetDirectories(pluginRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).LastOrDefault();
            if (versionDir == null)
                return names;

            foreach (var file in Directory.GetFiles(versionDir, "*Presets*.json"))
                names.AddRange(ReadPresetNames(file));
        }
        catch (Exception ex)
        {
            // A dev install, a relocated launcher, a locked folder -- none of them are worth an
            // empty dropdown, so just fall through with whatever the user presets gave us.
            Diagnostics.DebugLog.Verbose($"BossMod preset catalog: no default presets for {internalName}: {ex.Message}");
        }
        return names;
    }

    // <pluginConfigs>/<internal>.json -> Payload["BossMod.Autorotation.AutorotationConfig"]
    //                                           ["HideDefaultPresets"]. Absent means false,
    // which is BMR's own default.
    private static bool HideDefaultPresets(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Payload", out var payload)
                && payload.TryGetProperty("BossMod.Autorotation.AutorotationConfig", out var cfg)
                && cfg.TryGetProperty("HideDefaultPresets", out var hide))
                return hide.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Verbose($"BossMod preset catalog: could not read '{path}': {ex.Message}");
        }
        return false;
    }
}
