using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Relicable.Model;

// A single deserialized step. Only the fields relevant to a given StepType are
// populated; the rest stay at their defaults. This keeps the JSON schema flat
// and easy for the data generator (see DESIGN.md section 7) to emit.
public sealed class StepData
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StepType Type { get; set; }

    // Navigation
    public Vector3? Position { get; set; }
    public float StopDistance { get; set; } = 3.0f;
    public bool Fly { get; set; }

    // Travel
    public uint AetheryteId { get; set; }
    public uint AethernetShardId { get; set; }

    // Combat / targeting
    public string? TargetName { get; set; }
    public uint TargetDataId { get; set; }
    public int Count { get; set; } = 1;
    public bool FateBound { get; set; }

    // Several enemy names that ALL count for this one KillTarget step, so it takes whichever is
    // nearest rather than clearing them one type at a time. Empty = single-target (TargetName).
    //
    // The base relic's beastman hunt is the case this exists for: "slay eight lancers, eight
    // pugilists and eight thaumaturges" is one journal step over three intermingled spawn groups,
    // and running it as three sequential single-name steps meant walking past two thirds of the
    // enemies that still needed killing on every lap. TargetName is still set (to the first name)
    // for logs and the map flag. See KillTargetExecutor for how types are retired at their cap.
    public List<string> TargetNames { get; set; } = new();

    // Opt-in: this KillTarget step's progress is tracked by the relic quest's own
    // QuestWork counters rather than a local kill tally. Set ONLY by
    // BaseRelicHuntGenerator for the "A Relic Reborn" beastmen hunt (part 5), which is
    // the one kill type with no RelicNote slot and no item drop to read.
    //
    // Deliberately an explicit per-step opt-in rather than something inferred from the
    // objective's stage/kind: KillTargetExecutor is shared by the Animus book monster
    // slots, Atma, FATEs and leves, and inferring the mode risks silently re-routing
    // those. Nothing that does not set this flag can reach the quest-counter path.
    public bool UseQuestKillCounter { get; set; }

    // The CUMULATIVE beastmen-kill total (across this step and every earlier hunt step)
    // at which this step is done: 8 for the first mob, 16 for the second, 24 for the
    // third. The step completes when the sum of all three quest counters reaches this.
    //
    // Why cumulative rather than a per-mob "which nibble is mine" count: only White
    // Mage's mob->nibble mapping is known, and reading the SUM needs no mapping. The sum
    // is conserved -- it does not matter which type a kill (or an AoE cleave) credits, or
    // whether the credit lands a frame late -- so this is race-free and restart-proof
    // where the earlier per-nibble cap-detector was neither. Set by BaseRelicHuntGenerator
    // alongside UseQuestKillCounter.
    public int QuestCounterTarget { get; set; }

    // FATE
    public uint FateId { get; set; }

    // A predecessor FATE that must be cleared before FateId can spawn (0 = none). A few Trials of the
    // Braves book FATEs are gated this way (e.g. Breaching North Tidegate needs Gauging North Tidegate
    // first); ParticipateFateExecutor drives this prereq when the target is not yet in the FATE table.
    public uint PrerequisiteFateId { get; set; }

    // Leve / duty
    public uint LeveId { get; set; }
    public uint LevemeteDataId { get; set; }
    public uint ContentFinderConditionId { get; set; }

    // Duty territory + loop count (AutoDuty). Used by EnterDuty for the Nexus
    // light farm, where the duty is run repeatedly.
    public uint TerritoryType { get; set; }
    public int Loops { get; set; } = 1;

    // Run the duty UNSYNCED via AutoDuty (level sync off, solo old content). Set for the
    // base-relic trials and dungeons, which a current player clears solo unsynced; without
    // it AutoDuty tries to queue them synced and never starts.
    public bool Unsynced { get; set; }

    // Interaction
    public uint NpcDataId { get; set; }

    // InteractNpc: take the relic weapon OFF before talking, because the game's hand-over UI lists
    // inventory and armoury items but NOT what is currently in your hands -- so a quest step that
    // asks for the equipped relic ("Give the unfinished <weapon> to Gerolt") can never be satisfied
    // while it is equipped.
    //
    // The executor restores it on the way out if the conversation did NOT consume it (an aborted or
    // failed turn-in), so a stalled step can never leave the character bare-handed. It also records
    // the stage into RelicStageMemo first, so the engine's "which stage is this character on" read --
    // which is derived from the EQUIPPED weapon -- does not read None and regress planning during the
    // window where the weapon is deliberately off.
    public bool UnequipRelicFirst { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InteractionType Interaction { get; set; }

    // Text to match when picking a list-menu entry (e.g. the relic upgrade option
    // in a SelectString dialogue). Null/empty falls back to the first entry.
    public string? MenuOption { get; set; }

    // Items
    public uint ItemId { get; set; }
    public int Quantity { get; set; }

    // Relic upgrade
    public uint ExpectedRelicItemId { get; set; }

    // Generic wait
    public string? ConditionKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
}
