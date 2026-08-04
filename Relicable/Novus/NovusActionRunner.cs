using System;
using System.Collections.Generic;
using System.Linq;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Novus;

// Runs the two Novus popout actions -- Infuse Sphere Scroll and Fetch from Retainer --
// independently of the main RelicController, so the planner is a self-contained tool
// that does not require starting the whole automation. It is ticked every framework
// update by the plugin; when Idle it does nothing.
//
// Both actions work from the planner's progress-aware route (only the remaining
// melds). Infuse drives the live meld window (RelicMeld); Fetch hands the route's
// materia to the shared RetainerFetchRunner, which drives the summoning bell. Progress
// is judged by real inventory changes, so an ineffective live-UI call stops fast rather
// than spinning.
public sealed class NovusActionRunner
{
    public enum Mode { Idle, Infusing, Fetching }

    // Infusing is two ticks per meld (select, then confirm), so keep the cadence snappy.
    private const long ActionCooldownMs = 600;
    private const long InfuseTimeoutMs = 600_000;

    private readonly Configuration _config;
    private readonly MateriaPlanner _planner;
    private readonly RetainerFetchRunner _fetch;

    private readonly List<WorkLine> _work = new();
    private readonly Dictionary<uint, int> _needByItem = new();

    private const long StuckTimeoutMs = 20_000;

    private int _lastInfuseTotal = -1;   // scroll's infused count last read (progress detection)
    private int _lastMax = 75;
    private long _lastProgressTicks;     // when a meld last landed
    private long _lastAction, _startTicks;

    private int Shown() => _lastInfuseTotal >= 0 ? _lastInfuseTotal : 0;

    private readonly record struct WorkLine(uint ItemId, MateriaType Type, int Grade, int Melds);

    public NovusActionRunner(Configuration config, MateriaPlanner planner, AutoRetainerIpc? autoRetainer = null)
    {
        _config = config;
        _planner = planner;
        _fetch = new RetainerFetchRunner(config, "Novus", autoRetainer);
    }

    public Mode Current { get; private set; } = Mode.Idle;
    public string Status { get; private set; } = "Idle";
    public bool Busy => Current != Mode.Idle;

    public void StartInfuse()
    {
        BuildPlan();
        if (_work.Count == 0)
        {
            Status = "Nothing to infuse: the route is empty (scroll complete, or 'Already infused' is at the cap).";
            DebugLog.Warn("Novus Infuse: route is empty, nothing to do (check 'Already infused' and that prices loaded).");
            return;
        }
        _lastInfuseTotal = -1;
        _lastMax = 75;
        _startTicks = Environment.TickCount64;
        _lastProgressTicks = Environment.TickCount64;
        _lastAction = 0;
        Current = Mode.Infusing;
        Status = "Infusing: open your Sphere Scroll's melding window while holding the route's materia.";
        // Warn level so it shows even without the debug log enabled.
        DebugLog.Warn($"Novus Infuse started ({_work.Count} route lines). Open the RelicSphereScroll window; it will infuse the materia you hold, in route order.");
    }

    public void StartFetch()
    {
        BuildPlan();
        if (_needByItem.Count == 0)
        {
            Status = "Nothing to fetch (route empty or already in your bags).";
            DebugLog.Warn("Novus Fetch: nothing to fetch (route empty, or all route materia are already in your bags).");
            return;
        }

        // The shared engine owns the bell drive; this only supplies the route's materia, the
        // materia naming, and the materia cache to report retainer locations from.
        if (!_fetch.Start(_needByItem, "the route's materia", MateriaName,
                () => _config.RetainerMateria.Retainers.Values
                    .Select(r => new RetainerFetchRunner.RetainerStock(r.RetainerName, r.Materia))))
        {
            Status = _fetch.Status;
            return;
        }
        Current = Mode.Fetching;
        Status = _fetch.Status;
    }

    public void Stop(string status = "Idle")
    {
        _fetch.Stop(status);
        Current = Mode.Idle;
        Status = status;
        _work.Clear();
        _needByItem.Clear();
    }

    public void Tick()
    {
        // Runs in EVERY mode, including Idle: the scroll's infused counter is only readable while its
        // window is open, so whenever it is, record it. That is what lets a scroll melded entirely by
        // hand still be seen at 75/75 -- and 75/75 is what sends the run to Jalzahn.
        NovusScrollState.Observe(_config, () => Plugin.PluginInterface.SavePluginConfig(_config));

        switch (Current)
        {
            case Mode.Infusing:
                TickInfuse();
                break;
            case Mode.Fetching:
                _fetch.Tick();
                Status = _fetch.Status;
                if (!_fetch.Busy)
                    Current = Mode.Idle; // the engine finished (or timed out) on its own
                break;
        }
    }

    private void BuildPlan()
    {
        _work.Clear();
        _needByItem.Clear();
        _planner.EnsurePrices();
        var route = _planner.ComputeRoute();
        foreach (var scroll in route.Scrolls)
        foreach (var line in scroll.Lines)
        {
            var id = MateriaCatalog.ItemId(line.Type, line.Grade);
            if (id == 0 || line.SuccessfulMelds <= 0)
                continue;
            _work.Add(new WorkLine(id, line.Type, line.Grade, line.SuccessfulMelds));
            _needByItem[id] = _needByItem.GetValueOrDefault(id) + line.StockToBuy;
        }
    }

    private void TickInfuse()
    {
        if (Environment.TickCount64 - _startTicks > InfuseTimeoutMs) { Stop("Infuse timed out."); return; }
        if (Environment.TickCount64 - _lastAction < ActionCooldownMs)
            return;
        _lastAction = Environment.TickCount64;

        // 1. Confirm any open Yes/No prompt -- this completes a pending infusion.
        if (RelicMeld.TryConfirmYesNo())
        {
            Status = $"Confirming infusion ({Shown()}/{_lastMax})";
            return;
        }

        // 2. The game closes the window after each infusion; re-open it to continue.
        if (!RelicMeld.IsScrollOpen())
        {
            if (Environment.TickCount64 - _lastProgressTicks > StuckTimeoutMs)
            {
                DebugLog.Warn("Novus Infuse: the RelicSphereScroll window could not be (re)opened. Is a 'Sphere Scroll' in your bags? Open-window list:");
                RelicMeld.LogOpenWindows();
                Stop("Could not open the Sphere Scroll window (is the scroll in your inventory?).");
                return;
            }
            Status = RelicMeld.TryOpenScroll()
                ? $"Re-opening the Sphere Scroll ({Shown()}/{_lastMax})..."
                : "No Sphere Scroll found in your bags; open its melding window manually.";
            return;
        }

        // 3. Window open: read completion + progress from the real infused count.
        if (RelicMeld.TryReadInfuseTotal(out var cur, out var m))
        {
            _lastMax = m;
            if (m > 0 && cur >= m)
            {
                // The scroll is finished and will be turned in; drop just THIS scroll's per-stat
                // progress (identified by its max points, so Paladin's other scroll is untouched) so a
                // fresh scroll of the same profile starts from zero. It is persisted and not otherwise
                // reset, so clear it here and save.
                var doneSpec = MateriaCatalog.GetScrolls(_config.NovusWeapon).FirstOrDefault(s => s.TotalPoints == m);
                if (doneSpec != null)
                {
                    _config.ScrollProgressByScroll.Remove(doneSpec.Name);
                    // The per-stat block above is wiped, so the authoritative counter is the only
                    // record that this scroll finished -- and it is what the Novus enhancement
                    // objective reads to decide the run may go to Jalzahn.
                    NovusScrollState.Record(_config, doneSpec.Name, cur, m);
                }
                Plugin.PluginInterface.SavePluginConfig(_config);
                Stop($"Scroll complete -- {cur}/{m} infused. Press Start to hand it to Jalzahn for the Novus enhancement.");
                return;
            }
            if (_lastInfuseTotal >= 0 && cur > _lastInfuseTotal)
                _lastProgressTicks = Environment.TickCount64; // a meld landed
            _lastInfuseTotal = cur;
        }

        if (Environment.TickCount64 - _lastProgressTicks > StuckTimeoutMs)
        {
            DebugLog.Warn($"Novus Infuse: no meld in {StuckTimeoutMs / 1000}s ({Shown()}/{_lastMax}). A prompt may be unhandled, or you are out of the route's materia. Open-window list:");
            RelicMeld.LogOpenWindows();
            Stop("Infuse made no progress (prompt unhandled or out of materia). See the log.");
            return;
        }

        // 4. Infuse the next materia you hold that the game allows: route order first,
        // then anything held + selectable so existing materia still makes progress.
        foreach (var line in _work)
        {
            if (GameState.InventoryCount(line.ItemId) <= 0)
                continue;
            if (RelicMeld.TryAttachOne(line.ItemId, line.Type, line.Grade))
            {
                Status = $"Infusing {Label(line)} ({Shown()}/{_lastMax})";
                return;
            }
        }
        if (RelicMeld.TryInfuseHeldSelectable())
        {
            Status = $"Infusing held materia ({Shown()}/{_lastMax})";
            return;
        }

        Status = "Looking for an infusable materia (holding the route's materia?)...";
    }

    private static string MateriaName(uint itemId)
        => MateriaCatalog.TryResolve(itemId, out var t, out var g)
            ? $"{MateriaCatalog.MateriaBaseName(t)} {Roman(g)}"
            : $"item {itemId}";

    private static string Label(WorkLine line)
        => $"{MateriaCatalog.MateriaBaseName(line.Type)} {Roman(line.Grade)}";

    private static string Roman(int g) => g switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => g.ToString() };
}
