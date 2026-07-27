using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.External.Ipc;

namespace Relicable.External;

// Hardened wrapper over Lifestream. VERIFIED against Questionable's LifestreamIpc:
//   Lifestream.AethernetTeleportById(uint) -> bool
//   Lifestream.IsBusy() -> bool
//
// IsBusy is polled while a travel step runs; it is cached (50 ms). The teleport
// command is naturally edge-triggered by the caller (issued once in a step's
// Start), and is HasFunction-guarded so an absent Lifestream is a safe no-op.
public sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<uint, bool>? _aethernetTeleportById;
    private readonly ICallGateSubscriber<bool>? _isBusy;
    private readonly Cached<bool> _busyCache;

    public LifestreamIpc(IDalamudPluginInterface pi)
    {
        _aethernetTeleportById = TrySub(() => pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById"));
        _isBusy = TrySub(() => pi.GetIpcSubscriber<bool>("Lifestream.IsBusy"));
        _busyCache = new Cached<bool>(ReadBusy, 50);
    }

    public bool Available => _aethernetTeleportById?.HasFunction ?? false;

    public bool AethernetTeleport(uint shardId)
    {
        if (_aethernetTeleportById is not { HasFunction: true })
            return false;
        try
        {
            var ok = _aethernetTeleportById.InvokeFunc(shardId);
            _busyCache.Invalidate();
            return ok;
        }
        catch { return false; }
    }

    public bool IsBusy() => _busyCache.Value;

    private bool ReadBusy()
    {
        if (_isBusy is not { HasFunction: true })
            return false;
        try { return _isBusy.InvokeFunc(); }
        catch { return false; }
    }

    private static ICallGateSubscriber<TR>? TrySub<TR>(Func<ICallGateSubscriber<TR>> f)
    {
        try { return f(); } catch { return null; }
    }

    private static ICallGateSubscriber<T1, TR>? TrySub<T1, TR>(Func<ICallGateSubscriber<T1, TR>> f)
    {
        try { return f(); } catch { return null; }
    }
}
