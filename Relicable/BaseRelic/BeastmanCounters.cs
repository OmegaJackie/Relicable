using System.Text;
using Relicable.Model;

namespace Relicable.BaseRelic;

// The "A Relic Reborn" beastmen hunt (part 5) kill counters, read from the relic quest's
// six QuestWork.Variables bytes.
//
// VERIFIED LAYOUT (two fully independent codebases agree byte for byte):
//   (1) Sapphire (server emulator) src/scripts/quest/classquest/DoM/WHM/JobWhm001.cpp --
//       quest 66660 (= row 1124, "A Relic Reborn (Thyrus)"). onBNpcKill increments UI8AL /
//       UI8BH / UI8BL, one per beastman type, and checkQuestCompletion tests
//       `getSeq() == Seq10 && UI8AL >= 8 && UI8BH >= 8 && UI8BL >= 8`, then ZEROES all
//       three and advances to Seq11. The nibble->byte identity comes from the QuestData
//       union in src/common/Common.h, where vars[6] aliases UI8AL:4 / UI8AH:4 / UI8BL:4 /
//       UI8BH:4 ... low nibble first, so UI8A = Variables[0] and UI8B = Variables[1].
//   (2) Questionable's QuestPaths "1127_A Relic Reborn (Omnilex).json" (Scholar -- a
//       different job, a different codebase) authors sequence 10's
//       CompletionQuestVariablesFlags as exactly [{Low:8},{High:8,Low:8},null,null,null,null].
//
// So the three counters are 4-bit NIBBLES, not whole bytes, and they are NOT contiguous --
// UI8AH is skipped:
//       Variables[0] low   (UI8AL)
//       Variables[1] high  (UI8BH)
//       Variables[1] low   (UI8BL)
// Any "three bytes in order" or "pack them in order" assumption is wrong on both counts.
//
// LOAD-BEARING CONSEQUENCE: these nibbles are only meaningful AT SEQUENCE 10. They are
// wiped to 0 when the part completes (advance to 11), and Sapphire shows Variables[1] is
// REUSED as unrelated scratch at other sequences (onDungeonComplete sets UI8BH for the
// Chimera / Amdapor Keep / Ifrit, UI8BL for Garuda). Every read MUST therefore be gated on
// the live sequence, or a dungeon flag reads as a kill count.
//
// UNVERIFIED SEAM: WHICH beastman maps to WHICH nibble is closed for White Mage only, and
// it is NOT positional (Sapphire's ids map to the authored Beastmen array as
// [0] Quarryman -> AL, [1] Bedesman -> BL, [2] Priest -> BH, i.e. a naive "nth mob -> nth
// of AL/BH/BL" wiring swaps two of them). The other nine jobs are inference only. This is
// exactly why the executor never relies on a mob->nibble mapping: see CapDetection in
// KillTargetExecutor, which only needs "did ANY counter rise", a question that needs no
// mapping and cannot be wrong.
public static class BeastmanCounters
{
    // The quest sequence at which the hunt is active and the counters are live. Sourced from
    // the authored part data (part 5) rather than a literal, so the two cannot drift.
    public static int HuntSequence => BaseRelicData.ActiveFromSequenceFor(5);

    // The per-mob kill target the game enforces (Sapphire: `>= 8` for all three).
    public const int PerMobTarget = 8;

    // Nibble codes, encoded as index*2 + (high ? 1 : 0).
    private const int Ui8Al = 0; // Variables[0] low
    private const int Ui8Bl = 2; // Variables[1] low
    private const int Ui8Bh = 3; // Variables[1] high

    // The three beastman counters, in the verified set. Order is NOT a mob mapping -- see
    // the seam note above; nothing may index this by mob.
    public static readonly int[] Nibbles = { Ui8Al, Ui8Bh, Ui8Bl };

    // Read one nibble (code = index*2 + high). -1 when unreadable, so a caller can never
    // mistake "no data" for a zero count.
    public static int ReadNibble(byte[]? vars, int code)
        => vars == null || vars.Length < 6 || code < 0 || code >= 12
            ? -1
            : ((code & 1) == 0 ? vars[code >> 1] & 0x0F : vars[code >> 1] >> 4);

    // The sum of the three beastman counters (0..24), or -1 when unreadable. This is the
    // whole mechanism the executor needs: it is mapping-free, so it is correct for all ten
    // jobs even though only White Mage's mob->nibble assignment is known.
    public static int Total(byte[]? vars)
    {
        if (vars == null || vars.Length < 6)
            return -1;
        var sum = 0;
        foreach (var code in Nibbles)
            sum += ReadNibble(vars, code);
        return sum;
    }

    // True when every counter has reached its target, i.e. the hunt is finished and the game
    // is about to advance the quest to sequence 11 (Sapphire's checkQuestCompletion).
    public static bool AllComplete(byte[]? vars)
    {
        if (vars == null || vars.Length < 6)
            return false;
        foreach (var code in Nibbles)
            if (ReadNibble(vars, code) < PerMobTarget)
                return false;
        return true;
    }

    // A compact dump of all six bytes for the diagnostic log: the raw byte plus its high/low
    // nibbles, with the three beastman counters called out. This is what RECORDS the layout
    // for the nine jobs whose mob->nibble assignment is still inference, so a future build
    // can author it.
    public static string Dump(byte[]? vars)
    {
        if (vars == null || vars.Length < 6)
            return "(quest work unavailable)";
        var sb = new StringBuilder();
        for (var i = 0; i < 6; i++)
            sb.Append($"[{i}]=0x{vars[i]:X2}(hi {vars[i] >> 4},lo {vars[i] & 0x0F}) ");
        sb.Append($"| AL={ReadNibble(vars, Ui8Al)} BH={ReadNibble(vars, Ui8Bh)} BL={ReadNibble(vars, Ui8Bl)}");
        sb.Append($" total={Total(vars)}/{PerMobTarget * 3}");
        return sb.ToString();
    }

    // The live relic-quest id for an objective's job, or 0. Deliberately uses
    // RelicQuestIdFor (not RelicQuestSequenceFor) because the latter's fallback scan returns
    // the highest sequence across ALL relic quest rows, which can be a DIFFERENT job's quest
    // and would silently mis-gate the read.
    public static uint QuestIdFor(RelicObjective? o)
        => o is { Stage: RelicStage.Relic, Job: not RelicJob.None }
            ? BaseRelicState.RelicQuestIdFor(o.Job)
            : 0u;
}
