using System;
using System.Collections.Generic;
using System.Linq;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps;

// Novus stat allocation: attach materia to the Novus weapon to fill its stats.
//
// FFXIVClientStructs does not expose the per-stat allocation of the ARR Novus
// weapon, so completion cannot be read from the stat bars directly. Instead it is
// tracked by the materia actually consumed from the inventory, which IS readable
// and reliable: the step finishes once `Count` materia (item `ItemId`) have been
// attached. If the player runs out of materia first, the step fails so the user
// can restock.
//
// The one seam that needs the live UI is attaching a single materia
// (RelicMeld.TryAttachOne). The completion logic is independent of how that
// attach is performed, so the loop is correct regardless.
public sealed class MeldMateriaExecutor : ITaskExecutor
{
    public StepType Handles => StepType.MeldMateria;

    private const long TimeoutMs = 120_000;
    private const long AttachCooldownMs = 1500;

    private int _baseline;
    private long _startTicks;
    private long _lastAttachTicks;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _baseline = GameState.InventoryCount(step.ItemId);
        _startTicks = Environment.TickCount64;
        _lastAttachTicks = 0;
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        var current = GameState.InventoryCount(step.ItemId);
        var consumed = _baseline - current;

        if (consumed >= step.Count)
        {
            DebugLog.Verbose($"Meld complete: {consumed} of {step.ItemId} attached");
            return ExecutorStatus.Complete;
        }

        if (current <= 0)
        {
            DebugLog.Warn($"Out of materia {step.ItemId} after {consumed} melds; restock to continue");
            return ExecutorStatus.Failed;
        }

        if (Environment.TickCount64 - _startTicks > TimeoutMs)
        {
            DebugLog.Warn($"Meld timed out after {consumed} of {step.Count}");
            return ExecutorStatus.Failed;
        }

        if (ctx.Config.EnableAutoMeld && Environment.TickCount64 - _lastAttachTicks >= AttachCooldownMs)
        {
            _lastAttachTicks = Environment.TickCount64;

            // Full meld cycle, mirroring NovusActionRunner.TickInfuse: confirm a
            // pending Yes/No first (that is what consumes the materia), re-open the
            // scroll window the game closes after each infusion, and only then fire
            // the next attach. Without the confirm/reopen halves the executor made
            // at most one infusion per manually opened window.
            if (RelicMeld.TryConfirmYesNo())
                return ExecutorStatus.InProgress;
            if (!RelicMeld.IsScrollOpen())
            {
                RelicMeld.TryOpenScroll();
                return ExecutorStatus.InProgress;
            }
            RelicMeld.TryAttachOne(step.ItemId);
        }

        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}

// Route-driven Novus melding. Computes the cheapest valid materia route from the
// planner (held materia + retainer stock + Universalis prices, optimised under the
// Sphere Scroll caps), then melds it strictly in order: each stat's grades ascend
// (the "go in order" rule) and stats follow the planned sequence.
//
// Sourcing: each line needs a specific (type, grade) materia. If the player's bags
// are short and AutoWithdrawFromRetainers is on, it drives the retainer-retrieve
// seam to pull more from a retainer that holds it; otherwise it fails with an
// actionable message pointing at the Novus window's shopping list.
//
// Completion limitation (same as MeldMateriaExecutor): the per-stat success bar is
// not readable, so progress is tracked by materia consumed (attempts). Failed melds
// consume materia too, so over-stocking per the wiki is expected; the live-UI attach
// seam is the natural place to switch to true success tracking once wired.
public sealed class MeldNovusRouteExecutor : ITaskExecutor
{
    public StepType Handles => StepType.MeldNovusRoute;

    private const long TimeoutMs = 600_000;       // routes are long (75+ melds)
    private const long AttachCooldownMs = 1200;

    // After this many attach attempts with no materia consumed at all, give up fast
    // rather than spinning until the timeout -- this is what happens while the affix
    // attach seam (RelicMeld.TryAttachOne) is still stubbed.
    private const int MaxNoProgressAttach = 8;

    private readonly List<WorkLine> _work = new();
    private int _lineIndex;
    private int _lineBaseline;
    private long _startTicks;
    private long _lastActionTicks;
    private int _attachAttempts;
    private bool _progressed;

    private readonly record struct WorkLine(uint ItemId, MateriaType Type, int Grade, int Melds);

    public void Start(StepData step, ExecutionContext ctx)
    {
        _startTicks = Environment.TickCount64;
        _lastActionTicks = 0;
        _lineIndex = 0;
        _attachAttempts = 0;
        _progressed = false;
        _work.Clear();

        var planner = ctx.MateriaPlanner;
        if (planner != null)
        {
            planner.EnsurePrices();
            var route = planner.ComputeRoute();
            foreach (var scroll in route.Scrolls)
            foreach (var line in scroll.Lines)
            {
                var id = MateriaCatalog.ItemId(line.Type, line.Grade);
                if (id != 0 && line.SuccessfulMelds > 0)
                    _work.Add(new WorkLine(id, line.Type, line.Grade, line.SuccessfulMelds));
            }
        }

        _lineBaseline = _work.Count > 0 ? GameState.InventoryCount(_work[0].ItemId) : 0;
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        DebugLog.Info($"Novus route: {_work.Count} lines, {_work.Sum(w => w.Melds)} planned melds");
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_work.Count == 0)
        {
            DebugLog.Warn("Novus route is empty (materia ids or prices unavailable). Open the Novus window to inspect.");
            return ExecutorStatus.Failed;
        }
        if (_lineIndex >= _work.Count)
            return ExecutorStatus.Complete;
        if (Environment.TickCount64 - _startTicks > TimeoutMs)
        {
            DebugLog.Warn("Novus route timed out");
            return ExecutorStatus.Failed;
        }

        var line = _work[_lineIndex];
        var have = GameState.InventoryCount(line.ItemId);
        var consumed = _lineBaseline - have;
        if (consumed > 0)
            _progressed = true;

        // Line satisfied (proxy: attempts consumed). Advance, re-baselining on the
        // next line's item (which may share an id across the two Paladin scrolls).
        if (consumed >= line.Melds)
        {
            _lineIndex++;
            if (_lineIndex >= _work.Count)
            {
                DebugLog.Info("Novus route complete (all planned melds consumed)");
                return ExecutorStatus.Complete;
            }
            _lineBaseline = GameState.InventoryCount(_work[_lineIndex].ItemId);
            return ExecutorStatus.InProgress;
        }

        // Out of this materia in bags. Programmatic retainer retrieval is a documented
        // permanent no-op (RetainerWithdraw.TryRetrieve always returns false; the real
        // move crashes the client), so looping it until the 10-minute timeout only
        // hid the actionable message. Fail fast with guidance instead; the Novus
        // window's Fetch action lists exactly what to drag out of which retainer.
        if (have <= 0)
        {
            var planner = ctx.MateriaPlanner;
            var inRetainers = ctx.Config.AutoWithdrawFromRetainers && planner != null
                ? planner.HeldInRetainers(line.Type, line.Grade)
                : 0;
            DebugLog.Warn($"Out of {MateriaCatalog.MateriaName(line.Type, line.Grade)}" +
                          (inRetainers > 0
                              ? $"; a retainer holds {inRetainers} (auto-pull is disabled: retainer moves crash the client). " +
                                "Use Fetch in /relic novus to pull them, then restart."
                              : "; buy the shortfall (see the Novus window), then restart."));
            return ExecutorStatus.Failed;
        }

        if (Environment.TickCount64 - _lastActionTicks >= AttachCooldownMs)
        {
            _lastActionTicks = Environment.TickCount64;

            // Only drive the live meld window when the user has opted into the
            // experimental auto-meld; otherwise the route is computed and sourced but
            // the player infuses it themselves.
            if (ctx.Config.EnableAutoMeld)
            {
                // Full cycle (mirrors NovusActionRunner.TickInfuse): confirm the pending
                // Yes/No (this consumes the materia), re-open the window the game closes
                // after each infusion, then attach the next. A successful confirm/reopen
                // is real progress toward the next meld and is not counted as an attach
                // attempt; a FAILED reopen (no Sphere Scroll in the bags) is counted, so
                // the no-progress guard trips instead of waiting out the long timeout.
                if (RelicMeld.TryConfirmYesNo())
                    return ExecutorStatus.InProgress;
                if (!RelicMeld.IsScrollOpen())
                {
                    if (!RelicMeld.TryOpenScroll())
                        _attachAttempts++;
                }
                else
                {
                    RelicMeld.TryAttachOne(line.ItemId, line.Type, line.Grade);
                    _attachAttempts++;
                }
            }
            else
            {
                _attachAttempts++;
            }

            // Progress is judged by materia actually consumed (set above), never by the
            // attach call's return, so a stubbed/ineffective attach stops fast instead
            // of spinning until the timeout.
            if (!_progressed && _attachAttempts >= MaxNoProgressAttach)
            {
                DebugLog.Warn(ctx.Config.EnableAutoMeld
                    ? "Novus auto-meld made no progress. Hold the route's materia and keep a Sphere Scroll in your bags; the meld callback layout may need verifying (turn on the debug log to capture it)."
                    : "Novus auto-meld is off. Enable it in /relic novus (experimental) to infuse automatically, or meld the planned route yourself -- view it with /relic novus.");
                return ExecutorStatus.Failed;
            }
        }

        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
