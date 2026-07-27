using System.Text.Json.Serialization;

namespace Relicable.Model;

// A per-quest-work-byte matcher, mirroring Questionable's QuestWorkValue. Each accepted
// quest carries six "work" bytes (QuestWork.Variables) the game uses to track sub-step
// progress within a sequence. Each byte is split into a HIGH nibble (>>4) and a LOW
// nibble (&0xF); a QuestWorkValue optionally constrains High and/or Low, compared either
// exactly or bitwise. This is the exact mechanism Questionable uses to decide a step is
// done (CompletionQuestVariablesFlags) or eligible to run (RequiredQuestVariables), and
// is what lets Relicable verify a base-relic step at sub-sequence granularity instead of
// only watching the coarse whole-quest sequence.
public enum QuestWorkMode
{
    Exact,   // the nibble must equal the value
    Bitwise, // all bits set in the value must be set in the nibble ((actual & v) == v)
}

public sealed class QuestWorkValue
{
    // Null means "do not constrain this nibble".
    public byte? High { get; set; }
    public byte? Low { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QuestWorkMode Mode { get; set; } = QuestWorkMode.Exact;

    public QuestWorkValue() { }

    public QuestWorkValue(byte? high, byte? low, QuestWorkMode mode = QuestWorkMode.Exact)
    {
        High = high;
        Low = low;
        Mode = mode;
    }

    // True when at least one nibble is constrained to a non-zero value (so this is a real
    // marker, not an all-null / all-zero placeholder slot).
    [JsonIgnore]
    public bool ConstrainsAnything => High is > 0 || Low is > 0;
}
