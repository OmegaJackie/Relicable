namespace Relicable.External;

// Abstraction over the combat driver so the executors are backend-agnostic. Both
// Rotation Solver Reborn (RotationSolverIpc) and BossMod Reborn
// (BossModRebornCombatBackend) implement this; CombatRouter picks the active one from
// Configuration.Backend at call time.
//
// The surface is exactly what the kill / FATE / leve / treasure-map executors call
// through ctx.Rotation. Relicable itself sets the hard target (KillTarget marks and
// hard-targets the mob; the FATE/leve executors call EngageNearestHostile before
// enabling), so a backend only has to run the rotation on the current target -- it
// does not need to select targets or drive movement.
public interface ICombatBackend
{
    // True when the backend's control IPC is live (the plugin is loaded and exposing
    // its gate). Used for diagnostics; executors call the verbs regardless and each
    // no-ops safely when its plugin is absent.
    bool Available { get; }

    // Full autorotation: used in FATEs / treasure-map fights where enemies are already
    // hostile. Relicable keeps a hostile hard-targeted; the backend attacks it.
    void EnableAuto();

    // Assist/pull the current hard target. The open-world relic grind uses this to pull
    // a NEUTRAL, un-aggroed note mob that only a manual pull engages.
    void EnableManual();

    // Stop combat (idempotent; edge-triggered inside each backend so per-tick calls are
    // cheap).
    void Disable();

    // Backend-specific pre-fight setup for FATE targeting (RSR sets its hostile-type and
    // FATE-priority settings; BossMod Reborn acts on the hard target and needs nothing here).
    void ConfigureForFate();

    // True when this backend selects and attacks FATE mobs on its OWN (RSR Auto mode with
    // the FATE settings), so the FATE executor should hand targeting over: get into the
    // ring, level-sync, ground, then ConfigureForFate + EnableAuto and let the backend
    // pick/pull mobs, WITHOUT Relicable hard-targeting and Attack1-marking each one per
    // tick. False for backends that only rotate on Relicable's current hard target
    // (BossMod Reborn, none), where the executor keeps setting the target itself.
    bool OwnsFateTargeting { get; }

    // Force the next Enable/Disable to re-send even if the cached state is unchanged
    // (e.g. the plugin reset its own mode/preset, or we just left a duty).
    void ResyncNextDispatch();
}

// Backend for Configuration.CombatBackend.None: does nothing. Selecting "None" means
// Relicable navigates and targets but drives no rotation, so the player (or another
// tool) handles combat.
public sealed class NullCombatBackend : ICombatBackend
{
    public bool Available => false;
    public void EnableAuto() { }
    public void EnableManual() { }
    public void Disable() { }
    public void ConfigureForFate() { }
    public bool OwnsFateTargeting => false;
    public void ResyncNextDispatch() { }
}

// Routes each call to the backend selected in config, so the choice can change live
// (the Combat backend dropdown) without rebuilding the ExecutionContext. When the
// selection changes it disables and resyncs the backend we were using, so switching
// mid-session cannot leave the old driver latched "on".
public sealed class CombatRouter : ICombatBackend
{
    private readonly Configuration _config;
    private readonly RotationSolverIpc _rsr;
    private readonly BossModRebornCombatBackend _bossModReborn;
    private readonly NullCombatBackend _none = new();
    private Configuration.CombatBackend _last;

    public CombatRouter(Configuration config, RotationSolverIpc rsr, BossModRebornCombatBackend bossModReborn)
    {
        _config = config;
        _rsr = rsr;
        _bossModReborn = bossModReborn;
        _last = config.Backend;
    }

    private ICombatBackend For(Configuration.CombatBackend b) => b switch
    {
        Configuration.CombatBackend.BossModReborn => _bossModReborn,
        Configuration.CombatBackend.RotationSolverReborn => _rsr,
        _ => _none,
    };

    // Resolve the active backend, stopping the previously-selected one on a change so a
    // switched-away driver does not keep running.
    private ICombatBackend Active()
    {
        var current = _config.Backend;
        if (current != _last)
        {
            var old = For(_last);
            old.Disable();
            old.ResyncNextDispatch();
            _last = current;
        }
        return For(current);
    }

    public bool Available => Active().Available;
    public void EnableAuto() => Active().EnableAuto();
    public void EnableManual() => Active().EnableManual();
    public void Disable() => Active().Disable();
    public void ConfigureForFate() => Active().ConfigureForFate();
    public bool OwnsFateTargeting => Active().OwnsFateTargeting;
    public void ResyncNextDispatch() => Active().ResyncNextDispatch();
}
