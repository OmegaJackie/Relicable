using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Relicable.External;

// Avoidance side of the BossMod Reborn (FFXIV-CombatReborn/BossmodReborn) integration:
// hands BMR an AoE-avoidance preset while ANOTHER plugin drives the rotation -- i.e.
// under the Rotation Solver Reborn and Wrath Combo combat backends. Thin wrapper over
// the shared BossModRebornPresetControl (BossMod.Presets.SetActive / ClearActive -- BMR
// keeps the "BossMod." IPC prefix); see that file for the verified IPC surface.
//
// Relicable ships and auto-installs its OWN avoidance preset (BossModRebornAvoidancePreset)
// and uses it by default, exactly as the combat backend does with its rotation preset.
// That preset contains one module, MiscAI.NormalMovement, which is pure movement and
// never writes Hints.ForcedTarget -- so avoidance cannot steal the hard target from the
// plugin that owns the rotation. See BossModRebornAvoidancePreset for why the previous
// default ("VBM Multibox") was actively harmful.
//
// Not used when Backend == BossModReborn: there the combat preset
// (BossModRebornCombatBackend) already includes avoidance, and CombatAssist skips this
// path so the two do not clobber each other's active preset (SetActive is exclusive).
public sealed class BossModRebornIpc
{
    private readonly BossModRebornPresetControl _control;

    // BossMod.Presets.Get -> serialized preset JSON (or null), and Presets.Create(json,
    // overwrite) -> installed. Same gates the combat backend uses to self-install.
    private readonly ICallGateSubscriber<string, string?>? _getPreset;
    private readonly ICallGateSubscriber<string, bool, bool>? _createPreset;

    // One-shot: attempt the install once per session (or per resync) regardless of
    // outcome, so a failure cannot spin every tick.
    private bool _installed;

    public BossModRebornIpc(IDalamudPluginInterface pi)
    {
        _control = new BossModRebornPresetControl(pi, "avoidance");
        _getPreset = TrySub(() => pi.GetIpcSubscriber<string, string?>("BossMod.Presets.Get"));
        _createPreset = TrySub(() => pi.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create"));
    }

    private static ICallGateSubscriber<T1, T2>? TrySub<T1, T2>(Func<ICallGateSubscriber<T1, T2>> f)
    { try { return f(); } catch { return null; } }

    private static ICallGateSubscriber<T1, T2, T3>? TrySub<T1, T2, T3>(Func<ICallGateSubscriber<T1, T2, T3>> f)
    { try { return f(); } catch { return null; } }

    public bool Available => _control.Available;

    // Activate the avoidance preset. An EMPTY configured name means "use Relicable's own
    // shipped preset", which is the default; a non-empty name is the user's own choice and
    // is used verbatim without installing anything.
    public void EnableAvoidance(string configuredPreset)
    {
        if (!string.IsNullOrWhiteSpace(configuredPreset))
        {
            _control.Activate(configuredPreset);
            return;
        }

        EnsureInstalled();
        _control.Activate(BossModRebornAvoidancePreset.Name);
    }

    public void Disable() => _control.Clear();

    // Force the next Enable/Disable to re-send (for example if BMR changed the active
    // preset itself), and re-attempt the install -- the user may have deleted it.
    public void Resync()
    {
        _installed = false;
        _control.Resync();
    }

    // Install (or refresh) the shipped avoidance preset. Idempotent: Presets.Create is
    // called with overwrite, so it only ever replaces Relicable's own preset.
    private void EnsureInstalled()
    {
        if (_installed)
            return;
        _installed = true;

        if (_getPreset is not { HasFunction: true } || _createPreset is not { HasFunction: true })
        {
            Diagnostics.DebugLog.Warn(
                "BossMod Reborn: the Presets.Get/Create IPC is unavailable, so Relicable cannot auto-install " +
                "its avoidance preset. Update BossMod Reborn, or create a movement-only preset (just " +
                "MiscAI.NormalMovement, no AutoTarget and no FollowSlot) and name it in /relic config.");
            return;
        }

        try
        {
            var existed = _getPreset.InvokeFunc(BossModRebornAvoidancePreset.Name) != null;
            _createPreset.InvokeFunc(BossModRebornAvoidancePreset.Json, true);
            Diagnostics.DebugLog.Info(existed
                ? $"BossMod Reborn: refreshed avoidance preset '{BossModRebornAvoidancePreset.Name}'"
                : $"BossMod Reborn: installed avoidance preset '{BossModRebornAvoidancePreset.Name}'");
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Warn(
                $"BossMod Reborn: could not install the avoidance preset '{BossModRebornAvoidancePreset.Name}': {ex.Message}");
        }
    }
}
