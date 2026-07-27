using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Relicable.External;

// Combat backend that drives the rotation with BossMod Reborn's autorotation
// (Configuration.CombatBackend.BossModReborn), so RSR is not required. BMR keeps the
// "BossMod." IPC prefix, so the gate names below are BMR's own registrations (verified
// against FFXIV-CombatReborn/BossmodReborn IPCProvider.cs).
//
// Enable == activate a ROTATION-ONLY preset; its autorotation then executes on the
// player's current hard target, which Relicable always sets (KillTarget marks +
// hard-targets the mob; the FATE/leve executors close to the mob and hard-target it).
// BMR has no separate "auto" vs "manual" rotation mode -- the active preset simply
// acts on the current target -- so EnableAuto and EnableManual both map to activating
// the preset. Disable clears it.
//
// WHY BMR "WOULDN'T ATTACK" (the bug this backend was hardened to fix):
//   The rotation only fires if the active preset's job modules use Targeting = "Manual"
//   ("use the player's current target for all actions"). BMR's own shipped default
//   preset "VBM Default" uses Targeting = "Auto", which auto-selects the best target from
//   its priority list -- and a NEUTRAL, un-aggroed relic mob scores priority 0, so the
//   search returns null and nothing is cast. Relicable therefore SHIPS and auto-installs
//   its own rotation-only, Manual-targeting preset (BossModRebornRelicPreset) via
//   BossMod.Presets.Create, and activates THAT by default, so the backend works out of
//   the box with no user preset setup. See BossModRebornRelicPreset for the full rationale.
//
// BMR's AI loop (/bmrai) is kept OFF everywhere -- including FATEs. Under BMR the AI
// loop CANNOT run alongside an IPC-activated preset: /bmrai on routes through
// SwitchToFollow -> SwitchToIdle, which nulls the active preset, and while the AI is
// engaged AIBehaviour reassigns the active preset EVERY FRAME from the AI's own
// AIAutorotPresetName slot (AIManager.cs / AIBehaviour.cs:113, verified) -- stomping
// Relicable's SetActive'd preset so the rotation never fires. The old integration
// turned the AI on at FATEs for movement help; that is unnecessary now (the FATE
// executor navigates into the ring and closes on each mob itself) and actively harmful
// under BMR. Engage force-sends /bmrai off (edge-triggered) so a user-enabled AI loop
// cannot silently stomp the preset mid-run.
public sealed class BossModRebornCombatBackend : ICombatBackend
{
    private readonly Configuration _config;
    private readonly BossModRebornPresetControl _control;

    // BossMod.Presets.Get -> serialized preset JSON (or null if absent); used as an
    // existence check before Create / before activating a user-named preset.
    private readonly ICallGateSubscriber<string, string?>? _getPreset;
    // BossMod.Presets.Create(presetJson, overwrite) -> installed. Lets Relicable self-install
    // its shipped rotation preset instead of relying on the user to hand-create one.
    private readonly ICallGateSubscriber<string, bool, bool>? _createPreset;

    // The AI-loop state (/bmrai on|off) we last sent, so the native command is issued only on a
    // change. null after a resync = unknown; force the next send.
    private bool? _aiSent;

    // One-shot latches: install the shipped preset and set ClearPresetOnCombatEnd once per
    // session; cache the resolved preset name (recomputed on resync) to avoid a per-tick IPC.
    private bool _installed;
    private bool _configuredCombatEnd;
    private string? _resolvedPreset;

    public BossModRebornCombatBackend(IDalamudPluginInterface pi, Configuration config)
    {
        _config = config;
        _control = new BossModRebornPresetControl(pi, "combat");
        _getPreset = TrySub(() => pi.GetIpcSubscriber<string, string?>("BossMod.Presets.Get"));
        _createPreset = TrySub(() => pi.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create"));
    }

    public bool Available => _control.Available;

    public void EnableAuto() => Engage();
    public void EnableManual() => Engage();

    public void Disable()
    {
        _control.Clear();
        // Keep BMR's AI loop off on the way out too, so a stopped step cannot leave it driving.
        SetAi(false);
    }

    // No FATE-specific tuning under BMR: the AI loop must stay off (see the class note -- it
    // stomps the active preset every frame), and the Manual-targeting preset already rotates
    // on the hard target the FATE executor sets.
    public void ConfigureForFate() { }

    // BMR's rotation preset (Targeting = Manual) acts strictly on Relicable's current hard
    // target, so the FATE executor keeps ownership of targeting -- it hard-targets each FATE mob
    // and BMR rotates on it. BMR does not auto-select FATE mobs on its own here.
    public bool OwnsFateTargeting => false;

    public void ResyncNextDispatch()
    {
        _control.Resync();
        _aiSent = null;         // force the next Enable/Disable to re-send the AI-off state
        _resolvedPreset = null; // re-resolve the preset name (the config may have changed)
    }

    // Activate the rotation preset and set BMR's AI loop for the current mode. The preset is
    // Relicable's shipped Manual-targeting rotation preset (auto-installed) unless the user configured
    // a valid custom one, so BMR casts the rotation on Relicable's current hard target -- including
    // a neutral, non-aggroed relic-note mob. Native command via ECommons.Chat because Dalamud's
    // ICommandManager.ProcessCommand silently drops /bmr*.
    private void Engage()
    {
        EnsureInstalled();
        EnsureCombatEndConfig();

        var preset = Resolve();

        // Warn about a movement/AI preset once per AI-state edge -- such a preset silently fails
        // to pull the neutral mob (it targets/moves on its own). The shipped preset never trips
        // this; it only guards a user-set custom preset name.
        if (SetAi(false) && LooksLikeAiPreset(preset))
            Diagnostics.DebugLog.Warn(
                $"BossMod Reborn combat preset '{preset}' is an AI/movement preset; it will NOT reliably pull " +
                "neutral (non-aggroed) relic mobs. Clear the combat preset field in /relic config " +
                $"to use Relicable's built-in '{BossModRebornRelicPreset.Name}' preset (recommended).");

        _control.Activate(preset);
    }

    // Install Relicable's shipped rotation preset once per session (idempotent). overwrite=true only
    // ever replaces OUR own distinctly-named preset, never a user's, and keeps it matching the shipped
    // definition across Relicable updates. If BMR is too old to expose Get/Create, we cannot
    // self-install -- the resolved name must already exist, else SetActive warns.
    private void EnsureInstalled()
    {
        if (_installed)
            return;
        _installed = true; // attempt once regardless of outcome, so a failure does not spin every tick

        if (_getPreset is not { HasFunction: true } || _createPreset is not { HasFunction: true })
        {
            Diagnostics.DebugLog.Warn(
                "BossMod Reborn: the Presets.Get/Create IPC is unavailable, so Relicable cannot auto-install " +
                "its rotation preset. Update BossMod Reborn, or create a rotation-only preset (job modules " +
                "with Targeting = Manual) and put its name in /relic config > combat preset.");
            return;
        }

        try
        {
            var existed = _getPreset.InvokeFunc(BossModRebornRelicPreset.Name) != null;
            _createPreset.InvokeFunc(BossModRebornRelicPreset.Json, true);
            Diagnostics.DebugLog.Info(existed
                ? $"BossMod Reborn: refreshed rotation preset '{BossModRebornRelicPreset.Name}'"
                : $"BossMod Reborn: installed rotation preset '{BossModRebornRelicPreset.Name}'");
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Warn($"BossMod Reborn: could not install the rotation preset '{BossModRebornRelicPreset.Name}': {ex.Message}");
        }
    }

    // Stop BMR dropping the active preset when the player leaves combat. Between grind mobs the
    // player is briefly out of combat; a cleared preset would leave the NEXT pull with no rotation,
    // because Relicable re-activates only on a name change (edge-triggered), so a silent clear sticks --
    // another "targets but never attacks" path. Best-effort via the /bmr config console (harmless if
    // already false; BMR's AutorotationConfig.ClearPresetOnCombatEnd defaults false). Sent through
    // ECommons.Chat like /bmrai (ProcessCommand drops /bmr*).
    private void EnsureCombatEndConfig()
    {
        if (_configuredCombatEnd)
            return;
        _configuredCombatEnd = true;
        try
        {
            ECommons.Automation.Chat.ExecuteCommand("/bmr cfg Autorotation ClearPresetOnCombatEnd false");
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Warn($"BossMod Reborn: could not set ClearPresetOnCombatEnd false: {ex.Message}");
        }
    }

    // The preset name to activate, cached until a resync. Empty config, or the old bad default
    // "VBM Default" (Auto targeting -> will not pull neutral mobs), means "use the shipped preset". A
    // user-set custom name is honored only if it actually exists in BossMod; otherwise we fall back to
    // the shipped preset (and warn) rather than SetActive-ing a name that does nothing.
    private string Resolve() => _resolvedPreset ??= ResolvePreset();

    private string ResolvePreset()
    {
        var configured = (_config.BossModRebornCombatPreset ?? string.Empty).Trim();
        if (configured.Length == 0 || string.Equals(configured, "VBM Default", StringComparison.OrdinalIgnoreCase))
            return BossModRebornRelicPreset.Name;

        if (_getPreset is { HasFunction: true })
        {
            try
            {
                if (_getPreset.InvokeFunc(configured) == null)
                {
                    Diagnostics.DebugLog.Warn(
                        $"BossMod Reborn: configured combat preset '{configured}' does not exist; using " +
                        $"Relicable's built-in '{BossModRebornRelicPreset.Name}'. Clear the field in /relic config " +
                        "to silence this, or create that preset in BossMod Reborn.");
                    return BossModRebornRelicPreset.Name;
                }
            }
            catch { /* probe failed; try the configured name as-is */ }
        }
        return configured;
    }

    // Send "/bmrai on" or "/bmrai off" only when the desired state differs from what we last sent
    // (edge-triggered). Returns true when a command was actually issued (the state changed).
    // /bmrai is BMR's own AI command (AIManager.cs).
    private bool SetAi(bool on)
    {
        if (_aiSent == on)
            return false;
        try
        {
            ECommons.Automation.Chat.ExecuteCommand(on ? "/bmrai on" : "/bmrai off");
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Warn($"BossMod Reborn backend: '/bmrai {(on ? "on" : "off")}' failed: {ex.Message}");
        }
        _aiSent = on;
        return true;
    }

    // True when a preset name looks like an AI/movement preset rather than a rotation-only one -- the
    // known-bad choice for the combat preset. "VBM Multibox" is BMR's shipped AI preset; reusing the
    // avoidance preset name here is the same mistake. Relicable's own shipped preset never matches.
    private bool LooksLikeAiPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return false;
        if (presetName.Contains("Multibox", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(presetName, _config.BossModRebornAvoidancePreset, StringComparison.OrdinalIgnoreCase);
    }

    private static T? TrySub<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }
}
