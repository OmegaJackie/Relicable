using System.Collections.Generic;
using Relicable.Model;

namespace Relicable.BaseRelic;

// Nibble-compare of a quest's six work bytes against QuestWorkValue matchers -- the exact
// verification math Questionable uses (its Controller/Utils/QuestWorkUtils.cs). Each work
// byte is split into a high nibble (>>4) and a low nibble (&0xF); a null matcher entry
// means "don't care" for that byte. Exact compares equality; Bitwise checks that every set
// bit in the value is set in the nibble. Feed it the live bytes from
// GameState.QuestWorkVariables(questId).
public static class QuestWorkUtils
{
    // True when 'flags' is a real completion marker: six entries with at least one that
    // constrains a nibble to a non-zero value (mirrors HasCompletionFlags). An all-null or
    // shorter list is treated as "no completion flags authored".
    public static bool HasCompletionFlags(IReadOnlyList<QuestWorkValue?>? flags)
    {
        if (flags == null || flags.Count != 6)
            return false;
        foreach (var f in flags)
            if (f is { ConstrainsAnything: true })
                return true;
        return false;
    }

    // Completion check: every constrained nibble in 'flags' must match the live work bytes.
    // Used to decide a step/part is DONE. Returns false when either side is missing or the
    // work bytes cannot be read (caller then falls back to the sequence signal).
    public static bool MatchesQuestWork(
        IReadOnlyList<byte>? variables, IReadOnlyList<QuestWorkValue?>? flags)
    {
        if (variables == null || flags == null || flags.Count == 0)
            return false;

        var n = variables.Count < flags.Count ? variables.Count : flags.Count;
        for (var i = 0; i < n; i++)
        {
            var check = flags[i];
            if (check == null)
                continue;

            var high = (byte)(variables[i] >> 4);
            var low = (byte)(variables[i] & 0x0F);

            if (check.Mode == QuestWorkMode.Exact)
            {
                if (check.High is { } h && high != h)
                    return false;
                if (check.Low is { } l && low != l)
                    return false;
            }
            else // Bitwise: all set bits in the expected value must be set in the nibble
            {
                if (check.High is { } h && (byte)(high & h) != h)
                    return false;
                if (check.Low is { } l && (byte)(low & l) != l)
                    return false;
            }
        }
        return true;
    }

    // Required-gate check (OR-semantics within a slot): a step should run only when, for
    // every non-empty required slot, at least one of its QuestWorkValues matches the live
    // nibble. Mirrors MatchesRequiredQuestWorkConfig. A null/empty config means "no gate"
    // (returns true); unreadable work bytes with a real gate means "not met" (false).
    public static bool MatchesRequiredQuestWork(
        IReadOnlyList<byte>? variables, IReadOnlyList<IReadOnlyList<QuestWorkValue>?>? required)
    {
        if (required == null || required.Count == 0)
            return true;
        if (variables == null)
            return false;

        for (var i = 0; i < required.Count && i < variables.Count; i++)
        {
            var slot = required[i];
            if (slot == null || slot.Count == 0)
                continue;

            var high = (byte)(variables[i] >> 4);
            var low = (byte)(variables[i] & 0x0F);

            var anyMatch = false;
            foreach (var v in slot)
            {
                if (v.High is { } h && high != h)
                    continue;
                if (v.Low is { } l && low != l)
                    continue;
                anyMatch = true;
                break;
            }
            if (!anyMatch)
                return false;
        }
        return true;
    }
}
