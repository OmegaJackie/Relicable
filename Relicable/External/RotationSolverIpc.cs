using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.External.Ipc;

namespace Relicable.External;

// Hardened wrapper over Rotation Solver Reborn. VERIFIED gate and signature
// (AutoDuty's RSR_IPCSubscriber): "RotationSolverReborn.ChangeOperatingMode"
// taking a StateCommandType byte enum.
//
// Command re-firing is the main hazard here: KillTargetExecutor calls EnableAuto
// every tick while a target is engaged. The mode is edge-triggered, so the
// underlying IPC/command fires only when the mode actually changes (Off->Auto or
// Auto->Off), not every frame.
public sealed class RotationSolverIpc : ICombatBackend
{
    // Mirror of RSR's StateCommandType (byte-backed). Values must match RSR.
    public enum StateCommand : byte
    {
        Off = 0,
        Auto = 1,
        TargetOnly = 2,
        Manual = 3,
        AutoDuty = 4,
    }

    // Mirror of RSR's OtherCommandType (byte-backed). Values must match RSR.
    public enum OtherCommand : byte
    {
        Settings = 0,
        Rotations,
        DutyRotations,
        DoActions,
        ToggleActions,
        NextAction,
    }

    private readonly ICallGateSubscriber<StateCommand, object>? _changeOperatingMode;
    private readonly ICallGateSubscriber<OtherCommand, string, object>? _otherCommand;
    // RSR's parameterless "temporarily let RSR grab the nearest target" gates (verified in RSR's
    // IPCProvider). Used to scope TargetFreely to a FATE only, without touching the user's config.
    private readonly ICallGateSubscriber<object>? _enableTargetFreely;
    private readonly ICallGateSubscriber<object>? _disableTargetFreely;
    private readonly ICommandHelper _command;
    private readonly Configuration _config;
    private readonly EdgeTrigger<StateCommand> _mode;
    // Last time the mode edge-trigger actually sent (Environment.TickCount64 ms), for the re-assert
    // heartbeat in DispatchMode. 0 until the first dispatch (so the first "on" request re-sends).
    private long _lastModeSendMs;
    // How often an already-requested "on" mode is force-resent so an RSR self-off is re-armed. Slow on
    // purpose (a fast re-send thrashes RSR so it never settles); ~one GCD-plus so it never sits between
    // a self-off and the mob's aggro long enough to "never return fire". See DispatchMode.
    private const long ModeReassertMs = 2500;
    // Edge-triggered on/off for the FATE TargetFreely override, so the per-tick FATE engage loop
    // (which re-applies the FATE config every frame) sends the gate only when the state changes.
    private readonly EdgeTrigger<bool> _fateFreely;
    // Last value sent per SETTING KEY (the first token), so applying several distinct settings in
    // a row does not make each look "changed" versus a single shared latch and re-send every tick.
    private readonly Dictionary<string, string> _lastSettings = new(StringComparer.OrdinalIgnoreCase);

    public RotationSolverIpc(IDalamudPluginInterface pi, ICommandHelper command, Configuration config)
    {
        _command = command;
        _config = config;
        try
        {
            _changeOperatingMode = pi.GetIpcSubscriber<StateCommand, object>(
                "RotationSolverReborn.ChangeOperatingMode");
        }
        catch
        {
            _changeOperatingMode = null;
        }

        try
        {
            _otherCommand = pi.GetIpcSubscriber<OtherCommand, string, object>(
                "RotationSolverReborn.OtherCommand");
        }
        catch
        {
            _otherCommand = null;
        }

        try
        {
            _enableTargetFreely = pi.GetIpcSubscriber<object>("RotationSolverReborn.EnableTargetFreelyOverride");
        }
        catch
        {
            _enableTargetFreely = null;
        }

        try
        {
            _disableTargetFreely = pi.GetIpcSubscriber<object>("RotationSolverReborn.DisableTargetFreelyOverride");
        }
        catch
        {
            _disableTargetFreely = null;
        }

        _mode = new EdgeTrigger<StateCommand>(Send);
        _fateFreely = new EdgeTrigger<bool>(SendFateFreely);
    }

    // RSR selects and attacks FATE mobs itself (Auto mode + the FATE settings in ConfigureForFate),
    // so the FATE executor hands targeting over instead of hard-targeting each mob per tick.
    public bool OwnsFateTargeting => true;

    // True when the IPC gate is live. If false (or it cannot bind, e.g. RSR's
    // custom enum type), we still function via the /rotation command.
    public bool Available
    {
        get { try { return _changeOperatingMode?.HasAction ?? false; } catch { return false; } }
    }

    public void EnableAuto() => DispatchMode(StateCommand.Auto);

    // For named single-target kills. RSR Manual mode attacks the target we set (the
    // validated RelicNote mob) immediately and bypasses engage gating, so it pulls a
    // neutral mob and does not auto-switch to unrelated enemies. Auto mode combined
    // with a "previously engaged only" hostile type never initiates the pull.
    public void EnableManual() => DispatchMode(StateCommand.Manual);

    public void Disable()
    {
        DispatchMode(StateCommand.Off);
        // Leaving FATE combat: drop the temporary TargetFreely override so it cannot bleed into
        // the neutral relic-note grind (which tunnels a single hard target in Manual mode). Edge-
        // triggered, so the per-tick Disable calls in the grind/FATE loops do not spam the gate.
        _fateFreely.Dispatch(false);
    }

    // Edge-triggered mode dispatch with a slow RE-ASSERT heartbeat for the "on" modes.
    //
    // The hazard: RSR turns ITSELF off (AutoOffAfterCombat, and other self-off behaviour) between
    // kills and in the window after we tell it to engage but BEFORE a pulled / aggroing mob's combat
    // registers. Because the mode is edge-triggered, once RSR self-offs the executors' per-tick
    // EnableManual/EnableAuto is suppressed (the cached mode is unchanged) and RSR is never re-armed on
    // the SAME mob -- so "RSR toggles off faster than the mob aggros, and the player never returns fire".
    // KillTargetExecutor only re-arms once per NEW mob (keyed on the engaged id), which does not cover a
    // self-off on the current target.
    //
    // Fix: while an "on" mode is being (re)requested, force a re-send (via Reset) if it has not actually
    // gone out for ModeReassertMs, so an RSR self-off is corrected within that interval regardless of the
    // combat state. Deliberately SLOW, not per-tick: re-sending the mode every frame is the thrash that
    // stops RSR ever settling into its rotation (KillTargetExecutor's note). It is skipped while the
    // player is mid-hard-cast so a re-send can never clip a cast, and never applies to Off (off is off,
    // and must not be heartbeated back on).
    private void DispatchMode(StateCommand mode)
    {
        var now = Environment.TickCount64;
        var casting = Plugin.ObjectTable.LocalPlayer?.IsCasting == true;
        if (mode != StateCommand.Off && !casting && now - _lastModeSendMs >= ModeReassertMs)
            _mode.Reset();
        if (_mode.Dispatch(mode))
            _lastModeSendMs = now;
    }

    // Force the next EnableAuto/Disable to re-send, for example after RSR may have
    // changed mode on its own (combat ended with AutoOffAfterCombat). Also clears the
    // setting latches: RSR may have reloaded or the user changed a setting mid-session,
    // in which case a "duplicate" ConfigureForFate must actually re-send, and the FATE
    // TargetFreely override must be re-armed by the next ConfigureForFate.
    public void ResyncNextDispatch()
    {
        _mode.Reset();
        _lastSettings.Clear();
        _fateFreely.Reset();
    }

    // Configure RSR to OWN FATE targeting: it auto-detects the active FATE and, in Auto mode
    // (EnableAuto), auto-selects and attacks FATE mobs itself while Relicable only navigates into
    // the ring, level-syncs, and grounds (see OwnsFateTargeting / ParticipateFateExecutor). The
    // settings come from Configuration.Rsr* so the user can tune FATE behaviour; each is applied
    // once per distinct value (deduplicated) to avoid per-tick spam. Mirrors AutoDuty's verified
    // OtherCommand(Settings, "<Key> <Value>") pattern.
    public void ConfigureForFate()
    {
        SetSetting($"HostileType {_config.RsrFateHostileType}");
        SetSetting($"IgnoreNonFateInFate {Bool(_config.RsrFateIgnoreNonFateTargets)}");
        SetSetting($"TargetFatePriority {Bool(_config.RsrFateTargetFatePriority)}");
        // Temporary, FATE-only "grab the nearest target" -- via RSR's override IPC, never the
        // persistent TargetFreely setting, so the grind is unaffected. Cleared cleanly on Disable.
        _fateFreely.Dispatch(_config.RsrFateTargetFreely);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    // Configure RSR for the plugin's open-world LEVE combat: engage only enemies already engaged
    // with us or an ally, NOT every attackable enemy. Battlecraft leves run in the OPEN WORLD, so
    // unrelated NEUTRAL overworld mobs are present -- "all targets" made RSR attack random non-leve
    // enemies. TargetsHaveTarget restricts it to the leve mobs (which aggro us when the leve starts)
    // and leaves passing neutrals alone. Deduplicated like ConfigureForFate.
    public void ConfigureForLeve()
        => SetSetting("HostileType TargetsHaveTarget");

    public void SetSetting(string setting)
    {
        // Dedup per setting KEY (first token) so a run of distinct settings does not thrash a
        // single shared latch and re-send every one every tick.
        var key = setting.Split(' ', 2)[0];
        if (_lastSettings.TryGetValue(key, out var last) && last == setting)
            return;
        try
        {
            if (_otherCommand is { HasAction: true })
            {
                _otherCommand.InvokeAction(OtherCommand.Settings, setting);
                _lastSettings[key] = setting;
                Diagnostics.DebugLog.Verbose($"RSR setting -> {setting} (ipc)");
                return;
            }
        }
        catch { /* gate cannot bind; fall back to the command */ }

        // The OtherCommand gate takes RSR's own enum type and often cannot bind from
        // another plugin, so fall back to the equivalent chat command.
        _command.Run($"/rotation Settings {setting}");
        _lastSettings[key] = setting;
        Diagnostics.DebugLog.Verbose($"RSR setting -> {setting} (command)");
    }

    // Enable/disable RSR's temporary TargetFreely override (parameterless IPC). Best-effort: an
    // older RSR without these gates simply skips it -- Auto mode + the FATE hostile-type still
    // drives targeting. Edge-triggered via _fateFreely so it fires only on a state change.
    private void SendFateFreely(bool on)
    {
        var gate = on ? _enableTargetFreely : _disableTargetFreely;
        try
        {
            if (gate is { HasAction: true })
            {
                gate.InvokeAction();
                Diagnostics.DebugLog.Verbose($"RSR TargetFreely override -> {(on ? "on" : "off")} (ipc)");
                return;
            }
        }
        catch { /* gate unavailable (older RSR); the override is simply not applied */ }
    }

    private void Send(StateCommand mode)
    {
        try
        {
            if (_changeOperatingMode is { HasAction: true })
            {
                _changeOperatingMode.InvokeAction(mode);
                Diagnostics.DebugLog.Verbose($"RSR -> {mode} (ipc)");
                return;
            }
        }
        catch { /* gate cannot bind (custom enum type); use command */ }

        var cmd = mode switch
        {
            StateCommand.Off => "/rotation off",
            StateCommand.Manual => "/rotation Manual",
            _ => "/rotation Auto",
        };
        _command.Run(cmd);
        Diagnostics.DebugLog.Verbose($"RSR -> {mode} (command '{cmd}')");
    }
}

// Indirection so executors can issue chat commands without a hard dependency on
// Dalamud's command manager in tests.
public interface ICommandHelper
{
    void Run(string command);
}
