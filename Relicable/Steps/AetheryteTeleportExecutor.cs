using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps;

// Teleports to an aetheryte using Telepo (verified against current
// FFXIVClientStructs). Phase machine, polled each tick:
//
//   already there  -> Complete immediately
//   issued         -> wait while casting Teleport
//   casting done   -> wait while BetweenAreas (zoning)
//   loaded         -> Complete when in the destination territory and controllable
//   stalled        -> retry up to a cap, then Failed
//
// The destination territory is read from the Telepo TeleportList entry for the
// aetheryte, so no Lumina lookup is needed and the arrival check is exact.
public sealed class AetheryteTeleportExecutor : ITaskExecutor
{
    public StepType Handles => StepType.AetheryteTeleport;

    private const int MaxAttempts = 3;
    private const long AttemptTimeoutMs = 15000;
    // Settle delay after the world becomes safe before touching Telepo. UpdateAetheryteList faults
    // if the aetheryte list is not yet populated (e.g. the first frames after a duty-leave load).
    private const long SafeSettleMs = 500;

    private ushort _destTerritory;
    private bool _resolved;
    private bool _resolveFailed;
    private long _safeSince;
    private int _attempts;
    private long _lastAttemptTicks;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _attempts = 0;
        _resolved = false;
        _resolveFailed = false;
        _safeSince = 0;
        // Resolving the destination calls Telepo.UpdateAetheryteList, which faults if the world is
        // still loading -- observed crashing the client when fired straight after a duty leave. So
        // resolution and the first teleport are deferred to Update, gated on a safe, settled state.
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_resolveFailed)
            return ExecutorStatus.Failed;

        // Resolve the destination lazily, once the world is safe and has settled, so Telepo is
        // never queried mid-transition (the native UpdateAetheryteList crash after a duty leave).
        if (!_resolved)
        {
            if (!Teleporter.SafeToQuery())
            {
                _safeSince = 0;
                return ExecutorStatus.InProgress;
            }
            if (_safeSince == 0)
                _safeSince = Environment.TickCount64;
            if (Environment.TickCount64 - _safeSince < SafeSettleMs)
                return ExecutorStatus.InProgress;

            if (!Teleporter.TryGetDestinationTerritory(step.AetheryteId, out _destTerritory))
            {
                DebugLog.Warn($"Teleport: aetheryte {step.AetheryteId} not in teleport list (locked or unknown)");
                _resolveFailed = true;
                return ExecutorStatus.Failed;
            }
            _resolved = true;
            if (Teleporter.InTerritory(_destTerritory))
            {
                DebugLog.Verbose($"Teleport: already in territory {_destTerritory}, skipping");
                return ExecutorStatus.Complete;
            }
            IssueTeleport(step.AetheryteId);
            return ExecutorStatus.InProgress;
        }

        // Arrived: in the destination territory and no longer zoning.
        if (Teleporter.InTerritory(_destTerritory) && !Teleporter.IsZoning() && Teleporter.PlayerReady())
        {
            DebugLog.Verbose($"Teleport: arrived in territory {_destTerritory}");
            return ExecutorStatus.Complete;
        }

        // Still casting or zoning: keep waiting.
        if (Teleporter.IsCasting() || Teleporter.IsZoning() || Teleporter.TeleportRequested())
            return ExecutorStatus.InProgress;

        // Not progressing. Retry the teleport if we still have budget.
        if (Environment.TickCount64 - _lastAttemptTicks < AttemptTimeoutMs)
            return ExecutorStatus.InProgress;

        if (_attempts >= MaxAttempts)
        {
            DebugLog.Warn($"Teleport: failed after {_attempts} attempts to aetheryte {step.AetheryteId}");
            return ExecutorStatus.Failed;
        }

        IssueTeleport(step.AetheryteId);
        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) { }

    private void IssueTeleport(uint aetheryteId)
    {
        _attempts++;
        _lastAttemptTicks = Environment.TickCount64;
        var ok = Teleporter.Teleport(aetheryteId);
        DebugLog.Verbose($"Teleport: attempt {_attempts} to aetheryte {aetheryteId} -> issued={ok}");
    }
}

// Telepo-backed teleport operations. All calls run on the framework thread (the
// controller tick), satisfying the threading invariant in DESIGN Appendix C.
internal static unsafe class Teleporter
{
    // The Aetheryte Ticket item. Held in the normal bags, so the standard inventory count applies.
    private const uint AetheryteTicketItemId = 7569;

    // Set once at plugin load (Plugin ctor), like Steps.LocationNavigator.Config. Teleporter is
    // static and reached from both the step executor and the window click helpers in GameActions,
    // neither of which shares an ExecutionContext, so the ticket policy is read from here rather
    // than threaded through every call site. Null-safe: no config means "gil", the old behaviour.
    public static Configuration? Config { get; set; }

    // Refreshes the teleport list and returns the destination territory for an
    // aetheryte, or false if the aetheryte is not unlocked / not present.
    public static bool TryGetDestinationTerritory(uint aetheryteId, out ushort territory)
        => TryGetTeleportInfo(aetheryteId, out territory, out _);

    // The destination territory AND the game's own gil price for it. The price is what the
    // teleport window would charge -- it already accounts for favoured destinations, free
    // destinations, and the halved growth past 1000 -- so the ticket threshold compares against
    // the real cost rather than a guess from distance.
    private static bool TryGetTeleportInfo(uint aetheryteId, out ushort territory, out uint gilCost)
    {
        territory = 0;
        gilCost = 0;
        if (!SafeToQuery())
            return false;
        var tp = Telepo.Instance();
        if (tp == null)
            return false;

        tp->UpdateAetheryteList();
        // Iterate via the StdVector First/Last pointers (stable across versions).
        var vec = tp->TeleportList;
        var count = vec.Last - vec.First;
        for (long i = 0; i < count; i++)
        {
            var info = vec.First[i];
            if (info.AetheryteId == aetheryteId)
            {
                territory = info.TerritoryId;
                // A free destination (grand company / free-trial style) costs nothing, so a ticket
                // would be strictly wasted there; report 0 and let the threshold reject it.
                gilCost = info.IsFreeAetheryte ? 0u : info.GilCost;
                return true;
            }
        }
        return false;
    }

    // How many Aetheryte Tickets are in the bags.
    public static int TicketsHeld() => GameState.InventoryCount(AetheryteTicketItemId);

    // Issues the teleport (standard aetheryte, subIndex 0), spending an Aetheryte Ticket instead
    // of gil when the option is on, a ticket is held, and the destination is at or above the
    // configured gil threshold.
    //
    // The two paths are genuinely different native calls -- Telepo.Teleport charges gil, and
    // Telepo.UseTicketInvoker.TeleportWithTickets spends a ticket -- so this is a real choice, not
    // a flag on one call. The ticket path is attempted first and FALLS BACK to gil if it returns
    // false, so a miscounted ticket or a destination the ticket path refuses can never strand a
    // run that would otherwise have teleported fine.
    public static bool Teleport(uint aetheryteId)
    {
        if (!SafeToQuery())
            return false;
        var tp = Telepo.Instance();
        if (tp == null)
            return false;

        if (ShouldUseTicket(aetheryteId, out var gilCost))
        {
            // UpdateAetheryteList was already called by ShouldUseTicket's lookup.
            if (tp->UseTicketInvoker.TeleportWithTickets(aetheryteId, 0))
            {
                DebugLog.Verbose($"Teleport: spent an Aetheryte Ticket for aetheryte {aetheryteId} " +
                                 $"({gilCost}g saved, {TicketsHeld() - 1} left)");
                return true;
            }
            DebugLog.Verbose($"Teleport: ticket path refused aetheryte {aetheryteId}; paying gil instead.");
        }

        tp->UpdateAetheryteList();
        return tp->Teleport(aetheryteId, 0);
    }

    private static bool ShouldUseTicket(uint aetheryteId, out uint gilCost)
    {
        gilCost = 0;
        var config = Config;
        if (config is not { UseAetheryteTickets: true })
            return false;
        if (!TryGetTeleportInfo(aetheryteId, out _, out gilCost))
            return false;
        if (gilCost < (uint)Math.Max(1, config.AetheryteTicketMinGil))
            return false;
        return TicketsHeld() > 0;
    }

    // Safe to call Telepo.UpdateAetheryteList: the player exists and the world is not loading.
    // Calling it mid-transition dereferences an unpopulated list and crashes the client natively
    // (observed as a UpdateAetheryteList access violation right after a duty leave).
    public static bool SafeToQuery()
        => Plugin.ObjectTable.LocalPlayer != null
           && !Plugin.Condition[ConditionFlag.BetweenAreas]
           && !Plugin.Condition[ConditionFlag.BetweenAreas51];

    public static bool TeleportRequested()
    {
        var tp = Telepo.Instance();
        return tp != null && tp->ActiveTeleportRequest;
    }

    public static bool InTerritory(ushort territory)
        => Plugin.ClientState.TerritoryType == territory;

    public static bool IsZoning()
        => Plugin.Condition[ConditionFlag.BetweenAreas]
           || Plugin.Condition[ConditionFlag.BetweenAreas51]
           || Plugin.ObjectTable.LocalPlayer == null;

    public static bool IsCasting()
    {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p is { IsCasting: true };
    }

    // Player exists, is not zoning, and is not in a non-controllable cutscene.
    public static bool PlayerReady()
        => Plugin.ObjectTable.LocalPlayer != null
           && !Plugin.Condition[ConditionFlag.BetweenAreas]
           && !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];
}
