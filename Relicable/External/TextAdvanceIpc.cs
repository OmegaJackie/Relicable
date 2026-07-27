using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.External.Ipc;

namespace Relicable.External;

// Wrapper over TextAdvance. Relicable relies on TextAdvance running globally (its
// normal enabled state) to carry accept/turn-in/dialogue/cutscene prompts, rather
// than taking scoped external control. VERIFIED gates (TextAdvance IPCProvider.cs):
//   TextAdvance.IsEnabled() -> bool
//   TextAdvance.IsBusy() -> bool
//
// Enable/Disable are kept so executors can call them uniformly; Enable just reports
// whether TextAdvance is on (so a caller can warn), and Disable is a no-op because
// we never take control.
public sealed class TextAdvanceIpc
{
    private readonly ICallGateSubscriber<bool>? _isEnabled;
    private readonly ICallGateSubscriber<bool>? _isBusy;
    private readonly Cached<bool> _enabledCache;
    private readonly Cached<bool> _busyCache;

    public TextAdvanceIpc(IDalamudPluginInterface pi)
    {
        _isEnabled = TrySub(() => pi.GetIpcSubscriber<bool>("TextAdvance.IsEnabled"));
        _isBusy = TrySub(() => pi.GetIpcSubscriber<bool>("TextAdvance.IsBusy"));
        _enabledCache = new Cached<bool>(() => Read(_isEnabled), 100);
        _busyCache = new Cached<bool>(() => Read(_isBusy), 100);
    }

    public bool Available => _isEnabled?.HasFunction ?? false;

    public bool IsEnabled() => _enabledCache.Value;
    public bool IsBusy() => _busyCache.Value;

    // Confirm TextAdvance is on (global). Returns false if it is off so the caller
    // can warn; interaction steps depend on it.
    public bool Enable() => IsEnabled();

    // No-op: we use TextAdvance globally and never take scoped control.
    public void Disable() { }

    private static bool Read(ICallGateSubscriber<bool>? gate)
    {
        if (gate is not { HasFunction: true })
            return false;
        try { return gate.InvokeFunc(); }
        catch { return false; }
    }

    private static ICallGateSubscriber<TR>? TrySub<TR>(Func<ICallGateSubscriber<TR>> f)
    {
        try { return f(); } catch { return null; }
    }
}
