using System;
using Dalamud.Game.ClientState.Objects.Types;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps.Combat;

// Survival fallback for FATE work: drop the level sync when the fight is being lost.
//
// Level sync is what makes FATE combat work at all -- unsynced, the backend drops every FATE mob
// and nothing credits (see the FATE executor's own notes). But it is also what makes a FATE
// dangerous: synced down, a boss FATE hits a relic-geared character for a real fraction of a
// squashed health pool. Dying costs far more than the FATE is worth -- the death recovery Returns
// to a home aetheryte and the whole objective restarts from its teleport -- so below a health
// threshold the right trade is to give up THIS FATE's credit and live.
//
// Unsyncing restores the full level, the full health pool and full mitigation against mobs now far
// below us, which is why it works as an escape rather than merely a forfeit.
//
// THE EXCEPTION, and the reason this is not just a health check: if the mob will die before we do,
// bailing out throws away a FATE we were about to win. So the threshold only fires when we are
// actually going to lose the race -- time-to-kill against time-to-die, both measured from the
// health that has actually moved over the last few seconds rather than assumed.
//
// Both rates are unknown for the first sampling window, and unknown deliberately counts as "we
// lose": the fallback exists for the case where things have already gone wrong, so it errs toward
// surviving rather than toward keeping a FATE credit.
internal sealed class FateSyncGuard
{
    // Rates are derived over this window. Long enough that one big hit (or one crit) does not read
    // as a trend, short enough to react inside a fight that is going badly.
    private const long SampleWindowMs = 3000;
    // Re-issue guard for "/levelsync off" while the game registers it.
    private const long UnsyncThrottleMs = 2000;

    private readonly Rate _incoming = new();   // damage taken (our health falling)
    private readonly Rate _outgoing = new();   // damage dealt (the target's health falling)
    private ulong _rateTargetId;

    private ushort _bailedFate;
    private long _lastUnsync;
    private long _bailedAt;

    // True while we have deliberately dropped the sync for this FATE. The caller must not re-sync
    // or hand off to the rotation while this holds.
    public bool BailedOut => _bailedFate != 0;

    public void Reset()
    {
        _incoming.Reset();
        _outgoing.Reset();
        _rateTargetId = 0;
        _bailedFate = 0;
        _lastUnsync = 0;
        _bailedAt = 0;
    }

    // Called every tick while working a FATE, BEFORE any sync or engage decision. Returns true when
    // we are bailed out, in which case the caller must skip both.
    public bool Tick(ExecutionContext ctx, ushort fateId)
    {
        if (fateId == 0 || !ctx.Config.FateUnsyncOnLowHp)
        {
            if (BailedOut)
                ClearBail("the low-health fallback was turned off");
            return false;
        }

        // A different FATE is a different fight; never carry a bail-out into one.
        if (_bailedFate != 0 && _bailedFate != fateId)
            ClearBail("moved to a different FATE");

        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || me.MaxHp == 0)
            return BailedOut;

        var now = Environment.TickCount64;
        var hpPct = me.CurrentHp * 100f / me.MaxHp;

        // Sample both pools every tick regardless of state, so the rates are already warm at the
        // moment the threshold is crossed rather than starting to gather then.
        _incoming.Sample(now, me.CurrentHp);
        var target = Plugin.TargetManager.Target as IBattleChara;
        if (target == null || target.GameObjectId != _rateTargetId)
        {
            _rateTargetId = target?.GameObjectId ?? 0;
            _outgoing.Reset();
        }
        if (target != null)
            _outgoing.Sample(now, target.CurrentHp);

        if (BailedOut)
        {
            // Recovered: hand the FATE back to the normal flow, which re-syncs and resumes. The
            // recover threshold is well clear of the bail threshold so a few ticks of regen cannot
            // bounce us straight back into the fight that just went wrong.
            if (hpPct >= ctx.Config.FateResyncHpPercent)
            {
                ClearBail($"health recovered to {hpPct:0}%");
                return false;
            }
            // Still hurt: keep the sync off. The game drops sync on its own in some transitions, so
            // re-issue if it somehow came back.
            if (GameState.IsSyncedToCurrentFate())
                Unsync(now);
            return true;
        }

        if (hpPct > ctx.Config.FateUnsyncHpPercent)
            return false;
        // Only meaningful while we are actually synced to this FATE -- unsynced there is nothing to
        // drop, and outside the ring the sync is not ours to manage.
        if (GameState.CurrentFateId() != fateId || !GameState.IsSyncedToCurrentFate())
            return false;

        // Not losing health at all (a heal landed, or the mob lost us): the threshold alone is not
        // a reason to throw the FATE away.
        var incoming = _incoming.PerSecond;
        if (incoming <= 0f)
            return false;

        var timeToDie = me.CurrentHp / incoming;
        // Time to kill what we are fighting. No target, or no damage landing on it, means we cannot
        // claim we are winning -- so it reads as "never", and the race is lost.
        var outgoing = _outgoing.PerSecond;
        var timeToKill = target != null && outgoing > 0f
            ? target.CurrentHp / outgoing
            : float.PositiveInfinity;

        if (timeToKill < timeToDie)
        {
            DebugLog.Verbose($"FATE {fateId}: at {hpPct:0}% health but winning the race " +
                $"(kill in {timeToKill:0.0}s vs death in {timeToDie:0.0}s); staying synced.");
            return false;
        }

        _bailedFate = fateId;
        _bailedAt = now;
        DebugLog.Warn($"FATE {fateId}: health {hpPct:0}% and losing the race " +
            $"(death in {timeToDie:0.0}s vs kill in {(float.IsInfinity(timeToKill) ? "never" : timeToKill.ToString("0.0") + "s")}) -- " +
            "dropping level sync to survive. This FATE will not credit; the run re-syncs once health " +
            $"is back above {ctx.Config.FateResyncHpPercent}%.");
        Unsync(now);
        return true;
    }

    private void Unsync(long now)
    {
        if (now - _lastUnsync < UnsyncThrottleMs)
            return;
        _lastUnsync = now;
        // "/levelsync off", not the bare toggle: a toggle flips sync back ON if our read of the
        // synced state is momentarily stale, which is the opposite of what this is for. Sent
        // through the game chat box (ECommons.Chat) because Dalamud's ProcessCommand silently drops
        // native commands like /levelsync.
        try { ECommons.Automation.Chat.ExecuteCommand("/levelsync off"); }
        catch (Exception ex) { DebugLog.Warn($"FATE: /levelsync off failed: {ex.Message}"); }
    }

    private void ClearBail(string why)
    {
        if (_bailedFate == 0)
            return;
        DebugLog.Info($"FATE {_bailedFate}: resuming normal sync ({why}; bailed out for " +
            $"{(Environment.TickCount64 - _bailedAt) / 1000}s).");
        _bailedFate = 0;
        _bailedAt = 0;
    }

    // A health pool's rate of loss, in points per second, over the last SampleWindowMs.
    //
    // Anchor-and-recompute rather than a sample history: one (time, value) pair is enough for an
    // average over the window, and re-anchoring whenever the pool GROWS means a heal (or a fresh
    // mob at full health) restarts the measurement instead of averaging a recovery into a
    // death-rate and reporting that we are fine.
    private sealed class Rate
    {
        private long _at;
        private uint _anchor;
        public float PerSecond { get; private set; }

        public void Reset()
        {
            _at = 0;
            _anchor = 0;
            PerSecond = 0f;
        }

        public void Sample(long now, uint value)
        {
            if (_at == 0 || value > _anchor)
            {
                _at = now;
                _anchor = value;
                return;
            }
            var elapsed = now - _at;
            if (elapsed < SampleWindowMs)
                return;
            PerSecond = (_anchor - value) * 1000f / elapsed;
            _at = now;
            _anchor = value;
        }
    }
}
