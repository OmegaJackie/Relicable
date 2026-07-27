using Dalamud.Plugin;

namespace Relicable.External;

// Avoidance side of the BossMod Reborn (FFXIV-CombatReborn/BossmodReborn) integration:
// hands BMR an AoE-avoidance preset while RSR (or nothing) drives the rotation. Thin
// wrapper over the shared BossModRebornPresetControl (BossMod.Presets.SetActive /
// ClearActive -- BMR keeps the "BossMod." IPC prefix); see that file for the verified
// IPC surface.
//
// Relicable uses this alongside Rotation Solver Reborn: BMR dodges, RSR does the
// rotation. The preset must be configured in BMR for avoidance-only (its strategy
// tracks set so it does NOT run the rotation) to avoid fighting RSR; the preset name
// comes from config. SetActive is exclusive, so while avoidance is on Relicable's
// preset owns BMR's active-preset slot.
//
// Not used when Backend == BossModReborn: there the combat preset
// (BossModRebornCombatBackend) already includes avoidance, and CombatAssist skips this
// path so the two do not clobber each other's active preset.
public sealed class BossModRebornIpc
{
    private readonly BossModRebornPresetControl _control;

    public BossModRebornIpc(IDalamudPluginInterface pi)
        => _control = new BossModRebornPresetControl(pi, "avoidance");

    public bool Available => _control.Available;

    // Activate the named avoidance preset. No-op if the name is empty (nothing
    // configured) or BossMod Reborn is not loaded.
    public void EnableAvoidance(string presetName) => _control.Activate(presetName);

    public void Disable() => _control.Clear();

    // Force the next Enable/Disable to re-send (for example if BMR changed the
    // active preset itself).
    public void Resync() => _control.Resync();
}
