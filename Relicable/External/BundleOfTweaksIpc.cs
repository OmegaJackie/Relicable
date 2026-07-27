using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Relicable.External;

// Integration facade for Croizat's Bundle of Tweaks (CBT; plugin InternalName "Automaton",
// repo Jaksuhn/ffxiv-bundleoftweaks). CBT ships a self-contained "Atma (Zodiac)" FATE grind
// mode in its Fate Tool Kit tweak, which Relicable delegates the Atma stage to.
//
// CBT's IPC surface is minimal -- only enable/disable a tweak by class name:
//   Automaton.IsTweakEnabled(string className) -> bool
//   Automaton.SetTweakState(string className, bool state) -> void
// There is NO IPC to select the grind mode or start/stop a run. So the grind itself is driven
// by CBT's /dwd command, sent through the game chat (ECommons.Chat) -- the same mechanism the
// BossMod Reborn backend uses for /bmrai. The "Atma (Zodiac)" MODE is UI-only in CBT and cannot be set
// from here, so the user selects it once in CBT's Fate Tool Kit window. See
// docs/CBT-FateToolKit-IPC-request.md for the upstream IPC that would remove that manual step.
//
// Everything degrades safely: if CBT is absent (gates missing) the tweak-enable no-ops and the
// /dwd commands do nothing, so a missing/renamed CBT never throws.
public sealed class BundleOfTweaksIpc
{
    // EzIPC.Init(this) in CBT registers gates under the plugin's InternalName ("Automaton",
    // confirmed in Automaton.csproj). If CBT ever renames its InternalName these gates go absent
    // and the calls below no-op -- the /dwd path still works once the tweak is enabled by hand.
    private const string Prefix = "Automaton";

    private readonly ICallGateSubscriber<string, bool>? _isTweakEnabled;
    private readonly ICallGateSubscriber<string, bool, object>? _setTweakState;

    public BundleOfTweaksIpc(IDalamudPluginInterface pi)
    {
        _isTweakEnabled = TrySub(() => pi.GetIpcSubscriber<string, bool>($"{Prefix}.IsTweakEnabled"));
        _setTweakState = TrySub(() => pi.GetIpcSubscriber<string, bool, object>($"{Prefix}.SetTweakState"));
    }

    // True when CBT's IPC is present (plugin loaded and the gate registered).
    public bool Available => _isTweakEnabled?.HasFunction ?? false;

    public bool IsTweakEnabled(string className)
    {
        if (_isTweakEnabled is not { HasFunction: true })
            return false;
        try { return _isTweakEnabled.InvokeFunc(className); }
        catch { return false; }
    }

    // Best-effort: enable a CBT tweak by class name so its command surface (/dwd) is registered.
    // SetTweakState is a void IPC, so it registers as a CallGate Action (probe with HasAction).
    public void EnsureTweakEnabled(string className)
    {
        if (_setTweakState is not { HasAction: true })
            return;
        try
        {
            if (!IsTweakEnabled(className))
                _setTweakState.InvokeAction(className, true);
        }
        catch { /* best-effort */ }
    }

    // Best-effort: disable a CBT tweak by class name (inverse of EnsureTweakEnabled). Used to clean
    // up a tweak Relicable itself enabled for a delegated grind so it does not linger afterwards.
    public void DisableTweak(string className)
    {
        if (_setTweakState is not { HasAction: true })
            return;
        try
        {
            if (IsTweakEnabled(className))
                _setTweakState.InvokeAction(className, false);
        }
        catch { /* best-effort */ }
    }

    // Start CBT's Fate Tool Kit grind for up to 'fateCap' fates. CBT stops earlier when the
    // selected mode's target is met (all 12 Atmas for "Atma (Zodiac)"), and Relicable stops it
    // on the 12th atma regardless, so fateCap is only a runaway safety bound. Sent as the /dwd
    // command because CBT provides no IPC to start a run.
    public void StartFateGrind(int fateCap)
        => ECommons.Automation.Chat.ExecuteCommand($"/dwd run {fateCap}");

    public void StopFateGrind()
        => ECommons.Automation.Chat.ExecuteCommand("/dwd stop");

    private static T? TrySub<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }
}
