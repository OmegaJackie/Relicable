using System;
using Relicable.Model;

namespace Relicable.Steps;

// Keeps the engine's idea of "how far this character's relic has progressed" alive across the
// windows where the relic weapon is deliberately NOT equipped.
//
// The problem it solves: progress is read off the EQUIPPED weapon (GameState.EquippedRelicStage) --
// that is the authoritative record, because every upgrade hands back a new item id. But three steps
// in the line require the weapon to be OFF to work at all:
//   * the Zenith -> Atma trade at Jalzahn (his turn-in list shows only unequipped weapons),
//   * the Atma -> Animus trade at Jalzahn (same),
//   * "Give the unfinished <weapon> to Gerolt" (A Relic Reborn sequence 14; the hand-over UI does
//     not list what is in your hands).
// For the length of each of those, the live read is None -- and None means "no relic progress at
// all", which widens Auto-mode selection back to stages the character finished long ago. That is the
// engine losing track of progress purely because a step took the weapon off on purpose.
//
// So the unequip sites NOTE the stage here first, and the planning reads consult
// EffectiveEquippedStage() instead of the raw equipped read. Deliberately NARROW:
//   * opt-in -- nothing writes here except a step that is about to unequip, so this can never
//     paper over a genuine "no relic equipped" state the player created themselves;
//   * a FLOOR only -- a live read always wins, and the memo is dropped the moment any relic weapon
//     is equipped again (the real record is back, so the stand-in must not linger and outrank it);
//   * time-boxed -- a run that dies mid-trade cannot leave a stale stage asserted forever.
public static class RelicStageMemo
{
    // How long a noted stage stands in for the equipped read. Comfortably longer than a Jalzahn
    // round trip (teleport + approach + menu chain) and than a Gerolt hand-over, short enough that
    // an abandoned run forgets it well inside a play session.
    private const long HoldMs = 10 * 60 * 1000;

    private static RelicStage _stage;
    private static long _until;

    // Record the stage that is about to be unequipped. Ignores None (nothing to remember) so a
    // caller does not have to check first.
    public static void Note(RelicStage stage)
    {
        if (stage == RelicStage.None)
            return;
        _stage = stage;
        _until = Environment.TickCount64 + HoldMs;
    }

    // Forget the noted stage. Called once the weapon is back on (or the step that unequipped it is
    // finished with it), so the live read is the only authority again.
    public static void Clear()
    {
        _stage = RelicStage.None;
        _until = 0;
    }

    // The stage to plan against: the live equipped read when there IS one, otherwise a stage noted
    // by a step that deliberately took the weapon off. Self-clearing -- a live read means the memo
    // has served its purpose, so it is dropped here rather than relying on every call site to.
    public static RelicStage EffectiveEquippedStage()
    {
        var live = GameState.EquippedRelicStage();
        if (live != RelicStage.None)
        {
            Clear();
            return live;
        }
        if (_stage != RelicStage.None && Environment.TickCount64 > _until)
            Clear();
        return _stage;
    }
}
