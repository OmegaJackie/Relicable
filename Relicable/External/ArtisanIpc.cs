using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Relicable.External;

// Best-effort wrapper over Artisan's IPC (PunishXIV/Artisan, Artisan/IPC/IPC.cs). Used by
// the Braves planner to optionally craft the eight HQ quest items the player opts to make
// instead of buying on the market board.
//
// VERIFIED against Artisan IPC/IPC.cs (Init registers):
//   Artisan.CraftItem(ushort recipeId, int amount) -> void   (queues an Endurance craft)
//   Artisan.IsBusy() -> bool
//
// Every call is guarded: a missing or signature-changed gate degrades to "unavailable"
// rather than throwing, the same posture as the other IPC wrappers (AutoDutyIpc, etc.).
public sealed class ArtisanIpc
{
    private readonly ICallGateSubscriber<ushort, int, object>? _craftItem;
    private readonly ICallGateSubscriber<bool>? _isBusy;
    private readonly IDalamudPluginInterface _pi;

    public ArtisanIpc(IDalamudPluginInterface pi)
    {
        _pi = pi;
        _craftItem = TrySub(() => pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem"));
        _isBusy = TrySub(() => pi.GetIpcSubscriber<bool>("Artisan.IsBusy"));
    }

    // Artisan is usable: either its craft IPC action is live, or the plugin is installed and loaded.
    // CraftItem registers as a CallGate Action, so it is probed with HasAction; HasFunction is always
    // false for an Action -- the original symptom that made Available fall back to the plugin list.
    public bool Available => (_craftItem?.HasAction ?? false) || InstalledAndLoaded;

    // Artisan present and loaded, independent of IPC registration timing.
    private bool InstalledAndLoaded
    {
        get
        {
            try
            {
                foreach (var p in _pi.InstalledPlugins)
                    if (p.IsLoaded && string.Equals(p.InternalName, "Artisan", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { /* ignore */ }
            return false;
        }
    }

    // True while Artisan is mid-craft / has queued tasks. Best-effort: a missing gate
    // reports not busy so the UI stays responsive.
    public bool IsBusy()
    {
        if (_isBusy is not { HasFunction: true })
            return false;
        try { return _isBusy.InvokeFunc(); }
        catch { return false; }
    }

    // Queue Artisan to craft `amount` of recipe `recipeId` via its Endurance/IPC path.
    // Returns false (and does nothing) when Artisan is unavailable or the inputs are bad.
    public bool CraftItem(ushort recipeId, int amount)
    {
        if (recipeId == 0 || amount <= 0 || _craftItem is not { HasAction: true })
            return false;
        try
        {
            _craftItem.InvokeAction(recipeId, amount);
            Diagnostics.DebugLog.Info($"Artisan -> CraftItem recipe={recipeId} x{amount}");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Warn($"Artisan CraftItem failed: {ex.Message}");
            return false;
        }
    }

    private static T? TrySub<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }
}
