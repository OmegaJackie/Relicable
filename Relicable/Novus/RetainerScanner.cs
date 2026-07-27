using System;
using System.Collections.Generic;
using System.Linq;
using Relicable.BaseRelic;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Novus;

// Keeps the persisted retainer materia cache fresh. AutoRetainer's IPC cannot report
// item-level retainer inventory, so whenever a retainer is open at the summoning bell
// (whether the player opened it or AutoRetainer's loop did), this scans the retainer's
// bags for catalog materia and records the counts. The cache then lets the Novus
// planner show "available from retainers" even while offline.
//
// Ticked every framework update from the plugin, independent of whether automation is
// running. It is cheap: a 500 ms throttle, a single scan per retainer-open, and a
// debounced config save only when something changed.
public sealed class RetainerScanner
{
    private const long ScanThrottleMs = 500;
    private const long SaveDebounceMs = 4000;

    private readonly Configuration _config;
    private readonly Action _saveConfig;

    private long _lastScanTicks;
    private long _lastSaveTicks;
    private ulong _lastScannedRetainer;
    private bool _dirty;

    public RetainerScanner(Configuration config, Action saveConfig)
    {
        _config = config;
        _saveConfig = saveConfig;
    }

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastScanTicks < ScanThrottleMs)
        {
            FlushIfDue(now);
            return;
        }
        _lastScanTicks = now;

        if (!GameState.IsRetainerInventoryOpen())
        {
            _lastScannedRetainer = 0; // reopening later rescans
            FlushIfDue(now);
            return;
        }

        if (!GameState.TryGetActiveRetainer(out var id, out var name))
            return;

        // Scan each retainer once per open session, plus a re-scan if either tracked
        // map changed (e.g. after an auto-withdraw removed some). Both the Novus materia
        // catalog and the base-relic material catalog are scanned in this one bag pass.
        var materiaIds = MateriaCatalog.AllMateriaItemIds().ToList();
        var baseRelicIds = BaseRelicCatalog.AllMaterialItemIds().ToList();
        var atmaIds = GameState.AtmaItemIds;
        if (materiaIds.Count == 0 && baseRelicIds.Count == 0 && atmaIds.Count == 0)
            return;

        var foundMateria = materiaIds.Count > 0
            ? GameState.ScanOpenRetainerItems(materiaIds)
            : new Dictionary<uint, int>();
        var foundBaseRelic = baseRelicIds.Count > 0
            ? GameState.ScanOpenRetainerItems(baseRelicIds)
            : new Dictionary<uint, int>();
        // The twelve atmas are scanned in the same bag pass so the Atma tracker can show
        // retainer-held atmas offline, just like the Novus materia / base-relic caches.
        var foundAtmas = atmaIds.Count > 0
            ? GameState.ScanOpenRetainerItems(atmaIds)
            : new Dictionary<uint, int>();

        var existingMateria = _config.RetainerMateria.Retainers.GetValueOrDefault(id);
        var existingBaseRelic = _config.RetainerBaseRelicItems.Retainers.GetValueOrDefault(id);
        var existingAtmas = _config.RetainerAtmas.Retainers.GetValueOrDefault(id);
        var unchanged = id == _lastScannedRetainer
            && existingMateria != null && SameCounts(existingMateria.Materia, foundMateria)
            && existingBaseRelic != null && SameCounts(existingBaseRelic.Items, foundBaseRelic)
            && existingAtmas != null && SameCounts(existingAtmas.Items, foundAtmas);
        if (unchanged)
            return;

        var owner = GameState.OwnerContentId();
        var scannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _config.RetainerMateria.Upsert(new RetainerMateriaSnapshot
        {
            RetainerId = id,
            RetainerName = name,
            OwnerContentId = owner,
            ScannedAtUnix = scannedAt,
            Materia = foundMateria,
        });
        _config.RetainerBaseRelicItems.Upsert(new RetainerItemSnapshot
        {
            RetainerId = id,
            RetainerName = name,
            OwnerContentId = owner,
            ScannedAtUnix = scannedAt,
            Items = foundBaseRelic,
        });
        _config.RetainerAtmas.Upsert(new RetainerItemSnapshot
        {
            RetainerId = id,
            RetainerName = name,
            OwnerContentId = owner,
            ScannedAtUnix = scannedAt,
            Items = foundAtmas,
        });
        _lastScannedRetainer = id;
        _dirty = true;
        DebugLog.Verbose($"Scanned retainer '{name}': {foundMateria.Values.Sum()} materia, " +
                         $"{foundBaseRelic.Values.Sum()} base-relic mats, {foundAtmas.Values.Sum()} atmas");

        FlushIfDue(now);
    }

    private void FlushIfDue(long now)
    {
        if (_dirty && now - _lastSaveTicks >= SaveDebounceMs)
        {
            _saveConfig();
            _dirty = false;
            _lastSaveTicks = now;
        }
    }

    private static bool SameCounts(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value)
                return false;
        return true;
    }
}
