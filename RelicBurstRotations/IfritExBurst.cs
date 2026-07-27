namespace RelicBurstRotations;

// ============================================================================================
// DEPLOYMENT — READ BEFORE ASSUMING ANY OF THIS RUNS
// ============================================================================================
// RSR 7.5.1.17 does NOT load rotations from disk. Verified three ways:
//   * upstream RotationUpdater.LoadBuiltInRotations() is
//         List<Assembly> assemblies = [typeof(RotationUpdater).Assembly];
//     and there is no other loader;
//   * a full string dump of the installed RotationSolver.dll contains no directory path, no
//     "*.dll" search pattern, and no Assembly.LoadFrom/GetFiles literal;
//   * the shipped rotations (RotationSolver.RebornRotations.*, RotationSolver.ExtraRotations.*)
//     are compiled INTO RotationSolver.dll.
// pluginConfigs\RotationSolver\Rotations\RebornRotations.dll is a dead leftover from an older
// RSR and is never read.
//
// Consequence: building this project produces a DLL that RSR can never discover. The ONLY way
// these rotations run is to compile the .cs files in this folder into a fork of the
// RotationSolver plugin assembly and install that fork over the official one:
//   1. clone FFXIV-CombatReborn/RotationSolverReborn at tag 7.5.1.17;
//   2. copy IfritExBurst.cs and Rotations\*.cs into the RotationSolver project (the file-scoped
//      namespace RelicBurstRotations[.Rotations] is preserved, so no wiring change is needed);
//   3. build and replace RotationSolver.dll in the installed plugin directory.
// Note the fork is REQUIRED for compilation too, not just for loading: RotationSolver.Basic ships
// [assembly: InternalsVisibleTo("RotationSolver")], so anything reaching RSR internals only
// compiles inside an assembly named RotationSolver.
//
// Dalamud auto-updates RSR into a new version-stamped directory, so every RSR release undoes the
// fork. Relicable therefore ships AutoSwapIfritBurstRotation OFF by default and surfaces the
// "not found in your RotationSolver build" state in its config window instead of failing quietly.
//
// This project (RelicBurstRotations.csproj) exists ONLY as a compile-verification harness for the
// source drop. Its output is not deployed anywhere and must not be copied into any plugin folder.
// ============================================================================================

/// <summary>
/// Shared state helpers for the solo/unsynced Bowl of Embers (Extreme) relic-light farm.
/// Every <c>*_IfritEX</c> rotation in this assembly calls into here so that the "are we farming,
/// are we still in the opener, should we be hitting nails" questions have exactly one answer.
///
/// ACCESSIBILITY CONTRACT (verified by reflection against RotationSolver.Basic.dll 7.5.1.17):
/// this is a plain static class, NOT a CustomRotation subclass, so it can only touch
/// <c>public static</c> members of <see cref="CustomRotation"/>. The following are
/// <c>protected static</c> and are therefore NOT reachable from here — a rotation must pass them
/// in as arguments: <c>HostileTarget</c>, <c>CurrentTarget</c>, <c>Target</c>, <c>Player</c>,
/// <c>AllHostileTargets</c>, <c>AllTargets</c>, <c>CombatElapsedLess</c>, <c>CombatElapsedLessGCD</c>.
/// </summary>
public static class IfritExBurst
{
    #region Territory

    /// <summary>TerritoryType id of "the Bowl of Embers (Extreme)". (292 = Hard, 1045 = Normal.)</summary>
    public const uint BowlOfEmbersExtremeTerritoryId = 295;

    /// <summary>
    /// Same id pre-narrowed to the <see cref="ushort"/> that
    /// <c>CustomRotation.IsInTerritory(ushort)</c> actually takes.
    /// </summary>
    public const ushort BowlOfEmbersExtremeTerritoryIdU16 = (ushort)BowlOfEmbersExtremeTerritoryId;

    /// <summary>True while the player is standing in Bowl of Embers (Extreme).</summary>
    public static bool InIfritEx => CustomRotation.IsInTerritory(BowlOfEmbersExtremeTerritoryIdU16);

    #endregion

    #region Burst state

    /// <summary>
    /// The farm state: we are in Bowl of Embers (Extreme) AND in combat. Gate every
    /// "dump everything on cooldown, ignore normal cooldown discipline" branch on this.
    /// </summary>
    public static bool InBurst => InIfritEx && CustomRotation.InCombat;

    /// <summary>
    /// Seconds since combat started, or 0 when out of combat.
    /// (<c>CustomRotation.CombatTime</c> is already 0 out of combat; clamped here anyway.)
    /// </summary>
    public static float CombatSeconds => CustomRotation.InCombat ? CustomRotation.CombatTime : 0f;

    /// <summary>
    /// Default length of the "opener" window in seconds. The whole kill is expected to last
    /// 10-20 s, so this deliberately covers most of the fight.
    /// </summary>
    public const float DefaultOpenerWindowSeconds = 20f;

    /// <summary>True while we are within <see cref="DefaultOpenerWindowSeconds"/> of the pull.</summary>
    public static bool InOpenerWindow => InOpenerWindowOf(DefaultOpenerWindowSeconds);

    /// <summary>True while we are within <paramref name="seconds"/> of the pull.</summary>
    public static bool InOpenerWindowOf(float seconds) =>
        CustomRotation.InCombat && CustomRotation.CombatTime <= seconds;

    /// <summary>
    /// True for the first <paramref name="seconds"/> of an Ifrit EX pull only. The normal gate for
    /// "fire this burst cooldown right now, no alignment logic".
    /// </summary>
    public static bool InIfritOpener(float seconds = DefaultOpenerWindowSeconds) =>
        InIfritEx && InOpenerWindowOf(seconds);

    #endregion

    #region Infernal Nails

    /// <summary>
    /// Every <see cref="NPCName"/> entry RSR 7.5.1.17 knows for "Infernal Nail"
    /// (verified: NPCName.InfernalNail = 1186, InfernalNail_10043, InfernalNail_10044).
    /// </summary>
    public static readonly NPCName[] InfernalNailNames =
    [
        NPCName.InfernalNail,
        NPCName.InfernalNail_10043,
        NPCName.InfernalNail_10044,
    ];

    /// <summary>True when <paramref name="chara"/> is an Infernal Nail.</summary>
    public static bool IsInfernalNail(IBattleChara? chara)
    {
        if (chara is null)
        {
            return false;
        }

        foreach (NPCName name in InfernalNailNames)
        {
            if (ObjectHelper.IsNamed(chara, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when more than one hostile is up in Ifrit EX. Ifrit is always one of them, so a second
    /// hostile means nails have spawned. Uses only <c>public static</c> counters, so it works from
    /// anywhere without needing the protected enemy list.
    /// </summary>
    public static bool MultipleHostilesPresent =>
        InIfritEx && CustomRotation.NumberOfAllHostilesInMaxRange > 1;

    /// <summary>
    /// The main question a rotation asks: "should I be hitting nails instead of Ifrit right now?".
    /// True when we are in Ifrit EX and either the current hostile target already IS a nail,
    /// or extra hostiles have spawned (i.e. a nail set is up).
    ///
    /// Damaging Ifrit while nails are alive is strictly harmful post-patch-4.56 (he goes
    /// temporarily invulnerable), so when this is true a rotation must retarget onto the nails.
    /// </summary>
    /// <param name="currentHostileTarget">
    /// Pass the rotation's <c>HostileTarget</c> — it is <c>protected static</c> on
    /// <see cref="CustomRotation"/> and unreachable from this class.
    /// </param>
    public static bool ShouldKillNails(IBattleChara? currentHostileTarget) =>
        InIfritEx && (IsInfernalNail(currentHostileTarget) || MultipleHostilesPresent);

    /// <summary>
    /// DEPRECATED — always returns <c>default</c>, i.e. it is a deliberate no-op.
    ///
    /// This used to return <see cref="TargetType.LowMaxHP"/> so that <c>CanUse(..., targetOverride:)</c>
    /// would prefer nails. That does not work and never did. Verified against
    /// <c>RotationSolver.Basic/Actions/ActionTargetInfo.cs</c> at tag 7.5.1.17:
    /// <c>FindTargetByType</c>'s <c>targetOverride</c> switch has no <c>LowMaxHP</c> case, so it falls
    /// through to <c>_ =&gt; isFriendly ? FindFriendly() : FindHostile()</c>. Only <c>FindFriendly()</c>
    /// reads <c>targetOverride</c>; <c>FindHostile()</c> -&gt; <c>FindHostileRaw()</c> sorts purely on
    /// <c>DataCenter.TargetingType</c> and never sees the override at all. Passing anything here
    /// therefore had zero effect on which HOSTILE was picked.
    ///
    /// Nail priority is now driven from two places that RSR actually reads:
    ///   * Relicable sets <c>DataCenter.TargetingTypeOverride = TargetingType.LowMaxHP</c> for the
    ///     duration of territory 295 (<c>Relicable.External.RsrTargetingOverride</c>), which IS the
    ///     field <c>FindHostileRaw</c> sorts on; and
    ///   * <see cref="MustHoldFire"/> below, which makes every rotation refuse to press anything at
    ///     all while a nail set is up and the resolved target is still Ifrit.
    ///
    /// The method is kept (returning <c>default</c>) so the <c>targetOverride:</c> arguments already
    /// threaded through every rotation stay valid and provably inert rather than misleading.
    /// </summary>
    public static TargetType NailFirstTargeting(IBattleChara? currentHostileTarget) => default;

    /// <summary>
    /// The hard safety gate. True when a nail set is up but the action we are about to press would
    /// land on something that is NOT a nail — i.e. on Ifrit, who is invulnerable until every nail
    /// dies. In that state the correct number of buttons to press is zero: damage aimed at Ifrit is
    /// wasted at best and feeds the post-4.56 invulnerability budget at worst, and the run can only
    /// be won by killing nails.
    ///
    /// Every rotation in this assembly returns false out of <c>GeneralGCD</c> and
    /// <c>AttackAbility</c> while this is true, so a nail phase degrades to a short stall (until
    /// RSR's LowMaxHP targeting swings onto a nail) instead of an unwinnable Ifrit lock.
    /// </summary>
    /// <param name="currentHostileTarget">The rotation's <c>HostileTarget</c>.</param>
    public static bool MustHoldFire(IBattleChara? currentHostileTarget) =>
        ShouldKillNails(currentHostileTarget) && !IsInfernalNail(currentHostileTarget);

    #endregion
}
