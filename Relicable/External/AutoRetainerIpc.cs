using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Relicable.Diagnostics;

namespace Relicable.External;

// One retainer as reported by AutoRetainer's offline character data.
public readonly record struct RetainerRef(ulong OwnerContentId, string OwnerName, ulong RetainerId, string Name);

// Hardened wrapper over AutoRetainer's IPC (verified against AutoRetainerAPI):
//   AutoRetainer.GetRegisteredCIDs        -> List<ulong>
//   AutoRetainer.GetOfflineCharacterData  -> OfflineCharacterData (per content id)
//   AutoRetainer.GetSuppressed / SetSuppressed -> bool / void
//
// IMPORTANT: AutoRetainer's IPC exposes retainer NAMES, gil, and venture state but
// NOT item-level inventory (OfflineRetainerData carries only an 'MBItems' count).
// So this wrapper is used to (a) enumerate retainers and (b) suppress AutoRetainer
// while Relicable drives the bell, NOT to read materia counts. Materia counts are
// scanned from the native retainer inventory in game memory (see GameState /
// RetainerScanner) and cached in Configuration.
//
// OfflineCharacterData is AutoRetainer's own type, loaded in its assembly-load
// context, so it cannot be referenced directly. Dalamud JSON-serializes mismatched
// IPC types across the boundary, so the subscriber declares local MIRROR classes
// carrying just the fields Relicable reads (extra provider fields are ignored);
// subscribing as 'object' instead yields a Newtonsoft JObject whose members field
// reflection cannot see, i.e. zero retainers. (Same mirror pattern GatherBuddy
// Reborn uses for this gate.) Every call is guarded by HasFunction and wrapped in
// try/catch so an absent or version-shifted AutoRetainer degrades to "no retainers".
public sealed class AutoRetainerIpc
{
    // Local mirrors of AutoRetainer's OfflineCharacterData / OfflineRetainerData.
    private sealed class OfflineCharacterDataMirror
    {
        public ulong CID { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<OfflineRetainerDataMirror> RetainerData { get; set; } = new();
    }

    private sealed class OfflineRetainerDataMirror
    {
        public string Name { get; set; } = string.Empty;
        public ulong RetainerID { get; set; }
    }

    private readonly ICallGateSubscriber<List<ulong>>? _getCids;
    private readonly ICallGateSubscriber<ulong, OfflineCharacterDataMirror?>? _getOffline;
    private readonly ICallGateSubscriber<bool>? _getSuppressed;
    private readonly ICallGateSubscriber<bool, object>? _setSuppressed;

    public AutoRetainerIpc(IDalamudPluginInterface pi)
    {
        _getCids = TrySub(() => pi.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs"));
        _getOffline = TrySub(() => pi.GetIpcSubscriber<ulong, OfflineCharacterDataMirror?>("AutoRetainer.GetOfflineCharacterData"));
        _getSuppressed = TrySub(() => pi.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed"));
        _setSuppressed = TrySub(() => pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed"));
    }

    public bool Available => _getCids?.HasFunction ?? false;

    // All known character content ids (excludes blacklisted / uninitialised).
    public IReadOnlyList<ulong> GetRegisteredCharacters()
    {
        if (_getCids is not { HasFunction: true })
            return Array.Empty<ulong>();
        try { return _getCids.InvokeFunc() ?? new List<ulong>(); }
        catch (Exception ex)
        {
            DebugLog.Warn($"AutoRetainer GetRegisteredCIDs failed: {ex.Message}");
            return Array.Empty<ulong>();
        }
    }

    // Every retainer across every registered character. Names only -- inventory is
    // read elsewhere (see class remarks).
    public IReadOnlyList<RetainerRef> GetAllRetainers()
    {
        var result = new List<RetainerRef>();
        if (_getOffline is not { HasFunction: true })
            return result;

        foreach (var cid in GetRegisteredCharacters())
        {
            OfflineCharacterDataMirror? data;
            try { data = _getOffline.InvokeFunc(cid); }
            catch (Exception ex)
            {
                DebugLog.Warn($"AutoRetainer GetOfflineCharacterData({cid}) failed: {ex.Message}");
                continue;
            }
            if (data?.RetainerData == null)
                continue;
            foreach (var r in data.RetainerData)
                if (r != null && !string.IsNullOrEmpty(r.Name))
                    result.Add(new RetainerRef(cid, data.Name ?? string.Empty, r.RetainerID, r.Name));
        }
        return result;
    }

    public bool IsSuppressed()
    {
        if (_getSuppressed is not { HasFunction: true })
            return false;
        try { return _getSuppressed.InvokeFunc(); }
        catch { return false; }
    }

    // Pause or resume AutoRetainer's own automation while Relicable uses the bell, so
    // the two do not fight over the retainer UI.
    public void SetSuppressed(bool value)
    {
        if (_setSuppressed is not { HasAction: true })
            return;
        try { _setSuppressed.InvokeAction(value); }
        catch (Exception ex) { DebugLog.Warn($"AutoRetainer SetSuppressed failed: {ex.Message}"); }
    }

    private static T? TrySub<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }
}
