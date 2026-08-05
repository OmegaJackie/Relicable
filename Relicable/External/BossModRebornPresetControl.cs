using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.External.Ipc;

namespace Relicable.External;

// Shared control over BossMod Reborn's active-preset slot, via its VERIFIED
// autorotation IPC. BMR keeps the "BossMod." prefix on its gates
// (FFXIV-CombatReborn/BossmodReborn IPCProvider.cs):
//   BossMod.Presets.SetActive   -> Func<string, bool>  (activate ONE preset by name,
//                                                        clearing any others; false if
//                                                        the name is not found)
//   BossMod.Presets.ClearActive -> Func<bool>          (deactivate the active preset)
//   BossMod.Presets.GetActive   -> Func<string?>       (the active preset's name, or null)
//
// Two independent uses share this control (each its own instance / edge-latch): the
// avoidance preset (BossModRebornIpc, used under the RSR backend) and the combat preset
// (BossModRebornCombatBackend, used under the BossMod Reborn backend). Only one is ever
// active at a time because the combat-backend selection gates which path runs, and
// SetActive is exclusive, so activating one implicitly clears the other. Edge-triggered
// so re-activating the same preset every tick is a no-op -- with a throttled reconcile
// (see Reconcile) that re-arms the latch when BMR empties the slot itself, because a pure
// edge trigger cannot recover from a clear it never observed.
internal sealed class BossModRebornPresetControl
{
    private readonly ICallGateSubscriber<string, bool>? _setActive;
    private readonly ICallGateSubscriber<bool>? _clearActive;
    private readonly ICallGateSubscriber<string?>? _getActive;
    private readonly EdgeTrigger<string> _preset;
    private readonly string _label; // for logs: "avoidance" / "combat"

    // The preset name THIS control last activated, so Clear never deactivates a preset
    // the user picked by hand.
    private string _lastActivated = string.Empty;

    // How often Reconcile is allowed to read BMR's active preset. Activate is level-triggered
    // by the executors (every tick, from six call sites), so an unthrottled GetActive would be
    // a per-frame IPC round trip for no benefit.
    private const long ReconcileIntervalMs = 2000;
    private const int ReconcileHintAfter = 5;
    private long _reconciledAt;

    // A preset name SetActive has already REJECTED (no preset by that name exists in BMR).
    // Without this latch the reconcile below re-sends it every 2s for the whole step and the
    // four-line warning in Send floods the log. Cleared by Resync -- the user may have created
    // the preset since.
    private string _rejected = string.Empty;

    // Consecutive reconciles that found the slot empty again, so the "something else keeps
    // taking this slot" hint is logged once instead of every two seconds.
    private int _reconciles;

    public BossModRebornPresetControl(IDalamudPluginInterface pi, string label)
    {
        _label = label;
        _setActive = TrySub(() => pi.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive"));
        _clearActive = TrySub(() => pi.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive"));
        _getActive = TrySub(() => pi.GetIpcSubscriber<string?>("BossMod.Presets.GetActive"));
        _preset = new EdgeTrigger<string>(Send);
    }

    // SetActive is a Func, so probe HasFunction.
    public bool Available => _setActive?.HasFunction ?? false;

    // Activate the named preset. No-op if the name is empty (nothing configured).
    public void Activate(string presetName)
    {
        if (string.IsNullOrEmpty(presetName))
            return;
        Reconcile(presetName);
        _preset.Dispatch(presetName);
    }

    // Re-arm the edge latch when BMR has VACATED the active-preset slot behind our back.
    //
    // THIS IS WHY AVOIDANCE DIED IN FATES. BMR's own AutorotationConfig has
    // ClearPresetOnCombatEnd and ClearPresetOnDeath, and BMR nulls its active preset whenever
    // either fires (on death it assigns ForceDisable = new Preset(""), so GetActive reads back
    // as an empty string rather than null -- both are handled here). Activate is called every
    // tick by the executors, but EdgeTrigger only invokes on a CHANGE: once "Relicable Avoidance"
    // had been dispatched, every later Activate compared equal and returned without sending, so
    // nothing ever put the preset back. Inside a FATE combat drops between every wave, so
    // avoidance survived exactly one pull and was gone for the rest of the step -- the reported
    // "AoE avoidance isn't activating on FATEs". The same latch governs the COMBAT preset, so a
    // mid-fight death silently ended the rotation too.
    //
    // Only an EMPTY slot is reclaimed. A different, non-empty preset means the user (or another
    // plugin) deliberately switched, and we must not fight them for it -- the same rule the clear
    // arm in Send already follows. And only when WE are the ones who put a preset there
    // (_lastActivated non-empty), so a never-successful SetActive cannot spin here.
    private void Reconcile(string presetName)
    {
        // Older BMR without the read gate: no way to tell, so keep the plain edge behaviour.
        if (_getActive is not { HasFunction: true })
            return;
        if (string.Equals(_rejected, presetName, StringComparison.Ordinal))
            return;

        var now = Environment.TickCount64;
        if (now - _reconciledAt < ReconcileIntervalMs)
            return;
        _reconciledAt = now;

        var active = TryGetActive();
        if (!string.IsNullOrEmpty(active))
        {
            _reconciles = 0;
            return;
        }

        if (string.IsNullOrEmpty(_lastActivated))
            return;

        _preset.Reset();
        if (++_reconciles == ReconcileHintAfter)
            Diagnostics.DebugLog.Info(
                $"BMR {_label} preset '{presetName}' keeps being cleared by BossMod Reborn; Relicable is " +
                "re-applying it. That is BMR's own ClearPresetOnCombatEnd / ClearPresetOnDeath setting, or " +
                "its AI loop (which reassigns the active preset every frame -- turn it off with /bmrai off). " +
                "This is a note, not an error.");
    }

    // Clear whatever we activated.
    public void Clear() => _preset.Dispatch(string.Empty);

    // Force the next Activate/Clear to re-send (e.g. BMR changed the active preset
    // itself, or we left a duty).
    public void Resync()
    {
        _rejected = string.Empty;
        _reconciles = 0;
        _reconciledAt = 0;
        _preset.Reset();
    }

    private void Send(string presetName)
    {
        if (string.IsNullOrEmpty(presetName))
        {
            if (_clearActive is not { HasFunction: true })
                return;
            try
            {
                // Only clear a preset WE activated: one the user switched to by hand mid-run
                // must survive. BMR's GetActive returns the active preset's name, or null when
                // none is active (nothing to clear) -- either way, only ClearActive when the
                // active preset is the one this control set. When the gate is missing (an older
                // BMR build) fall back to the old unconditional clear rather than leaving our
                // preset stuck on.
                if (_getActive is { HasFunction: true })
                {
                    var active = TryGetActive();
                    if (active == null || !string.Equals(active, _lastActivated, StringComparison.Ordinal))
                    {
                        Diagnostics.DebugLog.Verbose(
                            $"BMR {_label} preset -> clear skipped (active '{active ?? "none"}' was not set by Relicable)");
                        _lastActivated = string.Empty;
                        return;
                    }
                }
                _clearActive.InvokeFunc();
                _lastActivated = string.Empty;
                Diagnostics.DebugLog.Verbose($"BMR {_label} preset -> cleared");
            }
            catch { /* unavailable */ }
            return;
        }

        if (_setActive is not { HasFunction: true })
            return;
        try
        {
            var ok = _setActive.InvokeFunc(presetName);
            if (ok)
            {
                _lastActivated = presetName;
                _rejected = string.Empty;
                Diagnostics.DebugLog.Verbose($"BMR {_label} preset -> '{presetName}'");
            }
            else
            {
                // Latch the rejection so Reconcile stops re-sending a name BMR does not have.
                _rejected = presetName;
                Diagnostics.DebugLog.Warn(
                    $"BossMod Reborn -> SetActive('{presetName}') returned false: no preset by that name " +
                    $"exists. Create a preset with this exact name (the {_label} preset in Relicable config) " +
                    "or clear the setting; BossMod Reborn is not engaged until the name matches.");
            }
        }
        catch { /* unavailable */ }
    }

    // The active preset's name, or null when none is active (or the read fails).
    private string? TryGetActive()
    {
        try { return _getActive?.InvokeFunc(); }
        catch { return null; }
    }

    private static T? TrySub<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }
}
