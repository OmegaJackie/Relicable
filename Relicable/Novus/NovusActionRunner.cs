using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Novus;

// Runs the two Novus popout actions -- Infuse Sphere Scroll and Fetch from Retainer --
// independently of the main RelicController, so the planner is a self-contained tool
// that does not require starting the whole automation. It is ticked every framework
// update by the plugin; when Idle it does nothing.
//
// Both actions work from the planner's progress-aware route (only the remaining
// melds). Infuse drives the live meld window (RelicMeld); Fetch moves the route's
// materia out of an open retainer (RetainerWithdraw). Progress is judged by real
// inventory changes, so an ineffective live-UI call stops fast rather than spinning.
public sealed class NovusActionRunner
{
    public enum Mode { Idle, Infusing, Fetching }

    // Infusing is two ticks per meld (select, then confirm), so keep the cadence snappy.
    private const long ActionCooldownMs = 600;
    private const long InfuseTimeoutMs = 600_000;
    private const long FetchTimeoutMs = 300_000;

    private readonly Configuration _config;
    private readonly MateriaPlanner _planner;
    private readonly AutoRetainerIpc? _autoRetainer;

    private readonly List<WorkLine> _work = new();
    private readonly Dictionary<uint, int> _needByItem = new();

    // Multi-retainer bell drive (AutoRetainer style): the retainers already emptied this run, so the
    // bell loop opens each retainer exactly once; the retainer currently being processed (for identity
    // when the game's active-retainer read is momentarily unavailable); and whether WE suppressed
    // AutoRetainer for this run (so Stop only restores what we changed).
    private readonly HashSet<string> _pulledFrom = new(StringComparer.Ordinal);
    private string _currentRetainer = string.Empty;
    private bool _suppressedAr;

    // The retainer action-menu entry that opens the item-transfer window (Addon sheet row 2378).
    private const string EntrustItemsNeedle = "Entrust or withdraw items";
    // The retainer action-menu entry that dismisses the retainer back to the bell list (Addon row 2383).
    // Hiding the Retainer agent only closes the item window BACK TO this action menu, so leaving a
    // retainer takes two steps -- hide, then Quit -- exactly as AutoRetainer's scheduler does.
    private const string QuitNeedle = "Quit";

    private enum RetainerListState { NotReady, Selected, Exhausted }

    private const long StuckTimeoutMs = 20_000;

    private int _lastInfuseTotal = -1;   // scroll's infused count last read (progress detection)
    private int _lastMax = 75;
    private long _lastProgressTicks;     // when a meld last landed
    private long _lastAction, _startTicks;

    // Auto-fetch (retainer -> player) pacing: one native retrieve per tick, and do not
    // issue the next until the previous stack has landed in the bags (or timed out), so
    // back-to-back retainer commands cannot desync the session (mirrors AutoRetainer).
    private const long RetrieveThrottleMs = 400;
    private const long RetrieveLandTimeoutMs = 5000;
    private long _lastRetrieveTicks;
    private uint _pendingRetrieveItem;
    private int _pendingRetrieveBefore;

    private int Shown() => _lastInfuseTotal >= 0 ? _lastInfuseTotal : 0;

    private readonly record struct WorkLine(uint ItemId, MateriaType Type, int Grade, int Melds);

    public NovusActionRunner(Configuration config, MateriaPlanner planner, AutoRetainerIpc? autoRetainer = null)
    {
        _config = config;
        _planner = planner;
        _autoRetainer = autoRetainer;
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
        _startTicks = Environment.TickCount64;
        _lastAction = 0;
        _pendingRetrieveItem = 0;
        _pulledFrom.Clear();
        _currentRetainer = string.Empty;
        // Pause AutoRetainer's own automation while we drive the bell, so the two do not fight over the
        // retainer UI. Only when it is not already suppressed by the user, and restore exactly that on Stop.
        if (_autoRetainer?.Available == true && !_autoRetainer.IsSuppressed())
        {
            _autoRetainer.SetSuppressed(true);
            _suppressedAr = true;
        }
        Current = Mode.Fetching;
        Status = _config.AutoWithdrawFromRetainers
            ? "Fetch: open the summoning bell and the route's materia will be pulled from every retainer."
            : "Fetch: open a retainer at the bell to list what to withdraw for the route.";
        DebugLog.Warn($"Novus Fetch started ({_needByItem.Count} materia types needed). " + (_config.AutoWithdrawFromRetainers
            ? "Open the summoning bell; Relicable will cycle through every retainer and pull the route materia."
            : "Open a retainer at the summoning bell; it will list what to drag out."));
    }

    public void Stop(string status = "Idle")
    {
        if (_suppressedAr)
        {
            _autoRetainer?.SetSuppressed(false);
            _suppressedAr = false;
        }
        Current = Mode.Idle;
        Status = status;
        _work.Clear();
        _needByItem.Clear();
        _pulledFrom.Clear();
        _currentRetainer = string.Empty;
        _pendingRetrieveItem = 0;
    }

    // Logs the open windows immediately (for the "Find infusion window" button),
    // independent of any route or whether you hold the materia.

    public void Tick()
    {
        switch (Current)
        {
            case Mode.Infusing: TickInfuse(); break;
            case Mode.Fetching: TickFetch(); break;
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
                    _config.ScrollProgressByScroll.Remove(doneSpec.Name);
                Plugin.PluginInterface.SavePluginConfig(_config);
                Stop($"Scroll complete -- {cur}/{m} infused.");
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

    private void TickFetch()
    {
        if (Environment.TickCount64 - _startTicks > FetchTimeoutMs) { Stop("Fetch timed out."); return; }

        // Wait for a just-issued retrieve to actually land (its bag count rose) or time
        // out before issuing the next, so back-to-back native commands cannot desync the
        // retainer session.
        if (_pendingRetrieveItem != 0)
        {
            var landed = GameState.InventoryCount(_pendingRetrieveItem) > _pendingRetrieveBefore;
            if (!landed && Environment.TickCount64 - _lastRetrieveTicks < RetrieveLandTimeoutMs)
            {
                Status = $"Retrieving {MateriaName(_pendingRetrieveItem)}...";
                return;
            }
            _pendingRetrieveItem = 0;
        }

        // Auto-pull off: guide the player to drag items out of the open retainer (finish when stocked).
        if (!_config.AutoWithdrawFromRetainers)
        {
            if (!AnyOutstanding()) { Stop("Fetch complete -- all route materia are in your bags."); return; }
            ReportManualDrag();
            return;
        }

        // Human-ish spacing between UI actions (select / entrust / retrieve / dismiss).
        if (Environment.TickCount64 - _lastAction < RetrieveThrottleMs)
            return;
        _lastAction = Environment.TickCount64;

        // Route fully stocked: fully back OUT of the retainer UI before finishing, one level per tick.
        // Leaving a retainer is two steps (hide the item window back to the action menu, then Quit to
        // the list), then close the bell list -- otherwise the run ended one menu too deep (the reported
        // "not fully exiting; needs to go back one more").
        if (!AnyOutstanding())
        {
            if (RetainerWithdraw.IsItemWindowOpen())
            {
                RetainerWithdraw.CloseRetainerAgent();
                Status = "Fetched everything; closing the retainer window...";
                return;
            }
            if (DialogueMenu.IsOpen("SelectString"))
            {
                DialogueMenu.SelectByTextSafe("SelectString", QuitNeedle);
                Status = "Fetched everything; leaving the retainer...";
                return;
            }
            if (DialogueMenu.IsOpen("RetainerList"))
                DialogueMenu.FireClose("RetainerList");
            Stop("Fetch complete -- all route materia are in your bags.");
            return;
        }

        var wanted = OutstandingItemIds();

        // A) A retainer's "Entrust or withdraw items" window is open: pull the next needed stack this
        //    retainer holds (one per tick); when it holds none, remember it and hide the item window
        //    (which returns to the action menu, where step B then Quits back to the bell list).
        if (RetainerWithdraw.IsItemWindowOpen())
        {
            if (wanted.Count > 0 && GameState.TryFindRetainerSlot(wanted, out var page, out var slot, out var itemId))
            {
                _pendingRetrieveItem = itemId;
                _pendingRetrieveBefore = GameState.InventoryCount(itemId);
                _lastRetrieveTicks = Environment.TickCount64;
                if (RetainerWithdraw.TryRetrieveSlot(page, slot))
                    Status = $"Retrieving {MateriaName(itemId)} " +
                             $"({GameState.InventoryCount(itemId)}/{_needByItem.GetValueOrDefault(itemId)})...";
                else
                {
                    _pendingRetrieveItem = 0;
                    Status = "Could not retrieve (retainer window not ready).";
                }
                return;
            }
            // This retainer holds nothing more we need -> mark it done and hide the item window
            // (back to the action menu; step B then Quits to the list).
            MarkCurrentRetainerDone();
            RetainerWithdraw.CloseRetainerAgent();
            Status = "This retainer is done; leaving it...";
            return;
        }

        // B) A retainer's action menu (SelectString) is open. If we have already emptied this retainer,
        //    select "Quit" to go back to the bell list (hiding the item window in step A only returns
        //    here, not to the list -- so this is the required "one more" step). Otherwise open its
        //    item window.
        if (DialogueMenu.IsOpen("SelectString"))
        {
            var who = ActiveRetainerName();
            if (who.Length > 0 && _pulledFrom.Contains(who))
            {
                DialogueMenu.SelectByTextSafe("SelectString", QuitNeedle);
                Status = "This retainer is done; returning to the list...";
                return;
            }
            DialogueMenu.SelectByTextSafe("SelectString", EntrustItemsNeedle);
            Status = "Opening 'Entrust or withdraw items'...";
            return;
        }

        // C) The summoning bell's retainer list is up: open the next retainer we have not pulled from.
        switch (TrySelectNextRetainer(out var opened))
        {
            case RetainerListState.Selected:
                _currentRetainer = opened;
                Status = $"Opening retainer {opened}...";
                return;
            case RetainerListState.Exhausted:
                Stop(AnyOutstanding()
                    ? "Checked every retainer -- still short on some materia; buy the shortfall (see the Novus list)."
                    : "Fetch complete -- all route materia are in your bags.");
                return;
        }

        // D) Nothing recognized open. Distinguish "between retainers" from "not at a bell yet".
        Status = _pulledFrom.Count > 0
            ? "Moving to the next retainer..."
            : "Open the summoning bell and the route's materia will be pulled from every retainer. "
              + RetainerLocationsHint();
    }

    // The name of the retainer currently being processed: the game's own active-retainer read
    // (authoritative while a retainer is summoned), falling back to the one we selected from the list.
    private string ActiveRetainerName()
        => GameState.TryGetActiveRetainer(out _, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n.Trim()
            : _currentRetainer;

    // Mark the retainer we are on as emptied, so the bell loop never re-opens it this run.
    private void MarkCurrentRetainerDone()
    {
        var who = ActiveRetainerName();
        if (who.Length > 0)
            _pulledFrom.Add(who);
        _currentRetainer = string.Empty;
    }

    // Drive the summoning bell's retainer list: select the first retainer we have not pulled from yet
    // (marking any that cannot be selected so the loop always advances). Returns NotReady when the list
    // is not up, Selected when a retainer was opened, Exhausted when every retainer has been visited.
    private RetainerListState TrySelectNextRetainer(out string name)
    {
        name = string.Empty;
        if (!TryGetAddonMaster<AddonMaster.RetainerList>("RetainerList", out var list) || !list.IsAddonReady)
            return RetainerListState.NotReady;
        foreach (var entry in list.Retainers)
        {
            var n = (entry.Name ?? string.Empty).Trim();
            if (n.Length == 0 || _pulledFrom.Contains(n))
                continue;
            if (entry.Select())
            {
                name = n;
                return RetainerListState.Selected;
            }
            _pulledFrom.Add(n); // could not select (e.g. unavailable) -> skip it for this run
        }
        return RetainerListState.Exhausted;
    }

    private bool AnyOutstanding()
    {
        foreach (var kv in _needByItem)
            if (GameState.InventoryCount(kv.Key) < kv.Value)
                return true;
        return false;
    }

    private List<uint> OutstandingItemIds()
    {
        var list = new List<uint>();
        foreach (var kv in _needByItem)
            if (GameState.InventoryCount(kv.Key) < kv.Value)
                list.Add(kv.Key);
        return list;
    }

    // The manual (auto-pull off) path: scan the open retainer and list what to drag out.
    private void ReportManualDrag()
    {
        if (!GameState.IsRetainerInventoryOpen())
        {
            Status = "Open a retainer at the summoning bell; this lists what to drag out. " + RetainerLocationsHint();
            return;
        }
        if (Environment.TickCount64 - _lastAction < 500)
            return;
        _lastAction = Environment.TickCount64;

        var held = GameState.ScanOpenRetainerMateria(new List<uint>(_needByItem.Keys));
        var parts = new List<string>();
        foreach (var kv in _needByItem)
        {
            var stillNeed = kv.Value - GameState.InventoryCount(kv.Key);
            if (stillNeed <= 0)
                continue;
            var have = held.GetValueOrDefault(kv.Key);
            if (have <= 0)
                continue;
            parts.Add($"{Math.Min(have, stillNeed)}x {MateriaName(kv.Key)}");
        }

        Status = parts.Count == 0
            ? "This retainer has no route materia to pull -- open another retainer, or press Stop."
            : "Drag from this retainer: " + string.Join(", ", parts);
    }

    // From the cached retainer scans, which retainers hold materia the route still needs,
    // so the player knows which to open (the fetch only pulls from the currently-open one).
    private string RetainerLocationsHint()
    {
        var parts = new List<string>();
        foreach (var r in _config.RetainerMateria.Retainers.Values)
        {
            var items = new List<string>();
            foreach (var kv in _needByItem)
            {
                if (GameState.InventoryCount(kv.Key) >= kv.Value)
                    continue;
                var have = r.Materia.GetValueOrDefault(kv.Key);
                if (have > 0)
                    items.Add($"{have}x {MateriaName(kv.Key)}");
            }
            if (items.Count > 0)
                parts.Add($"{r.RetainerName} ({string.Join(", ", items)})");
        }
        return parts.Count == 0 ? string.Empty : "In retainers: " + string.Join("; ", parts) + ".";
    }

    private static string MateriaName(uint itemId)
        => MateriaCatalog.TryResolve(itemId, out var t, out var g)
            ? $"{MateriaCatalog.MateriaBaseName(t)} {Roman(g)}"
            : $"item {itemId}";

    private static string Label(WorkLine line)
        => $"{MateriaCatalog.MateriaBaseName(line.Type)} {Roman(line.Grade)}";

    private static string Roman(int g) => g switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => g.ToString() };
}
