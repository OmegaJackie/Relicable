using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relicable.Model;

// How an objective's completion is detected. The controller never advances on a
// proxy event (for example a mob dying); it advances only when the authoritative
// condition below holds. Slot kinds map directly to RelicNote accessors verified
// against current FFXIVClientStructs.
public enum CompletionKind
{
    MonsterSlot,    // RelicNote.GetMonsterProgress(Slot) >= Threshold (kills, usually 3)
    DungeonSlot,    // RelicNote.IsDungeonComplete(Slot)
    FateSlot,       // RelicNote.IsFateComplete(Slot)
    LeveSlot,       // RelicNote.IsLeveComplete(Slot)
    ItemCount,      // inventory threshold (Atma, Sphere Scroll, materia)
    KeyItemCount,   // Key Items container threshold (Braves dungeon drops live in KeyItems, not the bags)
    AlexandriteCount, // Alexandrite inventory >= Configuration.AlexandriteTarget (user-set, dynamic)
    RelicItem,      // equipped relic item id changed
    LightGauge,     // Nexus light >= 2000, read from the equipped relic (GameState.IsLightGaugeFull)
    AtmaUpgraded,   // the Zenith -> Atma enhancement is done: the equipped weapon has reached the Atma tier
    AnimusUpgraded, // the Atma -> Animus enhancement is done: the equipped weapon has reached the Animus tier
    // The Sphere Scroll is at its cap (75/75; Paladin's Curtana 53 + Holy Shield 22 both full), read
    // from the game's own infused counter recorded by NovusScrollState -- so a scroll melded by hand
    // counts, not only one the engine melded. This is what ends the melding work and lets the run go
    // to Jalzahn for the Novus enhancement.
    SphereScrollFull,
    NovusUpgraded,  // the Animus -> Novus enhancement is done: the equipped weapon has reached the Novus tier
    NexusUpgraded,  // the Novus -> Nexus upgrade is done: the equipped weapon has reached the Nexus tier
    ZenithTraded,   // the Furnace trade is done: the weapon in the hands is a "<base> Zenith" form. Read off
                    // the EQUIPPED weapon, not "no bare relic held", so an alt job's parked base relic in the
                    // armoury neither blocks it nor completes it early while the traded weapon sits unequipped
    MahatmaGauge,   // Zeta: all 12 Mahatma awakened on the equipped Braves weapon (GameState.IsZetaFarmComplete)
    // A Braves stage quest (RelicObjective.BravesQuest) is in hand: accepted, or -- for the one-time
    // umbrella quest only -- completed. The four material quests are repeatable, so "completed" must
    // NOT count for them; see BravesAcceptExecutor.IsInHand.
    BravesQuestAccepted,
    // No Braves quest material is both short in the bags and sitting on a retainer, so there is
    // nothing left for the auto-fetch to pull. Read live from the planner, so it re-arms by itself if
    // more materials are entrusted later, and it is never "done" in a way that survives being wrong.
    BravesMaterialsFetched,
    RelicNoteAdvanced, // Animus book auto-advance: the active RelicNote is no longer the finished Book (a new book was bought; a repeat relic WRAPS from the last book back to book 1, so "different", not "greater")
    AllStepsDone,   // objective is purely procedural
}

public sealed class CompletionCondition
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompletionKind Kind { get; set; }

    // Slot index within the active relic note (0-based) for the *Slot kinds.
    public int Slot { get; set; }

    // Kills required for MonsterSlot (3 in the Trials of the Braves), or the
    // inventory threshold for ItemCount.
    public int Threshold { get; set; } = 1;

    public uint ItemId { get; set; }
    public uint ExpectedRelicItemId { get; set; }

    // Display-only: which book this entry belongs to (not used for logic; the
    // active note is read live from RelicNote.RelicNoteId).
    public int Book { get; set; }
}

public sealed class RelicObjective
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RelicStage Stage { get; set; }

    public int Book { get; set; }
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Enemy name for monster objectives (shown in the main window's objective-click
    // tooltip); empty for other objective types.
    public string TargetName { get; set; } = string.Empty;

    public List<StepData> Steps { get; set; } = new();

    public CompletionCondition Completion { get; set; } = new();

    // Optional: the relic weapon item id that must be equipped for this objective
    // to make progress (the Trials of the Braves book only advances with its
    // matching weapon equipped). 0 means "no explicit weapon requirement"; for
    // relic-note objectives the active-book check is used regardless.
    public uint RequiredWeaponItemId { get; set; }

    // Optional: the relic job this objective belongs to (base-relic Relic-stage
    // objectives are per job). None means "any job". The controller filters Relic-stage
    // objectives to the currently equipped job so it runs only that job's hunt.
    public RelicJob Job { get; set; } = RelicJob.None;

    // For base-relic (Relic-stage) objectives: the relic-quest sequence at which this
    // part is finished. When the live quest sequence is greater than this, the part is
    // treated as complete -- so a part done manually, or already passed, is not re-run.
    // 0 = not sequence-gated. Calibrate from /relic prereq, which shows the live sequence.
    public int CompleteAtSequence { get; set; }

    // For base-relic (Relic-stage) objectives: the LOWEST live quest sequence at which this
    // objective may run. Stops a later trial being selected before the quest has actually
    // reached its step -- e.g. the Hydra (active at sequence 12) must not run at sequence 11
    // while the beastman-hunt turn-in to Gerolt (11 -> 12) is still pending. 0 = no lower
    // bound. Pairs with CompleteAtSequence to form the [ActiveFrom, Complete) run window.
    public int ActiveFromSequence { get; set; }

    // Optional Questionable-style step verification: nibble matchers over the relic quest's
    // six work bytes (QuestWork.Variables) that mark this objective's part DONE. When
    // authored (six entries, at least one constrained) and the relic quest is active,
    // BaseRelicState verifies completion by nibble-comparing the live work bytes
    // (GameState.QuestWorkVariables) against these -- the exact mechanism Questionable uses
    // -- as a precise signal ORed with the coarse CompleteAtSequence gate. Empty = fall
    // back to the sequence gate alone. Calibrate in-game via /relic questwork.
    public List<QuestWorkValue?> CompletionQuestVariablesFlags { get; set; } = new();

    // For quest-path-driven objectives: the relic-quest sequence this objective handles.
    // The controller runs the objective whose ActiveAtSequence equals the live quest
    // sequence (so it follows the game step by step). -1 means "not sequence-driven"
    // (the generator/static objectives, selected by the normal lowest-incomplete order).
    public int ActiveAtSequence { get; set; } = -1;

    // For a one-time quest duty (the "A Relic Reborn: The Hydra" battle): the InstanceContent
    // row id. When set and that duty is already cleared (GameState.IsDutyCompleted), the
    // objective is treated complete -- the one-time battle cannot be re-entered, so the engine
    // must not try to re-queue it. 0 = not a one-time duty (repeatable Hard primals rely on
    // the live quest sequence instead, since clearing them again is allowed).
    public uint OneTimeDutyContentId { get; set; }

    // For Braves-stage dungeon objectives: the material quest (A Ponze of Flesh, Labor of Love,
    // Method in His Malice, A Treasured Mother) whose drop this dungeon yields. The controller
    // runs only the currently active material quest's objectives. Empty = not quest-gated.
    public string BravesQuest { get; set; } = string.Empty;

    // For Braves-stage dungeon objectives: the live BravesQuest sequence(s) (GameState.QuestSequence)
    // during which this drop is actually REQUESTED by the quest -- these quests hand out their dungeon
    // items in batches across several turn-in steps, so a drop only drops while the quest is at its
    // step. The controller reads the live sequence of BravesQuest and runs this dungeon only when it
    // is in this set. Empty = uncalibrated -> eligible whenever the quest is accepted (no per-step
    // gate). Calibrate the numbers in-game via /relic bravesseq (mirrors ActiveAtSequence).
    public List<int> ActiveAtQuestSequences { get; set; } = new();

    // The overworld TerritoryType this objective's work is in (the monster spawn zone, or the
    // FATE's zone). Used by the co-located-FATE optimization to batch a FATE with same-zone enemy
    // work into one teleport. 0 = unknown / not zone-based (dungeons, farms).
    public uint Territory { get; set; }
}
