using System;
using Relicable.Model;

namespace Relicable.Steps;

// Ensures a relic weapon is equipped before a duty so its drops/kills credit toward the relic.
// If none is equipped, best-effort equips the relic from the armoury/bags and verifies it took;
// if it cannot (or auto-equip is disabled), it fails with guidance so the player equips it and
// resumes. The common case -- a relic already equipped -- is a zero-cost no-op (no item moves).
public sealed class EnsureRelicEquippedExecutor : ITaskExecutor
{
    private const long EquipTimeoutMs = 5000;

    public StepType Handles => StepType.EnsureRelicEquipped;

    private long _startTicks;
    private bool _noCandidate;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _startTicks = Environment.TickCount64;
        _noCandidate = false;

        if (GameState.EquippedRelicStage() != RelicStage.None)
            return; // already equipped -> Update completes immediately

        if (!ctx.Config.AutoEquipRelicInDuty)
            return; // auto-equip off -> Update fails with the safe-pause guidance

        if (GameState.TryFindRelicInBags(out var container, out var slot))
        {
            Diagnostics.DebugLog.Info($"Relic not equipped; equipping it from {container} slot {slot} before the duty.");
            GameState.TryEquipFromBag(container, slot);
        }
        else
        {
            _noCandidate = true;
        }
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (GameState.EquippedRelicStage() != RelicStage.None)
            return ExecutorStatus.Complete; // already equipped, or the equip took

        // Not equipped and we will not / cannot fix it: stop with clear guidance.
        if (!ctx.Config.AutoEquipRelicInDuty || _noCandidate)
        {
            Diagnostics.DebugLog.Warn(
                !ctx.Config.AutoEquipRelicInDuty
                    ? "Relic weapon is not equipped and auto-equip is off. Equip your in-progress relic so the duty credits, then resume."
                    : "Relic weapon is not equipped and none was found in your armoury or bags to equip. Equip it so the duty credits, then resume.");
            return ExecutorStatus.Failed;
        }

        // Gave it the equip move; wait for it to apply, then pause if it did not take.
        if (Environment.TickCount64 - _startTicks < EquipTimeoutMs)
            return ExecutorStatus.InProgress;

        Diagnostics.DebugLog.Warn(
            "Tried to equip your relic weapon but it did not take (wrong job for the weapon, or the slot was blocked). " +
            "Equip it manually so the duty credits, then resume.");
        return ExecutorStatus.Failed;
    }

    public void Stop(ExecutionContext ctx) { }
}
