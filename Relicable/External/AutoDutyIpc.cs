using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.External.Ipc;

namespace Relicable.External;

// Hardened wrapper over AutoDuty. VERIFIED (AutoDuty IPCProvider.cs):
//   AutoDuty.Run(uint territoryType, int loops = 0, bool bareMode = false) -> void
//   AutoDuty.IsStopped() -> bool ; AutoDuty.Stop() -> void
//
// Per-frame cost: IsStopped is polled every tick while a duty runs; it is cached
// (50 ms) since duty state changes slowly.
//
// Command re-firing: Run is latched per territory so re-calling it for the same
// duty does nothing until ResetRun() (called when the duty step ends). This
// prevents an accidental duty restart if Run is reached on consecutive ticks.
//
// Availability gating: AutoDuty's void IPC methods (Run/Stop/SetConfig) register as
// Dalamud CallGate Actions, so they must be probed with HasAction. Their HasFunction is
// always false (HasFunction flags a registered Func), so gating them on HasFunction skips
// them even when they work -- the bug that kept Run from ever firing. The value-returning
// endpoints (IsStopped/GetConfig/ContentHasPath) register as Funcs and use HasFunction.
public sealed class AutoDutyIpc
{
    private readonly ICallGateSubscriber<uint, int, bool, object>? _run;
    private readonly ICallGateSubscriber<object>? _stop;
    private readonly ICallGateSubscriber<bool>? _isStopped;
    private readonly ICallGateSubscriber<string, object, object>? _setConfig;
    private readonly ICallGateSubscriber<string, string>? _getConfig;
    private readonly ICallGateSubscriber<uint, bool>? _contentHasPath;
    private readonly Cached<bool> _stoppedCache;

    private uint? _latchedTerritory;

    public AutoDutyIpc(IDalamudPluginInterface pi)
    {
        _run = TrySub(() => pi.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run"));
        _stop = TrySub(() => pi.GetIpcSubscriber<object>("AutoDuty.Stop"));
        _isStopped = TrySub(() => pi.GetIpcSubscriber<bool>("AutoDuty.IsStopped"));
        // SetConfig(string, object) and ContentHasPath(uint) -> bool, verified against
        // AutoDuty's IPCProvider (ECommons EzIPC). SetConfig drives DutyMode/Unsynced
        // for the unsynced Light farm; ContentHasPath is a best-effort pre-check.
        _setConfig = TrySub(() => pi.GetIpcSubscriber<string, object, object>("AutoDuty.SetConfig"));
        _getConfig = TrySub(() => pi.GetIpcSubscriber<string, string>("AutoDuty.GetConfig"));
        _contentHasPath = TrySub(() => pi.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath"));
        _stoppedCache = new Cached<bool>(ReadStopped, 50);
    }

    public bool Available => _run?.HasAction ?? false;

    // Diagnostic: log which AutoDuty IPC endpoints are present. Void methods (Run/Stop/SetConfig)
    // register as Actions and are probed with HasAction; value-returning ones (IsStopped/GetConfig/
    // ContentHasPath) register as Funcs and are probed with HasFunction. Probing an Action with
    // HasFunction wrongly reports it absent -- exactly how the Run hand-off was being skipped.
    public void LogAvailability()
    {
        Diagnostics.DebugLog.Info(
            $"AutoDuty IPC: Run={_run?.HasAction ?? false}, Stop={_stop?.HasAction ?? false}, " +
            $"IsStopped={_isStopped?.HasFunction ?? false}, SetConfig={_setConfig?.HasAction ?? false}, " +
            $"GetConfig={_getConfig?.HasFunction ?? false}, ContentHasPath={_contentHasPath?.HasFunction ?? false}");
    }

    public void Run(uint territoryType, int loops = 1, bool bareMode = false)
    {
        if (_latchedTerritory == territoryType)
            return; // already running this duty; do not re-fire
        if (_run is not { HasAction: true })
        {
            Diagnostics.DebugLog.Warn(
                "AutoDuty -> Run SKIPPED: the AutoDuty.Run IPC is unavailable (HasAction false), so no duty " +
                "will run. AutoDuty is not installed or is disabled. Install/enable AutoDuty (and confirm it " +
                "runs a duty from its own window first).");
            return;
        }
        try
        {
            _run.InvokeAction(territoryType, loops, bareMode);
            _latchedTerritory = territoryType;
            _stoppedCache.Invalidate();
            Diagnostics.DebugLog.Verbose($"AutoDuty -> Run territory={territoryType} loops={loops} bare={bareMode}");
        }
        catch { /* unavailable */ }
    }

    // Set AutoDuty's duty mode (e.g. "Trial", "Regular") via its reflection-based
    // SetConfig IPC. The Light farm needs "Trial" so a solo player queues the trial.
    public void SetDutyMode(string mode) => SetConfig("DutyModeEnum", mode);

    // Toggle AutoDuty's Unsynced flag, which sets the Duty Finder "Unrestricted Party"
    // box (level sync off, party-size limits lifted) so old content can be soloed.
    public void SetUnsynced(bool on) => SetConfig("Unsynced", on ? "true" : "false");

    // Ensure AutoDuty LEAVES the instance after the clear (its AutoExitDuty config). Relicable
    // needs the duty to exit so the relic run can advance to the next objective; if AutoExitDuty is
    // off -- e.g. left off by a `/relic adset AutoExitDuty false` probe, or a manual AutoDuty
    // setting -- AutoDuty clears the duty then just sits in it forever ("not exiting at all"). There
    // is no AutoDuty "delay before exit" config (only this on/off), so a timed in-duty linger is not
    // available; credit is instead covered by the post-exit RelicNote poll (see CreditGraceMs).
    public void SetAutoExit(bool on) => SetConfig("AutoExitDuty", on ? "true" : "false");

    // True when AutoDuty has a navigation path file for this duty. Best-effort: a
    // missing gate returns false, in which case the caller may still attempt the run.
    public bool ContentHasPath(uint territoryType)
    {
        if (_contentHasPath is not { HasFunction: true })
            return false;
        try { return _contentHasPath.InvokeFunc(territoryType); }
        catch { return false; }
    }

    // Read an AutoDuty config field's value, or empty if the field name does not resolve.
    public string GetConfig(string key)
    {
        if (_getConfig is not { HasFunction: true })
            return string.Empty;
        try { return _getConfig.InvokeFunc(key) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private void SetConfig(string key, string value)
    {
        if (_setConfig is not { HasAction: true })
        {
            Diagnostics.DebugLog.Warn(
                $"AutoDuty -> SetConfig {key}={value} SKIPPED: the AutoDuty.SetConfig IPC is unavailable " +
                "(HasAction false), so the mode/unsynced are NOT applied and a synced duty will not " +
                "solo-queue. Set AutoDuty's own Duty Mode + Unsynced in its window as a workaround.");
            return;
        }
        try
        {
            _setConfig.InvokeAction(key, value);
            // Read the value back so the log proves whether AutoDuty accepted it. An EMPTY
            // read-back means the field name did not match (AutoDuty logs "Unable to find
            // config"), so the mode/unsynced never applied and the duty cannot solo-queue.
            var readBack = GetConfig(key);
            Diagnostics.DebugLog.Info(
                $"AutoDuty -> SetConfig {key}={value} (read-back '{readBack}'"
                + (string.IsNullOrEmpty(readBack) ? "; EMPTY = no such AutoDuty config field, the name differs)" : ")"));
        }
        catch { /* unavailable */ }
    }

    public void Stop()
    {
        if (_stop is { HasAction: true })
        {
            try { _stop.InvokeAction(); }
            catch { /* unavailable */ }
        }
        ResetRun();
    }

    // Public passthrough to SetConfig for live exploration from the /relic adset debug command
    // (find the exit key via ProbeConfig, then try setting it without a rebuild). Logs the
    // read-back like the internal callers so it is clear whether AutoDuty accepted the field.
    public void SetConfigDebug(string key, string value) => SetConfig(key, value);

    // One-off diagnostic (run via /relic adcfg): read a broad set of candidate AutoDuty
    // Configuration field names and log each that returns a value. GetConfig reflects a field
    // by NAME and returns "" for an unknown name, so a non-empty read-back proves the field
    // exists (and shows its current value). The goal is to discover which field governs
    // AutoDuty's post-clear instance exit (auto-exit toggle or a pre-exit delay) so it can then
    // be driven with SetConfig / the /relic adset command. The known-good anchors (DutyModeEnum,
    // Unsynced) confirm the probe itself works; if even those read empty, GetConfig is not live.
    public void ProbeConfig()
    {
        if (_getConfig is not { HasFunction: true })
        {
            Diagnostics.DebugLog.Warn("AutoDuty ProbeConfig: GetConfig IPC is unavailable (AutoDuty not loaded/enabled?). Cannot probe.");
            return;
        }

        // Candidate Configuration field names (with case variants for the likely ones). Reflection
        // is name-based, so both casings are tried where AutoDuty's convention is uncertain.
        string[] candidates =
        {
            // --- exit / leave the instance after clearing ---
            "AutoExitDuty", "autoExitDuty", "ExitDutyEnabled", "ExitDutyOnComplete", "ExitDutyOnCompletion",
            "AutoLeaveDuty", "autoLeaveDuty", "LeaveDutyOnComplete", "ExitDuty", "LeaveDuty",
            "DontExitDuty", "StayInDuty", "autoExit",
            // --- delay / wait around the exit or between loops ---
            "ExitDutyDelay", "LeaveDutyDelay", "ExitDelay", "PreExitDelay", "WaitBeforeExit",
            "WaitTimeBeforeExit", "PostDutyDelay", "DutyCompleteDelay", "WaitTime", "waitTime",
            "LoopDelay", "DelayBetweenLoops", "WaitTimeBetweenLoops", "PreLoopDelay", "betweenLoopDelay",
            // --- loop / termination behaviour ---
            "LoopTimes", "StopLevel", "TerminationMethodEnum", "TerminationMethod", "TerminationMode",
            "TerminationKeepActive", "PreLoopActions", "BetweenLoopActions", "TerminationActions",
            // --- context / management flags (help identify the config surface) ---
            "autoManageRSRState", "autoManageBossModAISetting", "hideOverlay", "autoEquipRecommendedGear",
            // --- known-good anchors (should read non-empty; prove the probe works) ---
            "dutyModeEnum", "DutyModeEnum", "Unsynced",
        };

        // Emitted via Plugin.Log directly (not DebugLog.Info, which is gated by the debug toggle)
        // so this user-invoked diagnostic always prints its findings.
        Plugin.Log.Information("[Relicable] AutoDuty ProbeConfig: reading candidate config fields (non-empty = the field exists) ...");
        var found = 0;
        foreach (var key in candidates)
        {
            string val;
            try { val = _getConfig.InvokeFunc(key) ?? string.Empty; }
            catch { val = string.Empty; }
            if (!string.IsNullOrEmpty(val))
            {
                found++;
                Plugin.Log.Information($"[Relicable]   [exists] {key} = '{val}'");
            }
            else
            {
                Diagnostics.DebugLog.Verbose($"  [none]   {key}");
            }
        }
        Plugin.Log.Information(
            $"[Relicable] AutoDuty ProbeConfig: {found}/{candidates.Length} candidate field(s) returned a value. " +
            "Look above for an exit/leave/delay field, then test it with '/relic adset <key> <value>' " +
            "(e.g. an auto-exit bool to false, or a delay to 2). If only the anchors (DutyModeEnum/Unsynced) " +
            "showed, the exit is not a simple config field and we take a different approach.");
    }

    // Clear the run latch so the next Run() for any territory will fire.
    public void ResetRun()
    {
        _latchedTerritory = null;
        _stoppedCache.Invalidate();
    }

    public bool IsStopped() => _stoppedCache.Value;

    private bool ReadStopped()
    {
        if (_isStopped is not { HasFunction: true })
            return true;
        try { return _isStopped.InvokeFunc(); }
        catch { return true; }
    }

    private static T? TrySub<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }
}
