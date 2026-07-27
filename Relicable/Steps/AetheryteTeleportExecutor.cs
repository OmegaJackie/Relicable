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
    // Refreshes the teleport list and returns the destination territory for an
    // aetheryte, or false if the aetheryte is not unlocked / not present.
    public static bool TryGetDestinationTerritory(uint aetheryteId, out ushort territory)
    {
        territory = 0;
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
                return true;
            }
        }
        return false;
    }

    // Issues the teleport (standard aetheryte, subIndex 0).
    public static bool Teleport(uint aetheryteId)
    {
        if (!SafeToQuery())
            return false;
        var tp = Telepo.Instance();
        if (tp == null)
            return false;
        tp->UpdateAetheryteList();
        return tp->Teleport(aetheryteId, 0);
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
