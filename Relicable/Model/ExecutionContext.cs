using Relicable.External;

namespace Relicable.Model;

// Passed to every executor each tick. Bundles the companion-plugin IPC wrappers
// and the targeting helper so executors stay free of static service lookups and
// are testable against mocks (see DESIGN.md section 8).
public sealed class ExecutionContext
{
    public required NavmeshIpc Navmesh { get; init; }
    // The combat driver, selected from Configuration.Backend by CombatRouter: RSR,
    // BossMod Reborn, or none. Executors call it backend-agnostically (enable/disable/etc.).
    public required ICombatBackend Rotation { get; init; }
    public required LifestreamIpc Lifestream { get; init; }
    public required TextAdvanceIpc TextAdvance { get; init; }
    public required AutoDutyIpc AutoDuty { get; init; }
    public required BossModRebornIpc BossModReborn { get; init; }

    // Optional integration with Croizat's Bundle of Tweaks (CBT) for delegating the Atma FATE
    // farm to its Fate Tool Kit. Optional (nullable) so tests and the built-in Atma backend do
    // not need it; the AtmaCbtDriver null-checks it and CBT calls degrade safely when absent.
    public External.BundleOfTweaksIpc? Bot { get; init; }

    public required Data.Targeting Targeting { get; init; }
    public required External.ICommandHelper Commands { get; init; }
    public required Configuration Config { get; init; }

    // Novus materia planner (held + retainer materia, Universalis prices, cheapest
    // route). Optional so non-Novus executors and tests need not supply it.
    public Novus.MateriaPlanner? MateriaPlanner { get; init; }

    // The Braves (il125) material planner, so the engine can see which quest materials are short and
    // which of those are sitting on a retainer. Null when it could not be constructed.
    public Braves.BravesPlanner? BravesPlanner { get; init; }

    // Set by the controller so executors can consult the objective they belong to
    // (for example, to read the CompletionCondition for KillTarget).
    public RelicObjective? CurrentObjective { get; set; }

    // Per-step scratch state. Set by the controller when a step becomes active, so an
    // executor can time out relative to when its step started.
    public long StepStartTicks { get; set; }

    // How long ParticipateFate waits for a book FATE to spawn before rotating off it, set by the
    // controller per attempt: a SHORT glance on the first pass through a book's FATEs (skip an
    // unspawned one fast, move to the next in order), then Config.FateRotateSeconds on later passes.
    // 0 = use Config.FateRotateSeconds (the Atma "any FATE in zone" mode, which never rotates).
    public int FateWaitSeconds { get; set; }
}
