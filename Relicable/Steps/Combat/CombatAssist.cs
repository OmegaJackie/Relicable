using System;
using Dalamud.Game.ClientState.Conditions;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps.Combat;

// Shared combat-assist hooks called by the kill and FATE executors: keep the
// chocobo summoned in the configured stance, and (when RSR drives the rotation) hand
// BossMod Reborn a separate avoidance preset so it dodges AoEs while RSR runs the
// rotation.
//
// When BossMod Reborn IS the combat backend, its rotation-only combat preset owns the
// (exclusive) active-preset slot, so activating a second avoidance preset here would
// clobber the rotation -- hence the backend guard. Note this means the BossMod Reborn
// backend does not add AoE avoidance (avoidance needs movement control, which vnavmesh
// owns); that is an accepted trade for the trivial ARR relic content.
internal static class CombatAssist
{
    private static bool BossModRebornIsBackend(ExecutionContext ctx)
        => ctx.Config.Backend == Configuration.CombatBackend.BossModReborn;

    public static void Engage(ExecutionContext ctx)
    {
        Companion.EnsureReady(ctx.Config.AutoSummonChocobo, ctx.Config.ChocoboHealerStance);
        if (ctx.Config.UseBossModRebornAvoidance && !BossModRebornIsBackend(ctx))
            ctx.BossModReborn.EnableAvoidance(ctx.Config.BossModRebornAvoidancePreset);
    }

    public static void Disengage(ExecutionContext ctx)
    {
        if (!BossModRebornIsBackend(ctx))
            ctx.BossModReborn.Disable();
    }

    // Shared self-defense for the loops that TRAVEL or WAIT with the rotation off -- the FATE
    // stage-and-wait, the leve travel/initiate and objective holds, the treasure-map walk. Only
    // KillTargetExecutor and InteractObjectExecutor ever read ConditionFlag.InCombat, and there is
    // no global watchdog, so everywhere else an ambient enemy that aggroed was simply never
    // targeted: the loop kept calling ctx.Rotation.Disable() and kept moving while being hit.
    // Those loops' own finders cannot see it either -- NearestHostileInFate matches only mobs
    // carrying the FATE's id, and FindNearestLeveObjective only leve-director-owned ones, so an
    // ordinary overworld hostile is invisible to both at any distance.
    //
    // Ground, hard-target whatever is actually hitting us, mark it, and run the backend in MANUAL
    // on it. Returns true when the caller must abandon this tick (we are defending); false when
    // there is nothing on us, so the caller proceeds with its normal flow.
    //
    // armedId is the caller's own latch: the backend mode is re-sent ONLY when the target changes,
    // never per tick, so this cannot reproduce the RSR mode-thrash documented in
    // KillTargetExecutor (re-sending "Manual" every frame stops RSR ever settling into its
    // rotation). Callers must also FREEZE their step deadline while this returns true, or a long
    // add fight eats the budget and the step fails for the wrong reason.
    public static bool DefendSelf(ExecutionContext ctx, ref ulong armedId)
    {
        if (!Plugin.Condition[ConditionFlag.InCombat])
        {
            armedId = 0;
            return false;
        }

        // Nothing can be cast while mounted or airborne, so get down first.
        if (!Mount.IsGrounded())
        {
            ctx.Navmesh.Stop();
            Mount.LandAndDismount(ctx, Plugin.ObjectTable.LocalPlayer?.Position ?? default);
            return true;
        }

        // No excludeId: unlike the kill grind there is no intended relic mob to skip here, so
        // whatever hostile is on us (or on the chocobo) is the thing to fight.
        var meId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        if (!ctx.Targeting.EngageAggressor(meId, 0, Companion.CompanionId()))
        {
            // In combat but nothing is targeting us -- e.g. the FATE loop's own mobs are already
            // being handled, or combat is draining out. Hand the tick back to the caller.
            armedId = 0;
            return false;
        }

        var tid = Plugin.TargetManager.Target?.GameObjectId ?? 0;
        if (tid != armedId)
        {
            armedId = tid;
            // The backend may have auto-off'd after the previous fight while our mode is
            // edge-triggered; force this dispatch to actually re-send for the NEW target only.
            ctx.Rotation.ResyncNextDispatch();
            // Attack1 marks it as the backend's priority target. Through the game chat box
            // (ECommons.Chat), never ctx.Commands, which silently drops native game commands.
            try { ECommons.Automation.Chat.ExecuteCommand("/enemysign attack1 <t>"); }
            catch (Exception ex) { DebugLog.Warn($"DefendSelf: /enemysign failed: {ex.Message}"); }
        }

        ctx.Navmesh.Stop();
        ctx.Rotation.EnableManual();
        Engage(ctx);
        return true;
    }
}
