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
}
