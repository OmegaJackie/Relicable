using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace Relicable.External;

// One dependency's resolved runtime status, consumed by the Dependencies tab.
public sealed record DependencyStatus(
    string Name,
    bool Required,
    bool Installed,
    bool Loaded,
    bool GateLive,
    string Version,
    string GitHubUrl,
    string RepoUrl);

// Resolves the status of every companion plugin. "Installed/Loaded" come from
// Dalamud's InstalledPlugins list; "GateLive" comes from each wrapper's Available
// flag (the key IPC gate's HasFunction/HasAction), so the tab can distinguish "not
// installed" from "installed but not exposing its IPC yet". Repo URLs are the
// official GitHub pages and the Dalamud custom-repository links taken from each
// project's README.
public sealed class DependencyRegistry
{
    private readonly IDalamudPluginInterface _pi;
    private readonly Configuration _config;
    private readonly NavmeshIpc _nav;
    private readonly RotationSolverIpc _rsr;
    private readonly LifestreamIpc _lifestream;
    private readonly TextAdvanceIpc _textAdvance;
    private readonly AutoDutyIpc _autoDuty;
    private readonly BossModRebornIpc _bossModReborn;
    private readonly WrathComboCombatBackend _wrathCombo;
    private readonly AutoRetainerIpc _autoRetainer;

    // Dalamud custom-repository JSON links (paste into /xlsettings > Experimental).
    private const string PunishRepo = "https://love.puni.sh/ment.json";
    private const string VnavRepo = "https://puni.sh/api/repository/veyn";
    private const string CombatRebornRepo =
        "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json";

    public DependencyRegistry(
        IDalamudPluginInterface pi,
        Configuration config,
        NavmeshIpc nav,
        RotationSolverIpc rsr,
        LifestreamIpc lifestream,
        TextAdvanceIpc textAdvance,
        AutoDutyIpc autoDuty,
        BossModRebornIpc bossModReborn,
        WrathComboCombatBackend wrathCombo,
        AutoRetainerIpc autoRetainer)
    {
        _pi = pi;
        _config = config;
        _nav = nav;
        _rsr = rsr;
        _lifestream = lifestream;
        _textAdvance = textAdvance;
        _autoDuty = autoDuty;
        _bossModReborn = bossModReborn;
        _wrathCombo = wrathCombo;
        _autoRetainer = autoRetainer;
    }

    // Required dependencies that are neither IPC-live nor even loaded. A loaded
    // plugin is treated as usable (e.g. RSR drives combat via its chat command even
    // when its typed IPC gate cannot be bound), so only a truly absent plugin
    // blocks starting.
    public IReadOnlyList<string> MissingRequired()
    {
        var missing = new List<string>();
        foreach (var d in Evaluate())
            if (d.Required && !d.GateLive && !d.Loaded)
                missing.Add(d.Name);
        return missing;
    }

    // Evaluate() walks Dalamud's InstalledPlugins list seven times and allocates the
    // status records; the Dependencies tab calls it every frame, so cache the result
    // briefly. Plugin installs/loads change on a human timescale; 1s is plenty fresh.
    private IReadOnlyList<DependencyStatus>? _cached;
    private long _cachedAtTicks;
    private const long CacheTtlMs = 1000;

    public IReadOnlyList<DependencyStatus> Evaluate()
    {
        if (_cached != null && Environment.TickCount64 - _cachedAtTicks < CacheTtlMs)
            return _cached;
        _cached = EvaluateNow();
        _cachedAtTicks = Environment.TickCount64;
        return _cached;
    }

    private IReadOnlyList<DependencyStatus> EvaluateNow()
    {
        return new List<DependencyStatus>
        {
            Build("vnavmesh", _config.EnableNavmesh, _nav.Available,
                "https://github.com/awgil/ffxiv_navmesh", VnavRepo,
                "vnavmesh"),

            Build("Rotation Solver Reborn",
                _config.Backend == Configuration.CombatBackend.RotationSolverReborn, _rsr.Available,
                "https://github.com/FFXIV-CombatReborn/RotationSolverReborn", CombatRebornRepo,
                "RotationSolver", "RotationSolverReborn"),

            Build("Lifestream", _config.EnableLifestream, _lifestream.Available,
                "https://github.com/NightmareXIV/Lifestream", PunishRepo,
                "Lifestream"),

            Build("TextAdvance", _config.EnableTextAdvance, _textAdvance.Available,
                "https://github.com/NightmareXIV/TextAdvance", PunishRepo,
                "TextAdvance"),

            // ffxivcode/AutoDuty is archived; erdelf's fork is the maintained line.
            Build("AutoDuty", _config.EnableAutoDuty, _autoDuty.Available,
                "https://github.com/erdelf/AutoDuty", PunishRepo,
                "AutoDuty"),

            // Required when BossMod Reborn provides avoidance OR is the combat backend (in
            // which case RSR above is no longer required, so it can be uninstalled).
            Build("BossMod Reborn",
                _config.UseBossModRebornAvoidance || _config.Backend == Configuration.CombatBackend.BossModReborn,
                _bossModReborn.Available,
                "https://github.com/FFXIV-CombatReborn/BossmodReborn", CombatRebornRepo,
                "BossModReborn"),

            // Required only when it is the selected combat backend. Wrath is lease-based:
            // Relicable registers for control while it runs and hands it back on unload.
            //
            // GateLive deliberately also requires that a lease is obtainable. Wrath's own
            // IPCReady only reports that its caches are built, and is independent of the
            // IPC toggle that actually decides whether RegisterForLease succeeds -- so
            // reporting on IPCReady alone shows a green row while nothing can drive
            // combat, and the user gets a clean start followed by silent inaction.
            Build("Wrath Combo",
                _config.Backend == Configuration.CombatBackend.WrathCombo,
                _wrathCombo.Available && !_wrathCombo.LeaseRefused,
                "https://github.com/PunishXIV/WrathCombo", PunishRepo,
                "WrathCombo"),

            // Optional: enumerates retainers and can be suppressed while Relicable
            // drives the bell. Required only for the Novus auto-withdraw convenience;
            // the relic line does not need it, so it is not a start blocker.
            Build("AutoRetainer (optional)", required: false, gateLive: _autoRetainer.Available,
                "https://github.com/PunishXIV/AutoRetainer", PunishRepo,
                "AutoRetainer"),
        };
    }

    private DependencyStatus Build(
        string name, bool required, bool gateLive,
        string github, string repo, params string[] internalNames)
    {
        var plugin = _pi.InstalledPlugins
            .FirstOrDefault(p => internalNames.Contains(p.InternalName, StringComparer.OrdinalIgnoreCase));

        return new DependencyStatus(
            Name: name,
            Required: required,
            Installed: plugin != null,
            Loaded: plugin?.IsLoaded ?? false,
            GateLive: gateLive,
            Version: plugin?.Version?.ToString() ?? "-",
            GitHubUrl: github,
            RepoUrl: repo);
    }

}
