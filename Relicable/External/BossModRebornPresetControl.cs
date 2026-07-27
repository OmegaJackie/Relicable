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
// so re-activating the same preset every tick is a no-op.
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
        if (!string.IsNullOrEmpty(presetName))
            _preset.Dispatch(presetName);
    }

    // Clear whatever we activated.
    public void Clear() => _preset.Dispatch(string.Empty);

    // Force the next Activate/Clear to re-send (e.g. BMR changed the active preset
    // itself, or we left a duty).
    public void Resync() => _preset.Reset();

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
                Diagnostics.DebugLog.Verbose($"BMR {_label} preset -> '{presetName}'");
            }
            else
                Diagnostics.DebugLog.Warn(
                    $"BossMod Reborn -> SetActive('{presetName}') returned false: no preset by that name " +
                    $"exists. Create a preset with this exact name (the {_label} preset in Relicable config) " +
                    "or clear the setting; BossMod Reborn is not engaged until the name matches.");
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
