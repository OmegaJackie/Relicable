using System;
using System.Collections.Generic;
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Diagnostics;
using Relicable.External;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Steps;

// Drives the summoning bell to pull a caller-supplied set of items out of the player's
// retainers and into their bags.
//
// This is the engine the Novus "Fetch from Retainer" action grew up as; it is factored out
// here because nothing about it is materia-specific -- the Braves shopping list wants the
// same behaviour for its own materials, per item or per group. The caller supplies WHAT to
// fetch (item id -> the total it wants held), how to name an id for the status line, and the
// cached retainer snapshots used for the "which retainer holds what" hint. Everything else
// -- cycling retainers, opening the item window, issuing the native retrieve, backing out of
// the UI cleanly -- lives here.
//
// Two modes, selected by Configuration.AutoWithdrawFromRetainers:
//   on  -- Relicable drives the bell itself, visiting each retainer once and retrieving
//          every wanted stack it holds.
//   off -- nothing is moved; the open retainer is scanned and the status line lists what
//          to drag out by hand.
//
// Progress is judged by real inventory changes (a retrieve is not considered done until its
// stack lands in the bags), so an ineffective live-UI call stops fast rather than spinning.
public sealed class RetainerFetchRunner
{
    // One cached retainer's tracked contents, for the "in retainers: ..." hint. Callers
    // project their own snapshot type (RetainerMateriaSnapshot / RetainerItemSnapshot) onto
    // this so the runner does not need to know which cache it is reading.
    public readonly record struct RetainerStock(string Name, IReadOnlyDictionary<uint, int> Items);

    private const long FetchTimeoutMs = 300_000;

    // Auto-fetch (retainer -> player) pacing: one native retrieve per tick, and do not issue
    // the next until the previous stack has landed in the bags (or timed out), so back-to-back
    // retainer commands cannot desync the session (mirrors AutoRetainer).
    private const long RetrieveThrottleMs = 400;
    private const long RetrieveLandTimeoutMs = 5000;

    // The retainer action-menu entry that opens the item-transfer window (Addon sheet row 2378).
    private const string EntrustItemsNeedle = "Entrust or withdraw items";
    // The retainer action-menu entry that dismisses the retainer back to the bell list (Addon row 2383).
    // Hiding the Retainer agent only closes the item window BACK TO this action menu, so leaving a
    // retainer takes two steps -- hide, then Quit -- exactly as AutoRetainer's scheduler does.
    private const string QuitNeedle = "Quit";

    private enum RetainerListState { NotReady, Selected, Exhausted }

    // Only one fetch may drive the bell at a time: two runners taking turns on the same
    // retainer UI would fight over the menus (the Novus panel and the Braves panel each own
    // an instance). Starting one stops whichever was running.
    private static RetainerFetchRunner? _active;

    private readonly Configuration _config;
    private readonly AutoRetainerIpc? _autoRetainer;
    private readonly string _label;

    private readonly Dictionary<uint, int> _needByItem = new();

    // Multi-retainer bell drive (AutoRetainer style): the retainers already emptied this run, so the
    // bell loop opens each retainer exactly once; the retainer currently being processed (for identity
    // when the game's active-retainer read is momentarily unavailable); and whether WE suppressed
    // AutoRetainer for this run (so Stop only restores what we changed).
    private readonly HashSet<string> _pulledFrom = new(StringComparer.Ordinal);
    private string _currentRetainer = string.Empty;
    private bool _suppressedAr;

    private Func<uint, string> _nameOf = id => $"item {id}";
    private Func<IEnumerable<RetainerStock>> _stock = () => Array.Empty<RetainerStock>();
    private string _what = "items";

    private long _startTicks, _lastAction, _lastRetrieveTicks;
    private uint _pendingRetrieveItem;
    private int _pendingRetrieveBefore;

    // label names the caller in log lines ("Novus", "Braves"); it never reaches the UI.
    public RetainerFetchRunner(Configuration config, string label, AutoRetainerIpc? autoRetainer = null)
    {
        _config = config;
        _label = label;
        _autoRetainer = autoRetainer;
    }

    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Idle";

    // Begin a fetch. needByItem maps an item id to the TOTAL the caller wants held in the
    // player's bags (not the shortfall) -- what is already held is subtracted live, so the run
    // finishes as soon as the bags reach the target. what is a short human phrase for the set
    // ("the route's materia", "4x Sacred Spring Water") used in the status line. Returns false,
    // without starting, when there is nothing outstanding.
    public bool Start(IReadOnlyDictionary<uint, int> needByItem, string what,
                      Func<uint, string> nameOf, Func<IEnumerable<RetainerStock>> stock)
    {
        _needByItem.Clear();
        foreach (var kv in needByItem)
            if (kv.Key != 0 && kv.Value > 0)
                _needByItem[kv.Key] = kv.Value;

        _nameOf = nameOf;
        _stock = stock;
        _what = string.IsNullOrWhiteSpace(what) ? "items" : what;

        if (_needByItem.Count == 0 || !AnyOutstanding())
        {
            Status = $"Nothing to fetch ({_what} already in your bags).";
            return false;
        }

        // Another panel's fetch is mid-run: it owns the bell UI, so end it before taking over.
        if (_active != null && !ReferenceEquals(_active, this))
            _active.Stop("Stopped: another retainer fetch took over.");
        _active = this;

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

        Busy = true;
        Status = _config.AutoWithdrawFromRetainers
            ? $"Fetch: open the summoning bell and {_what} will be pulled from every retainer."
            : "Fetch: open a retainer at the bell to list what to withdraw.";
        DebugLog.Warn($"{_label} Fetch started ({_needByItem.Count} item type(s) needed: {_what}). " +
                      (_config.AutoWithdrawFromRetainers
                          ? "Open the summoning bell; Relicable will cycle through every retainer and pull them."
                          : "Open a retainer at the summoning bell; it will list what to drag out."));
        return true;
    }

    public void Stop(string status = "Idle")
    {
        if (_suppressedAr)
        {
            _autoRetainer?.SetSuppressed(false);
            _suppressedAr = false;
        }
        if (ReferenceEquals(_active, this))
            _active = null;
        Busy = false;
        Status = status;
        _needByItem.Clear();
        _pulledFrom.Clear();
        _currentRetainer = string.Empty;
        _pendingRetrieveItem = 0;
    }

    public void Tick()
    {
        if (!Busy)
            return;
        if (Environment.TickCount64 - _startTicks > FetchTimeoutMs) { Stop("Fetch timed out."); return; }

        // Wait for a just-issued retrieve to actually land (its bag count rose) or time
        // out before issuing the next, so back-to-back native commands cannot desync the
        // retainer session.
        if (_pendingRetrieveItem != 0)
        {
            var landed = GameState.InventoryCount(_pendingRetrieveItem) > _pendingRetrieveBefore;
            if (!landed && Environment.TickCount64 - _lastRetrieveTicks < RetrieveLandTimeoutMs)
            {
                Status = $"Retrieving {_nameOf(_pendingRetrieveItem)}...";
                return;
            }
            _pendingRetrieveItem = 0;
        }

        // Auto-pull off: guide the player to drag items out of the open retainer (finish when stocked).
        if (!_config.AutoWithdrawFromRetainers)
        {
            if (!AnyOutstanding()) { Stop($"Fetch complete -- {_what} are in your bags."); return; }
            ReportManualDrag();
            return;
        }

        // Human-ish spacing between UI actions (select / entrust / retrieve / dismiss).
        if (Environment.TickCount64 - _lastAction < RetrieveThrottleMs)
            return;
        _lastAction = Environment.TickCount64;

        // Fully stocked: back OUT of the retainer UI before finishing, one level per tick. Leaving a
        // retainer is two steps (hide the item window back to the action menu, then Quit to the list),
        // then close the bell list -- otherwise the run ends one menu too deep.
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
            Stop($"Fetch complete -- {_what} are in your bags.");
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
                    Status = $"Retrieving {_nameOf(itemId)} " +
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
        //    here, not to the list). Otherwise open its item window.
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
                    ? $"Checked every retainer -- still short on some of {_what}; buy or farm the shortfall."
                    : $"Fetch complete -- {_what} are in your bags.");
                return;
        }

        // D) Nothing recognized open. Distinguish "between retainers" from "not at a bell yet".
        Status = _pulledFrom.Count > 0
            ? "Moving to the next retainer..."
            : $"Open the summoning bell and {_what} will be pulled from every retainer. " + RetainerLocationsHint();
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

        var held = GameState.ScanOpenRetainerItems(new List<uint>(_needByItem.Keys));
        var parts = new List<string>();
        foreach (var kv in _needByItem)
        {
            var stillNeed = kv.Value - GameState.InventoryCount(kv.Key);
            if (stillNeed <= 0)
                continue;
            var have = held.GetValueOrDefault(kv.Key);
            if (have <= 0)
                continue;
            parts.Add($"{Math.Min(have, stillNeed)}x {_nameOf(kv.Key)}");
        }

        Status = parts.Count == 0
            ? "This retainer has nothing to pull for this list -- open another retainer, or press Stop."
            : "Drag from this retainer: " + string.Join(", ", parts);
    }

    // From the caller's cached retainer scans, which retainers hold what is still needed, so the
    // player knows which bell/retainer to open.
    private string RetainerLocationsHint()
    {
        var parts = new List<string>();
        foreach (var r in _stock())
        {
            var items = new List<string>();
            foreach (var kv in _needByItem)
            {
                if (GameState.InventoryCount(kv.Key) >= kv.Value)
                    continue;
                var have = r.Items.TryGetValue(kv.Key, out var n) ? n : 0;
                if (have > 0)
                    items.Add($"{have}x {_nameOf(kv.Key)}");
            }
            if (items.Count > 0)
                parts.Add($"{r.Name} ({string.Join(", ", items)})");
        }
        return parts.Count == 0 ? string.Empty : "In retainers: " + string.Join("; ", parts) + ".";
    }
}
