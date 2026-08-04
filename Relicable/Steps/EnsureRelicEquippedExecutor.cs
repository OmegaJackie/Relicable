using System;
using Relicable.Model;

namespace Relicable.Steps;

// Ensures a relic weapon is equipped before a duty or hunt so its drops/kills credit toward the
// relic. If none is equipped, best-effort equips the relic from the armoury/bags and verifies it
// took; if it cannot (or auto-equip is disabled), it fails with guidance so the player equips it and
// resumes. The common case -- a relic already equipped -- is a zero-cost no-op (no item moves).
//
// The search RETRIES for the whole grace window rather than being decided once at Start. This step
// runs immediately after the turn-in where Gerolt hands the unfinished relic over (A Relic Reborn
// sequence 9, right before the beastman hunt), and the new weapon takes a server round-trip to land
// in the bags: a single Start-time look would usually miss it and fail the step for "none found"
// when the weapon was about to appear.
public sealed class EnsureRelicEquippedExecutor : ITaskExecutor
{
    // Covers both waits this step has to absorb: an item just granted by a turn-in arriving in the
    // bags, and the equip itself applying afterwards.
    private const long EquipTimeoutMs = 10000;
    // Re-issue the equip at most this often while waiting, so a move that silently did not take is
    // retried without spamming MoveItemSlot every frame.
    private const long RetryIntervalMs = 1000;

    public StepType Handles => StepType.EnsureRelicEquipped;

    private long _startTicks;
    private long _lastAttempt;
    private bool _loggedWait;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _startTicks = Environment.TickCount64;
        _lastAttempt = 0;
        _loggedWait = false;
        TryEquip(ctx);
    }

    // Whether the weapon in hand is the one this step actually needs.
    //
    // "Some relic is equipped" is NOT the test while the character holds an "Unfinished <weapon>".
    // That form exists only between Gerolt forging it ('A Relic Reborn' sequence 9) and taking it
    // back (14), and the quest credits the beastman kills and the Hydra to it ALONE -- yet a
    // finished relic, a Zenith or an Atma of the same job is equippable and reads as RelicStage.Relic
    // just as happily. So a character with an older relic of that job in hand (a repeat run, or one
    // equipped by hand) passed this step instantly and then culled 24 beastmen that credited nothing,
    // with no error anywhere: the kills simply never counted.
    //
    // Holding the Unfinished form is itself the signal that it is wanted, so no step has to declare
    // it. Once it is handed back at sequence 14 the check relaxes on its own, which is what keeps
    // the primal trials (15-17, fought without it) and every Atma+ upgrade on the old behaviour.
    private static bool RelicReady()
        => GameState.HoldsUnfinishedRelic()
            ? GameState.UnfinishedRelicEquipped()
            : GameState.EquippedRelicStage() != RelicStage.None;

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (RelicReady())
        {
            // A real relic is in hand again, so the live stage read is authoritative once more and
            // any stand-in noted by an unequip step must not linger.
            RelicStageMemo.Clear();
            return ExecutorStatus.Complete; // already equipped, or the equip took
        }

        // Auto-equip off: this is the player's choice, so say so immediately rather than burning
        // the grace window on a retry loop that will never fire.
        if (!ctx.Config.AutoEquipRelicInDuty)
        {
            Diagnostics.DebugLog.Warn("Relic weapon is not equipped and auto-equip is off. Equip your " +
                                      "in-progress relic so the kills credit, then resume.");
            return ExecutorStatus.Failed;
        }

        if (Environment.TickCount64 - _startTicks < EquipTimeoutMs)
        {
            TryEquip(ctx);
            return ExecutorStatus.InProgress;
        }

        Diagnostics.DebugLog.Warn(
            (GameState.HoldsUnfinishedRelic()
                ? "Could not equip your UNFINISHED relic weapon within " + EquipTimeoutMs / 1000 + "s. You are holding " +
                  "one, and it is the only weapon 'A Relic Reborn' credits kills to -- another tier of the same " +
                  "relic in hand will not count. "
                : "Could not equip your relic weapon within " + EquipTimeoutMs / 1000 + "s: none was found in your " +
                  "armoury or bags, or the equip did not take (wrong job for the weapon, or the slot was blocked). ") +
            "Equip it manually so the kills credit, then resume.");
        return ExecutorStatus.Failed;
    }

    // One throttled attempt: find the held relic weapon this step needs and move it into the hand.
    // Silent when nothing is found -- the caller keeps retrying until the grace window is out,
    // because "not there yet" and "not there at all" look identical until then.
    //
    // The bail-out is RelicReady(), not "any relic is equipped": with the wrong tier already in
    // hand the latter returned here every time, so the swap to the Unfinished form was never even
    // attempted and the step just timed out. MoveItemSlot into an occupied weapon slot swaps, so
    // there is nothing to take off first.
    private void TryEquip(ExecutionContext ctx)
    {
        if (!ctx.Config.AutoEquipRelicInDuty)
            return;
        if (RelicReady())
            return;
        if (Environment.TickCount64 - _lastAttempt < RetryIntervalMs)
            return;
        _lastAttempt = Environment.TickCount64;

        if (GameState.TryFindRelicInBags(out var container, out var slot))
        {
            Diagnostics.DebugLog.Info($"Relic not equipped; equipping it from {container} slot {slot}.");
            GameState.TryEquipFromBag(container, slot);
        }
        else if (!_loggedWait)
        {
            _loggedWait = true;
            Diagnostics.DebugLog.Info("No relic weapon in the armoury or bags yet; waiting for it to arrive.");
        }
    }

    public void Stop(ExecutionContext ctx) { }
}
