using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
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

    private const int MaxAttempts = 5;
    // Two retry clocks, because the two failures are distinguishable and BOTH are known-bad the
    // moment they are seen -- there is nothing to wait for in either case.
    //
    // NoCastRetryMs: Telepo accepted the request (Teleport returned true) and no cast ever started.
    // Observed live -- "issued=True" followed by casting/requested/zoning all false for fifteen
    // seconds straight, then the very next attempt casting normally. Whatever the transient is, the
    // request plainly did not take, and the old code could not tell: it had a single fifteen-second
    // window for "not progressing", so a teleport that would have worked on a prompt re-issue
    // instead did nothing for forty-five seconds and then failed the step.
    //
    // InterruptRetryMs: a cast that was seen to START and then vanished without zoning (movement
    // cancels it). Slightly shorter still, since the cast is known to be reachable.
    private const long NoCastRetryMs = 3000;
    private const long InterruptRetryMs = 2500;
    // Teleport is refused in combat. Wait for it to drop rather than burning attempts on a state
    // that resolves itself -- but bounded, so a bugged never-ending combat flag cannot hang the run.
    private const long CombatWaitMs = 20000;
    // How long the action layer may keep refusing Teleport before the step gives up and reports the
    // status code, rather than waiting on a blocker that is never going to clear.
    private const long BlockedGiveUpMs = 30000;
    // Settle delay after the world becomes safe before touching Telepo. UpdateAetheryteList faults
    // if the aetheryte list is not yet populated (e.g. the first frames after a duty-leave load).
    private const long SafeSettleMs = 500;

    private ushort _destTerritory;
    private bool _resolved;
    private bool _resolveFailed;
    private long _safeSince;
    private int _attempts;
    private long _lastAttemptTicks;
    private bool _castSeen;   // the Teleport cast was observed since the last attempt
    private long _stateLog;
    private long _combatSince; // when we started waiting for combat to drop (0 = not waiting)
    // CombatAssist.DefendSelf's per-caller latch: the id we last armed the backend for, so the mode
    // is re-sent only when the attacker changes rather than every tick.
    private ulong _defendArmedId;
    private long _blockedSince; // when the action-status refusal started (0 = not refused)
    private long _blockedLog;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _attempts = 0;
        _resolved = false;
        _resolveFailed = false;
        _safeSince = 0;
        _castSeen = false;
        _stateLog = 0;
        _combatSince = 0;
        _defendArmedId = 0;
        _blockedSince = 0;
        _blockedLog = 0;
        // Resolving the destination calls Telepo.UpdateAetheryteList, which faults if the world is
        // still loading -- observed crashing the client when fired straight after a duty leave. So
        // resolution and the first teleport are deferred to Update, gated on a safe, settled state.
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_resolveFailed)
            return ExecutorStatus.Failed;

        // MOVEMENT CANCELS THE TELEPORT CAST, and nothing here used to stop it. A vnavmesh path
        // outlives the executor that issued it, so a step that hands over mid-route -- or a user
        // "Run next" that re-plans while the character is still walking -- left us moving straight
        // through our own five-second cast: the cast starts (the game even logs the action), dies
        // silently, and the step then waits out its full 15s attempt window before trying again.
        //
        // Gated on IsRunning rather than issued unconditionally: Navmesh.Stop() is not deduplicated
        // (it fires the IPC and drops the cached destination on every call), so a per-tick stop is
        // its own problem. This is also self-healing -- if anything re-issues a move mid-cast, the
        // next tick sees it running and stops it again.
        if (ctx.Navmesh.IsRunning())
        {
            DebugLog.Verbose("Teleport: halting a path that was still running (movement cancels the cast)");
            ctx.Navmesh.Stop();
        }

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

        // Heartbeat (every 3s) so "it will not teleport" is answerable from the log alone: which of
        // the wait conditions is holding, and whether we are somewhere the cast cannot even start.
        var now = Environment.TickCount64;
        if (now - _stateLog > 3000)
        {
            _stateLog = now;
            DebugLog.Info($"Teleport step: aetheryte {step.AetheryteId} -> territory {_destTerritory} " +
                $"(here {Plugin.ClientState.TerritoryType}); casting={Teleporter.IsCasting()} " +
                $"zoning={Teleporter.IsZoning()} requested={Teleporter.TeleportRequested()} " +
                $"airborne={Teleporter.Airborne()} inEvent={Interaction.EventConditions.InEvent} " +
                $"inCombat={Teleporter.InCombat()} castSeen={_castSeen} " +
                // The decisive field: non-zero means the game is refusing the action outright, which
                // Telepo's own "issued=True" cannot show.
                $"actionStatus={Teleporter.TeleportActionStatus()} " +
                $"attempt {_attempts}/{MaxAttempts}");
        }

        // Still casting or zoning: keep waiting. TeleportRequested counts as the cast having taken --
        // it is Telepo's own "a teleport is under way" flag, so losing it later is an interruption
        // rather than a request that never landed.
        if (Teleporter.IsCasting() || Teleporter.IsZoning() || Teleporter.TeleportRequested())
        {
            _castSeen = true;
            return ExecutorStatus.InProgress;
        }

        // Not progressing, and which of the two failures it is decides how long we wait: a cast that
        // started and died (interrupted) vs a request that was accepted and never produced one.
        var since = now - _lastAttemptTicks;
        if (since < (_castSeen ? InterruptRetryMs : NoCastRetryMs))
            return ExecutorStatus.InProgress;

        // Airborne: the game will not start the cast at all, and Telepo still reports the request as
        // issued, so this is invisible without the check. Land first (mounted ON THE GROUND is fine,
        // hence not Mount.IsGrounded, which would also insist on dismounting). Does not consume an
        // attempt -- we have not tried to teleport yet.
        if (Teleporter.Airborne())
        {
            var here = Plugin.ObjectTable.LocalPlayer?.Position ?? default;
            DebugLog.Verbose("Teleport: airborne, landing before casting");
            Combat.Mount.LandAndDismount(ctx, here);
            return ExecutorStatus.InProgress;
        }

        // Mid-conversation / cutscene: the cast is refused. Wait it out rather than burning attempts
        // on a state that resolves itself.
        if (Interaction.EventConditions.InEvent)
            return ExecutorStatus.InProgress;

        // In combat: Teleport is refused outright.
        //
        // WAITING ALONE CANNOT RESOLVE THIS, which is what made this branch the plugin's clearest
        // case of "an aggroed enemy is never attacked". The thing refusing the teleport IS the thing
        // hitting us, so standing still for the full CombatWaitMs guarantees twenty seconds of free
        // damage and then a teleport attempt into the same refusal -- and this executor never touches
        // ctx.Rotation, so the backend sits in whatever mode the previous step left it in.
        //
        // So fight first. DefendSelf grounds us, hard-targets whatever is actually on us and runs the
        // backend on it; it also issues its own Navmesh.Stop(), which is what this step wants anyway
        // (movement cancels the cast). The bounded wait is kept for the case DefendSelf declines --
        // in combat with nothing targeting us, a combat flag draining out -- where waiting IS the
        // right answer. Neither path consumes an attempt.
        if (Teleporter.InCombat())
        {
            if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
            {
                // Freeze the wait AND the retry clock while defending: a long add fight must not
                // spend the combat window (or the re-issue window) it is the reason for.
                _combatSince = now;
                _lastAttemptTicks = now;
                return ExecutorStatus.InProgress;
            }
            if (_combatSince == 0)
                _combatSince = now;
            if (now - _combatSince < CombatWaitMs)
                return ExecutorStatus.InProgress;
        }
        else
        {
            _combatSince = 0;
            _defendArmedId = 0;
        }

        // Ask the game whether Teleport can be used AT ALL before spending an attempt on it. This is
        // the prevention rather than the recovery: a refusal here is why "issued=True" could be
        // followed by nothing at all, and waiting for it to clear means the cast goes out the moment
        // it can succeed instead of on a retry clock. Bounded, so a status that never clears reports
        // the code rather than waiting for ever.
        var status = Teleporter.TeleportActionStatus();
        if (status != 0)
        {
            if (_blockedSince == 0)
                _blockedSince = now;
            if (now - _blockedSince < BlockedGiveUpMs)
            {
                if (now - _blockedLog >= 5000)
                {
                    _blockedLog = now;
                    DebugLog.Info($"Teleport: the game will not let Teleport be used right now " +
                        $"(status {status}). {Teleporter.CostReport(step.AetheryteId)} Waiting for it to clear.");
                }
                return ExecutorStatus.InProgress;
            }
            DebugLog.Warn($"Teleport: the game refused Teleport for {BlockedGiveUpMs / 1000}s " +
                $"(status {status}) to aetheryte {step.AetheryteId}. {Teleporter.CostReport(step.AetheryteId)}");
            return ExecutorStatus.Failed;
        }
        _blockedSince = 0;

        if (_attempts >= MaxAttempts)
        {
            DebugLog.Warn($"Teleport: failed after {_attempts} attempts to aetheryte {step.AetheryteId}. " +
                $"{Teleporter.CostReport(step.AetheryteId)} The cast is cancelled by movement and refused " +
                "in combat, while airborne, or in a cutscene.");
            return ExecutorStatus.Failed;
        }

        DebugLog.Verbose(_castSeen
            ? "Teleport: the cast started and was interrupted (movement?); re-casting"
            : "Teleport: the request was accepted but no cast started; re-issuing");
        IssueTeleport(step.AetheryteId);
        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx) { }

    private void IssueTeleport(uint aetheryteId)
    {
        _attempts++;
        _castSeen = false;
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

    // Teleport is refused outright in combat -- a combat-heavy plugin walks into this constantly,
    // and from Telepo's side it is indistinguishable from a request that simply did not take.
    public static bool InCombat() => Plugin.Condition[ConditionFlag.InCombat];

    // The Teleport spell. Telepo issues it for us, but its own return value only says the request
    // was QUEUED -- so asking the action layer directly is the only way to see a refusal.
    private const uint TeleportActionId = 5;

    // Whether the game will currently let the Teleport action be used, as its own status code:
    // 0 = usable, anything else is a reason (on cooldown, in combat, cannot afford it, in a state
    // that forbids it...).
    //
    // THIS IS THE ONE HONEST SIGNAL. Telepo.Teleport returning true was being read as "the teleport
    // is happening", and it is not: it was observed live returning true with no cast, no request
    // flag and no zoning for fifteen seconds -- while /return, a different action with no gil cost,
    // worked from the same spot. Everything upstream of Telepo was invisible. Asking here means the
    // step waits for the blocker to clear and fires the moment it does, instead of firing blind into
    // a refusal and calling that an attempt.
    public static uint TeleportActionStatus()
    {
        var am = ActionManager.Instance();
        if (am == null)
            return 0;
        try { return am->GetActionStatus(ActionType.Action, TeleportActionId); }
        catch { return 0; } // unknown -> fail OPEN, so a bad read can never block a working teleport
    }

    // Gil held against what this destination actually costs, for the give-up message: "it will not
    // teleport" and "you cannot afford it" look identical from every other signal. Gil is item id 1.
    public static string CostReport(uint aetheryteId)
    {
        if (!TryGetTeleportInfo(aetheryteId, out _, out var cost) || cost == 0)
            return string.Empty;
        var gil = GameState.InventoryCount(1);
        return gil < cost
            ? $"You have {gil} gil and it costs {cost}."
            : $"(It costs {cost} gil; you have {gil}.)";
    }

    // The Teleport cast cannot START while airborne. Deliberately NOT Mount.IsGrounded, which also
    // rejects being mounted -- teleporting from the back of a mount on the ground is fine, and
    // forcing a dismount for every travel step would be a pointless remount afterwards.
    public static bool Airborne()
        => Plugin.Condition[ConditionFlag.InFlight]
           || Plugin.Condition[ConditionFlag.Diving];

    // Player exists, is not zoning, and is not in a non-controllable cutscene.
    public static bool PlayerReady()
        => Plugin.ObjectTable.LocalPlayer != null
           && !Plugin.Condition[ConditionFlag.BetweenAreas]
           && !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];
}
