using System.Collections.Generic;
using System.Numerics;

namespace Relicable.BaseRelic;

// Deserialization model for the qstxiv / Questionable quest-path JSON format
// (https://qstxiv.github.io/schema/quest-v1.json). A quest path is an ordered set of
// quest SEQUENCES; each sequence number matches the game's live quest sequence
// (QuestManager.GetQuestSequence), so the runner executes the step(s) for the current
// sequence and advances when the game does -- the authoritative, no-re-farm model.
//
// Only the fields the relic line needs are modelled; unknown JSON keys are ignored.
// Position is read through a DTO because System.Text.Json does not bind Vector3's
// fields by default.
public sealed class QuestPath
{
    public string Author { get; set; } = string.Empty;
    public List<QuestPathSequence> QuestSequence { get; set; } = new();
}

public sealed class QuestPathSequence
{
    public int Sequence { get; set; }
    public List<QuestPathStep> Steps { get; set; } = new();
}

public sealed class QuestPathStep
{
    // Target NPC / object id for Interact / AcceptQuest / CompleteQuest (0 if none).
    public uint DataId { get; set; }

    // World position to navigate to (the game's Vector3, Y = height).
    public QuestPathPosition? Position { get; set; }

    public uint TerritoryId { get; set; }

    // AcceptQuest, Interact, CompleteQuest, WalkTo, Combat, Duty, ... (qstxiv names), plus
    // InteractObject (ours -- see QuestPathLoader.MapStep).
    public string InteractionType { get; set; } = string.Empty;

    public bool Fly { get; set; }
    public float StopDistance { get; set; } = 3f;

    // ---- InteractObject (a world object, not an NPC) ----
    // The object's in-game name, e.g. "Treasure Coffer". The PRIMARY needle: the finder is
    // name-driven and ObjectKind-tolerant, because a quest coffer's live ObjectKind (EventObj
    // vs Treasure) is an unverified seam. DataId above, when set, is an optional STRONGER
    // match (ids do not localize; names do). Either alone is enough to find the object, so
    // set both wherever both are known.
    public string? ObjectName { get; set; }

    // ---- Combat (relic beastmen) ----
    public string? EnemyName { get; set; }
    public int KillCount { get; set; }

    // ---- Duty (relic trials) ----
    // Either a TerritoryType directly, or a ContentFinderCondition name to resolve one.
    public uint DutyTerritoryType { get; set; }
    public string? DutyName { get; set; }
}

// {X, Y, Z} object as written in the quest-path JSON. Properties (not fields) so
// System.Text.Json binds them; ToVector3 yields the game world position.
public sealed class QuestPathPosition
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3 ToVector3() => new(X, Y, Z);
}
