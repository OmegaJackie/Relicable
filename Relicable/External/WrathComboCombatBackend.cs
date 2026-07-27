using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.Diagnostics;
using Relicable.External.Ipc;

namespace Relicable.External;

// Combat backend driving Wrath Combo (https://github.com/PunishXIV/WrathCombo).
//
// ---------------------------------------------------------------------------
// Verified IPC surface
// ---------------------------------------------------------------------------
// Gate names come from Wrath's own provider registration,
//     EzIPC.Init(typeof(WrathIPC), "WrathCombo", SafeWrapper.IPCException)
// so every gate is "WrathCombo.<MethodName>". The provider is
// WrathCombo.Services.IPC.Provider.
//
// AUTHORITY FOR THE OPTION NUMBERS BELOW is the shipped WrathCombo.API.dll
// (WrathCombo.API.Enum.AutoRotationConfigOption / DPSRotationMode / SetResult), not
// the repo's docs/IPCExample.cs -- that example's copy of the option enum is
// TRUNCATED at member 13 and omits three of the six options used here.
//
// ---------------------------------------------------------------------------
// Why the Set gates are subscribed with an int return
// ---------------------------------------------------------------------------
// The Set gates return Wrath's own SetResult enum, a type this assembly cannot
// reference. Dalamud's CallGateChannel.InvokeFunc<TRet> JSON round-trips the result
// (Newtonsoft serialize + deserialize) whenever TRet differs from the provider's
// declared return type, so an int TRet is converted cleanly from Wrath's enum.
// SetResult is Int32-backed (verified by reflecting WrathCombo.API.dll), so the
// numeric values line up with the mirror enum below.
//
// The two object parameters of SetAutoRotationConfigState are passed as plain ints.
// Wrath reads them with Convert.ToInt32 / Enum.ToObject / Convert.ChangeType and
// documents that "All valid options can be parsed from an int, or the exact expected
// types", so ints avoid coupling to its enum types entirely.
//
// ---------------------------------------------------------------------------
// Leasing
// ---------------------------------------------------------------------------
// Unlike RSR and BossMod Reborn, Wrath is LEASE-based: a consumer registers once,
// receives a Guid, and passes it to every Set call. While a lease is held Wrath names
// the leasing plugin in its own UI and LOCKS every setting that lease has written --
// which is why control is released both on dispose and when the user switches to a
// different backend. Turning Auto-Rotation off is not enough; only ReleaseControl
// hands the settings back.
//
// A lease can be cancelled by Wrath (user revocation, job change, plugin disable) or
// refused outright. Refusal is NOT treated as terminal except for an explicit
// blacklist: Wrath legitimately refuses while its caches are still building, or while
// its IPC toggle is off, and both of those resolve on their own.
//
// ---------------------------------------------------------------------------
// Constraint worth knowing
// ---------------------------------------------------------------------------
// Wrath's AutoRotationController.ShouldSkipAutorotation hard-skips while the player is
// MOUNTED or occupied, and no IPC option relaxes that (RSR differs here). The
// executors' land-and-dismount-before-engage ordering is therefore load-bearing for
// this backend -- do not refactor it away.
public sealed class WrathComboCombatBackend : ICombatBackend, IDisposable
{
    // Mirror of Wrath's SetResult (WrathCombo.API.Enum.SetResult). Int32-backed.
    public enum SetResult
    {
        Unknown = int.MinValue,
        Ignored = -1,
        Okay = 0,
        OkayWorking = 1,
        IpcDisabled = 10,
        InvalidLease = 11,
        BlacklistedLease = 12,
        Duplicate = 13,
        PlayerNotAvailable = 14,
        InvalidConfiguration = 15,
        InvalidValue = 16,
    }

    // Mirror of WrathCombo.API.Enum.AutoRotationConfigOption. Only the members this
    // backend drives are listed; the numeric values are the wire contract.
    private enum Cfg
    {
        InCombatOnly = 0,          // bool, top-level: gate autorotation on being in combat
        DpsRotationMode = 1,       // enum DPSRotationMode: how DPS targets are chosen
        FatePriority = 3,          // bool, DPS: prefer FATE mobs when selecting a target
        OnlyAttackInCombat = 13,   // bool, DPS: never open on a mob that is not already fighting
        DpsAlwaysHardTarget = 19,  // bool, DPS: re-assert the hard target (AoE path only)
        BypassFate = 22,           // bool, top-level: allow engaging FATE mobs out of combat
    }

    // What the executors asked for. Wrath has no "manual mode" switch of its own, so the
    // two engaged states differ only in the configuration pushed alongside them.
    private enum Mode { Off, Auto, Manual }

    private readonly ICallGateSubscriber<bool>? _ipcReady;
    private readonly ICallGateSubscriber<string, string, Guid?>? _registerForLease;
    private readonly ICallGateSubscriber<Guid, object>? _releaseControl;
    private readonly ICallGateSubscriber<Guid, bool, int>? _setAutoRotationState;
    private readonly ICallGateSubscriber<Guid, int>? _setCurrentJobAutoRotationReady;
    private readonly ICallGateSubscriber<Guid, object, object, int>? _setAutoRotationConfigState;

    private readonly Configuration _config;

    // The active lease, or null when none is held.
    private Guid? _lease;

    // Terminal: the user revoked our control and Wrath blacklisted the lease. Only an
    // explicit BlacklistedLease sets this; everything else is retried.
    private bool _leaseBlacklisted;

    // Backoff for lease registration. Wrath refuses while its caches build and while its
    // IPC toggle is off, both of which resolve on their own, so a refusal must not be
    // permanent -- but asking every frame would spam its log.
    private long _lastLeaseAttemptMs;
    private bool _leaseWarned;
    private const long LeaseRetryMs = 5000;

    // Mode plus the job it was applied for. SetCurrentJobAutoRotationReady is per-job and
    // documented as asynchronous ("will take several seconds"), so it is edge-triggered
    // rather than called per tick, and re-fires when the job changes -- Wrath itself
    // cancels leases with CancellationReason.JobChanged.
    private readonly EdgeTrigger<(Mode Mode, uint Job)> _mode;

    // Last value pushed per config option, with the tick it was last attempted, so a
    // rejected option is retried on a backoff instead of every frame.
    private readonly Dictionary<Cfg, int> _lastConfig = new();
    private readonly Dictionary<Cfg, long> _lastConfigAttemptMs = new();
    // Options Wrath rejected as structurally invalid. Retrying those cannot start working.
    private readonly HashSet<Cfg> _configRejected = new();
    private const long ConfigRetryMs = 1000;

    // Cheap liveness re-assert. Wrath can cancel a lease silently (user revocation,
    // AllServicesSuspended, Wrath disabled); without a periodic call, a long single-mode
    // engagement would never notice, because the edge trigger sends nothing.
    private long _lastAssertMs;
    private const long ReassertMs = 10_000;

    public WrathComboCombatBackend(IDalamudPluginInterface pi, Configuration config)
    {
        _config = config;
        _ipcReady = Gate(() => pi.GetIpcSubscriber<bool>("WrathCombo.IPCReady"));
        _registerForLease = Gate(() => pi.GetIpcSubscriber<string, string, Guid?>("WrathCombo.RegisterForLease"));
        _releaseControl = Gate(() => pi.GetIpcSubscriber<Guid, object>("WrathCombo.ReleaseControl"));
        _setAutoRotationState = Gate(() => pi.GetIpcSubscriber<Guid, bool, int>("WrathCombo.SetAutoRotationState"));
        _setCurrentJobAutoRotationReady = Gate(() => pi.GetIpcSubscriber<Guid, int>("WrathCombo.SetCurrentJobAutoRotationReady"));
        _setAutoRotationConfigState = Gate(() => pi.GetIpcSubscriber<Guid, object, object, int>("WrathCombo.SetAutoRotationConfigState"));

        _mode = new EdgeTrigger<(Mode, uint)>(Apply);
    }

    private static ICallGateSubscriber<T>? Gate<T>(Func<ICallGateSubscriber<T>> f)
    { try { return f(); } catch { return null; } }

    private static ICallGateSubscriber<T1, T2>? Gate<T1, T2>(Func<ICallGateSubscriber<T1, T2>> f)
    { try { return f(); } catch { return null; } }

    private static ICallGateSubscriber<T1, T2, T3>? Gate<T1, T2, T3>(Func<ICallGateSubscriber<T1, T2, T3>> f)
    { try { return f(); } catch { return null; } }

    private static ICallGateSubscriber<T1, T2, T3, T4>? Gate<T1, T2, T3, T4>(Func<ICallGateSubscriber<T1, T2, T3, T4>> f)
    { try { return f(); } catch { return null; } }

    // ---------------------------------------------------------------------------
    // ICombatBackend
    // ---------------------------------------------------------------------------

    // Wrath asks consumers to check IPCReady before any other gate: it only returns true
    // once its caches are built. NOTE this is independent of Wrath's own IPC on/off
    // toggle, so Available true does not guarantee a lease can be obtained -- see
    // LeaseRefused, which is what the Dependencies tab should reflect.
    public bool Available
    {
        get
        {
            try { return _ipcReady?.HasFunction == true && _ipcReady.InvokeFunc(); }
            catch { return false; }
        }
    }

    // True when Wrath will not hand us control: it blacklisted us, its registration gate
    // is not even exposed, or the last attempt was refused and we hold no lease. Surfaced
    // in the Dependencies tab, because otherwise the only symptom is "Relicable
    // navigates and targets but never fights".
    public bool LeaseRefused
        => _leaseBlacklisted
           || _registerForLease?.HasFunction != true
           || (_lease is null && _leaseWarned);

    // FATE / treasure-map fights, where the enemies are already hostile: Wrath picks its
    // own target under the configured DPS targeting mode.
    public void EnableAuto() => Engage(Mode.Auto);

    // The open-world relic-note grind. The mob is NEUTRAL and un-aggroed, so the rotation
    // has to open on a hard target that is not yet fighting. DPS targeting is pinned to
    // Manual -- which is what routes Wrath's target selection to the player's own hard
    // target -- and both in-combat gates are cleared. Without those, Wrath waits for
    // combat that never starts and the character stands over the mob doing nothing.
    public void EnableManual() => Engage(Mode.Manual);

    public void Disable() => _mode.Dispatch((Mode.Off, CurrentJob()));

    // Wrath selects its own FATE targets, so the FATE executor hands targeting over --
    // but ONLY while this backend actually holds a lease and is therefore genuinely
    // driving. Anything weaker (a config-only check, or "we have not been refused yet")
    // can report true while Wrath is inert, and the FATE executor then deliberately stops
    // hard-targeting on the assumption Wrath is doing it. Neither side targets, the step
    // never completes, and the character stands in the ring level-synced doing nothing.
    //
    // Keyed on the lease specifically so the failure mode is the SAFE one: with no lease
    // this reads false, the executor keeps targeting and marking as it always did, and the
    // only thing missing is the rotation -- which is visible and diagnosable.
    public bool OwnsFateTargeting
        => _lease is not null
           && _config.WrathManageAutoRotationConfig
           && _config.WrathFateTargeting != Configuration.WrathDpsTargeting.Manual;

    // FATE targeting options. Deliberately does NOT acquire a lease: this is called at
    // objective SELECT, minutes of travel before the first swing, and taking the lease
    // there would name Relicable as the owner of the user's Wrath settings for the whole
    // journey. While no lease is held this is a no-op; once combat engages and the lease
    // exists, the per-tick call applies the options (SetConfig dedups, so it is cheap).
    //
    // FATEPriority does the real work -- it reorders Wrath's target selection to prefer
    // FATE mobs. BypassFATE is belt-and-braces: Wrath's CombatBypass only consults it
    // while InCombatOnly is on, which an engage has already cleared, so it matters only
    // if that set was rejected.
    public void ConfigureForFate()
    {
        if (!_config.WrathManageAutoRotationConfig || _lease is null)
            return;

        SetConfig(Cfg.FatePriority, true);
        SetConfig(Cfg.BypassFate, true);
    }

    public void ResyncNextDispatch()
    {
        _mode.Reset();
        ClearConfigCache();
    }

    // Hands control back: turns Auto-Rotation off, then releases the lease so Wrath
    // unlocks every setting we wrote and stops naming Relicable as their owner. Called
    // when the user switches to a different combat backend, and on plugin dispose.
    // Idempotent, and safe when no lease was ever taken.
    public void ReleaseControl()
    {
        if (_lease is { } lease)
        {
            try { _setAutoRotationState?.InvokeFunc(lease, false); }
            catch (Exception ex) { DebugLog.Verbose($"Wrath Combo: disabling autorotation on release failed: {ex.Message}"); }

            try { _releaseControl?.InvokeAction(lease); DebugLog.Info("Wrath Combo lease released."); }
            catch (Exception ex) { DebugLog.Verbose($"Wrath Combo ReleaseControl failed: {ex.Message}"); }
        }

        _lease = null;
        ClearConfigCache();
        _mode.Reset();
    }

    // ---------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------

    private static uint CurrentJob()
    {
        try { return Steps.GameState.ActiveClassJobId(); }
        catch { return 0u; }
    }

    private void ClearConfigCache()
    {
        _lastConfig.Clear();
        _lastConfigAttemptMs.Clear();
        _configRejected.Clear();
    }

    // Dispatches the mode edge, then keeps a slow liveness re-assert running so a lease
    // cancelled behind our back is noticed during a long fight instead of at the next
    // objective.
    private void Engage(Mode mode)
    {
        var now = Environment.TickCount64;
        if (_mode.Dispatch((mode, CurrentJob())))
        {
            _lastAssertMs = now;
            return;
        }

        if (_lease is not { } lease || now - _lastAssertMs < ReassertMs)
            return;

        _lastAssertMs = now;
        // Cheap compared with SetCurrentJobAutoRotationReady, and its result is what
        // surfaces a silently cancelled lease.
        Report("SetAutoRotationState(re-assert)", Invoke(() => _setAutoRotationState?.InvokeFunc(lease, true)));
    }

    private void Apply((Mode Mode, uint Job) state)
    {
        if (state.Mode == Mode.Off)
        {
            // Only talk to Wrath if we ever actually took control of it.
            if (_lease is { } held)
                Report("SetAutoRotationState(off)", Invoke(() => _setAutoRotationState?.InvokeFunc(held, false)));
            return;
        }

        if (!TryGetLease(out var lease))
            return;

        Report("SetAutoRotationState(on)", Invoke(() => _setAutoRotationState?.InvokeFunc(lease, true)));
        // Report may have invalidated the lease (Wrath cancels on job change). Abort
        // rather than spending the rest of this pass on a Guid known to be dead; the edge
        // trigger was reset, so the executor's next per-tick call re-dispatches cleanly.
        if (_lease is null)
            return;

        // Enables the job's single- and multi-target combos and puts them in Auto mode,
        // preferring settings the user already has. Asynchronous by design, hence the
        // edge trigger around Apply rather than a per-tick call.
        Report("SetCurrentJobAutoRotationReady", Invoke(() => _setCurrentJobAutoRotationReady?.InvokeFunc(lease)));
        if (_lease is null)
            return;

        if (!_config.WrathManageAutoRotationConfig)
            return;

        // Both gates have to come off for a neutral pull: InCombatOnly is the top-level
        // one, OnlyAttackInCombat the DPS-specific one.
        SetConfig(Cfg.InCombatOnly, false);
        SetConfig(Cfg.OnlyAttackInCombat, false);

        if (state.Mode == Mode.Manual)
        {
            // DPSRotationMode.Manual is the load-bearing setting: it routes Wrath's target
            // selection to the player's current hard target instead of its own picker, so
            // the rotation fires on a neutral book mob. DpsAlwaysHardTarget is set for the
            // AoE path (Wrath explicitly ignores it while the mode is Manual on the
            // single-target path), not because it enables the pull.
            SetConfig(Cfg.DpsRotationMode, (int)Configuration.WrathDpsTargeting.Manual);
            SetConfig(Cfg.DpsAlwaysHardTarget, true);
        }
        else
        {
            SetConfig(Cfg.DpsRotationMode, (int)_config.WrathFateTargeting);
            SetConfig(Cfg.DpsAlwaysHardTarget, false);
        }
    }

    private void SetConfig(Cfg option, bool value) => SetConfig(option, value ? 1 : 0);

    private void SetConfig(Cfg option, int value)
    {
        if (_configRejected.Contains(option))
            return;
        if (_lastConfig.TryGetValue(option, out var last) && last == value)
            return;

        var now = Environment.TickCount64;
        if (_lastConfigAttemptMs.TryGetValue(option, out var attempted) && now - attempted < ConfigRetryMs)
            return;
        _lastConfigAttemptMs[option] = now;

        if (!TryGetLease(out var lease))
            return;

        var result = Invoke(() => _setAutoRotationConfigState?.InvokeFunc(lease, (int)option, value));

        switch (result)
        {
            case SetResult.Okay:
            case SetResult.OkayWorking:
            case SetResult.Duplicate:
                // Only latch once Wrath accepted it, so a rejected set is retried rather
                // than remembered as applied.
                _lastConfig[option] = value;
                return;

            case SetResult.InvalidValue:
            case SetResult.InvalidConfiguration:
                // Structural: retrying cannot start working. Warn once and stop.
                _configRejected.Add(option);
                DebugLog.Warn($"Wrath Combo rejected {option}={value} ({result}); Relicable will stop trying to set it. "
                              + "This usually means Relicable and Wrath Combo are versions apart.");
                return;

            default:
                Report($"SetAutoRotationConfigState({option}, {value})", result);
                return;
        }
    }

    private bool TryGetLease(out Guid lease)
    {
        lease = default;
        if (_leaseBlacklisted)
            return false;
        if (_lease is { } existing)
        {
            lease = existing;
            return true;
        }
        if (_registerForLease?.HasFunction != true)
            return false;

        // Backoff. Wrath legitimately refuses while its caches build, so this must retry
        // -- but not every frame.
        var now = Environment.TickCount64;
        if (_lastLeaseAttemptMs != 0 && now - _lastLeaseAttemptMs < LeaseRetryMs)
            return false;
        _lastLeaseAttemptMs = now;

        // Wrath's own readiness gate. Registering before its caches are built just burns
        // a refusal.
        if (!Available)
            return false;

        try
        {
            // The internal name must be this plugin's real internal name -- Wrath uses it
            // to check we are still loaded and to drop the lease when we unload.
            var issued = _registerForLease.InvokeFunc("Relicable", "Relicable");
            if (issued is not { } id)
            {
                // Refused. Registering under a name that already has a lease returns the
                // EXISTING Guid rather than null, so the real causes are: Wrath has
                // blacklisted us (the user revoked control), or Wrath's IPC service is off.
                // Both can change, so this retries on the backoff above.
                if (!_leaseWarned)
                {
                    _leaseWarned = true;
                    DebugLog.Warn("Wrath Combo refused a control lease. Either its IPC service is off, or "
                                  + "Relicable's control was revoked in Wrath Combo. Combat will not be driven "
                                  + "until that is changed.");
                }
                return false;
            }

            _lease = id;
            _leaseWarned = false;
            lease = id;
            DebugLog.Info($"Wrath Combo lease acquired ({id}).");
            return true;
        }
        catch (Exception ex)
        {
            if (!_leaseWarned)
            {
                _leaseWarned = true;
                DebugLog.Warn($"Wrath Combo lease registration failed: {ex.Message}");
            }
            return false;
        }
    }

    private static SetResult Invoke(Func<int?> call)
    {
        try
        {
            var raw = call();
            return raw is null ? SetResult.Unknown : (SetResult)raw.Value;
        }
        catch (Exception ex)
        {
            DebugLog.Verbose($"Wrath Combo IPC call failed: {ex.Message}");
            return SetResult.Unknown;
        }
    }

    // Surfaces the outcomes that mean "this will not work until something changes", and
    // drops the lease when Wrath says it is gone so the next engage re-registers.
    private void Report(string what, SetResult result)
    {
        switch (result)
        {
            case SetResult.Okay:
            case SetResult.OkayWorking:
            case SetResult.Duplicate:
            case SetResult.Ignored:
            case SetResult.Unknown:
                return;

            case SetResult.InvalidLease:
                DebugLog.Info($"Wrath Combo lease is no longer valid ({what}); re-registering.");
                DropLease();
                return;

            case SetResult.BlacklistedLease:
                DebugLog.Warn("Wrath Combo has blacklisted Relicable's lease (control was revoked). "
                              + "Re-allow it in Wrath Combo, then reload Relicable.");
                DropLease();
                _leaseBlacklisted = true;
                return;

            case SetResult.IpcDisabled:
                DebugLog.Warn("Wrath Combo's IPC service is disabled, so Relicable cannot drive it. "
                              + "Enable IPC in Wrath Combo's settings.");
                DropLease();
                return;

            case SetResult.PlayerNotAvailable:
                // Zoning, loading, dead. Transient -- retried on the next engage.
                DebugLog.Verbose($"Wrath Combo: player not available for {what}.");
                return;

            default:
                DebugLog.Warn($"Wrath Combo returned {result} for {what}.");
                return;
        }
    }

    // Uniform state reset for every way a lease can be lost, so nothing is left cached
    // against a lease that no longer owns it.
    private void DropLease()
    {
        _lease = null;
        ClearConfigCache();
        _mode.Reset();
        // Let the next TryGetLease attempt run immediately rather than waiting out the
        // backoff: this is a known-dead lease, not a refusal.
        _lastLeaseAttemptMs = 0;
    }

    public void Dispose() => ReleaseControl();
}
