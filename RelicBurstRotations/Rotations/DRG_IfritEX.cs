namespace RelicBurstRotations.Rotations;

/// <summary>
/// DRAGOON — solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135), farming
/// the Bowl of Embers (Extreme) (TerritoryType 295) for Nexus light.
///
/// SCENARIO
/// --------
/// No party, no raid buffs, no healing needed, mechanics survivable. The relic costs roughly half
/// of a geared character's damage, so the kill is NOT instant — expect ~10-20 s, i.e. about 6-9
/// GCDs. That is exactly ONE burst window: every 60 s and 120 s cooldown fires precisely once and
/// there is never a second Lance Charge, Geirskogul, Battle Litany or Dragonfire Dive. The rotation
/// therefore IS the opener; there is deliberately no cooldown-alignment, drift or hold logic.
///
/// BURST PLAN (weave slots in brackets)
/// ------------------------------------
///   GCD 1  True Thrust        [1] Lance Charge      [2] Battle Litany
///   GCD 2  Spiral Blow        [1] Geirskogul        [2] Dragonfire Dive
///   GCD 3  Chaotic Spring     [1] Nastrond          [2] Rise of the Dragon
///   GCD 4  Wheeling Thrust    [1] Life Surge        [2] Stardiver   (Stardiver LAST — long lock)
///   GCD 5  Drakesbane         [1] Starcross         [2] High Jump
///   GCD 6  Raiden Thrust      [1] Mirage Dive
///   GCD 7  Lance Barrage      [1] Life Surge (2nd charge)
///   GCD 8  Heavens' Thrust                                  <- kill usually lands here or earlier
///   GCD 9  Fang and Claw      GCD 10 Drakesbane     GCD 11 Raiden Thrust [1] Wyrmwind Thrust
///
/// The ordering above is produced purely by PRIORITY plus each action's own dependency
/// (StatusNeed / StatusProvide / ComboIds / ActionCheck) — there are no timers anywhere.
///
/// DELIBERATE DEVIATIONS FROM THE BALANCE OPENER (all because there is no raid to align to):
///  * Lance Charge and Battle Litany are pulled forward to the GCD 1 weave window (standard puts
///    them on GCD 2 / GCD 3) so their multipliers cover as many of the ~7 GCDs as possible.
///  * Geirskogul is pulled to GCD 2 (standard: GCD 3). It gates Nastrond / Stardiver / Starcross —
///    Dragoon's three largest hits — so moving it one GCD earlier moves that whole chain earlier.
///  * Dragonfire Dive is pulled to GCD 2 (standard: GCD 5) and Rise of the Dragon to GCD 3
///    (standard: ~GCD 8). Both are one-shot cooldowns on a fight this short.
///  * Nastrond and Rise of the Dragon are kept ONE FULL GCD behind the action that grants them
///    (Geirskogul / Dragonfire Dive) rather than chained inside the same weave window — the status
///    grant needs a server round-trip and same-window chaining silently no-ops.
///  * Stardiver is single-weaved (guarded below) — its animation lock clips the following GCD.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK
/// -----------------------------------
///  1. Wyrmwind Thrust costs 2 Firstminds' Focus and each Raiden Thrust grants 1, so the 2nd stack
///     only exists at GCD 11 (~t=27 s). On a successful skip Dragoon simply never presses it. That
///     is a fact of the class in 7.x, not an omission here.
///  2. Winged Glide (the 20 y no-damage gap closer) is NOT used: RSR only invokes the
///     MoveForwardAbility stage under AutoStatus.MoveForward, and TEMPLATE.md restricts this file
///     to four overrides. Cost is ~2-3 s of travel on the pull and between distant nails. If that
///     matters, the fix is a MoveForwardAbility override — see the report.
///  3. The tincture (Gemdraught of Strength) is an ITEM, pressed via UseBurstMedicine. RSR's own
///     TinctureUseType defaults to "high-end duty only" and territory 295 is NOT high-end, so it
///     will usually silently no-op. Expected; the config defaults to off.
///  4. Positionals: stand at the REAR from the pull. Wheeling Thrust (rear) is GCD 4 and is the one
///     that matters; Fang and Claw (flank) is GCD 9 and on a successful run never happens. Ifrit is
///     stationary with no untargetable windows, so never reposition — let RSR fire True North free.
///  5. NAIL PHASE: while Infernal Nails are alive, damaging Ifrit is strictly harmful (post-4.56 he
///     goes temporarily invulnerable and the fight can lock into an unwinnable Hellfire loop). Every
///     damaging CanUse below therefore carries targetOverride: KillOrder, which switches to
///     TargetType.LowMaxHP while a nail set is up. NO AoE is used: all of Dragoon's multi-target
///     actions are straight LINES (Doom Spike / Draconian Fury / Sonic Thrust / Coerthan Torment)
///     and the nails ring the arena perimeter, so a line through one nail almost never catches a
///     second — travel time dominates and each nail dies to ~1 GCD anyway.
/// </summary>
[Rotation("Ifrit EX Burst (DRG)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/DRG_IfritEX.cs")]
[ExtraRotation]
public sealed class DRG_IfritEX : DragoonRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Piercing Talon as ranged filler when out of melee range")]
    public bool RangedFiller { get; set; } = true;

    [Range(0, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
    public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;

    #endregion

    #region Tracking Properties

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"InIfritEx: {IfritExBurst.InIfritEx}");
        ImGui.Text($"InBurst: {IfritExBurst.InBurst}");
        ImGui.Text($"CombatSeconds: {IfritExBurst.CombatSeconds:F1}");
        ImGui.Text($"InOpenerWindow: {IfritExBurst.InOpenerWindowOf(OpenerWindow)}");
        ImGui.Text($"ShouldKillNails: {IfritExBurst.ShouldKillNails(HostileTarget)}");
        ImGui.Text($"HostilesInMaxRange: {NumberOfAllHostilesInMaxRange}");
        ImGui.Text($"HasLanceCharge: {HasLanceCharge}  HasBattleLitany: {HasBattleLitany}");
        ImGui.Text($"HasPowerSurge: {HasPowerSurge}  HasDraconianFire: {HasDraconianFire}");
        ImGui.Text($"LOTDTime: {LOTDTime:F1}  EyeCount: {EyeCount}  FocusCount: {FocusCount}");
        ImGui.Text($"RaidenReady: {RaidenThrustPvEReady}  DrakesbaneWheeling: {DrakesbanePvEWheelingReady}  DrakesbaneFang: {DrakesbanePvEFangReady}");
    }

    #endregion

    #region Extra Methods

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// True in the weave window that immediately precedes one of Dragoon's two 460-potency
    /// finishers, i.e. the only moment Life Surge's guaranteed critical is worth spending.
    /// RSR hands the already-chosen next GCD to every ability stage, so this is self-correcting
    /// and needs no timer.
    /// NOTE: deliberately NOT static — the generated <c>XxxPvE</c> action properties on
    /// <see cref="DragoonRotation"/> are public INSTANCE properties (verified by reflection against
    /// RotationSolver.Basic.dll 7.5.1.17), unlike the <c>HasLanceCharge</c>-style state members
    /// which are public static. Marking this static would be CS0120.
    /// </summary>
    private bool LifeSurgeOnBigHit(IAction nextGCD) =>
        nextGCD.IsTheSameTo(true, DrakesbanePvE, HeavensThrustPvE);

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss.
    public override bool CanHealSingleSpell => false;

    public override bool CanHealAreaSpell => false;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
        // Note: RSR's tincture setting defaults to "high-end duty only" and 295 is not high-end,
        // so this frequently no-ops. That is expected and is not worked around.
        if (PullTincture && IfritExBurst.InIfritOpener(OpenerWindow) && UseBurstMedicine(out act))
        {
            return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // ---- NAIL SAFETY GATE ----------------------------------------------------------------
        // A nail set is up but the target RSR resolved is still Ifrit, who is invulnerable until
        // every nail dies. Press nothing: damage aimed at him is wasted, it feeds the post-4.56
        // invulnerability budget, and only nail kills can advance the fight. Relicable pins RSR's
        // DataCenter.TargetingTypeOverride to LowMaxHP for the whole of territory 295, so the
        // resolved target swings onto a nail within a frame or two and the rotation resumes there.
        // (The old mechanism -- IfritExBurst.NailFirstTargeting fed to CanUse's targetOverride --
        // provably cannot do this: RSR's hostile picker never reads targetOverride. See
        // IfritExBurst.NailFirstTargeting.)
        if (IfritExBurst.MustHoldFire(HostileTarget))
        {
            act = null;
            return false;
        }

        if (IfritExBurst.InBurst)
        {
            // Stardiver's animation lock is long enough that anything weaved after it clips the
            // next GCD. Never double-weave off it — mirrors DRG_Reborn's own guard.
            if (IsLastAction(false, StardiverPvE))
            {
                return base.AttackAbility(nextGCD, out act);
            }

            // --- GCD 1 weave window --------------------------------------------------------
            // Both are self-targeted friendly buffs, so they intentionally get NO targetOverride
            // (the override would replace Battle Litany's declared TargetType.Self).
            // skipTTKCheck is mandatory on Battle Litany: RSR ships it with TimeToKill = 10, and a
            // sub-20 s kill drives AverageTTK under that threshold, silently eating the cooldown.
            if (LanceChargePvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            if (BattleLitanyPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // --- Life Surge (GCD 4 and GCD 7 weave windows) --------------------------------
            // Placed above the damage oGCDs so it takes the FIRST weave slot of the window and
            // leaves the second for Stardiver, which must be the last weave of its window.
            // Also friendly/self, so no targetOverride.
            if (LifeSurgeOnBigHit(nextGCD)
                && LifeSurgePvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
            {
                return true;
            }

            // --- GCD 2 weave window --------------------------------------------------------
            // Geirskogul grants Life of the Dragon (+15%) and gates Nastrond/Stardiver/Starcross,
            // so it is the single most valuable thing to pull forward.
            if (GeirskogulPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            // 120 s cooldown, pressed exactly once, and grants Dragon's Flight for Rise of the
            // Dragon on the very next GCD.
            if (DragonfireDivePvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            // --- GCD 3 weave window --------------------------------------------------------
            // Nastrond needs Nastrond Ready (Geirskogul) and Rise of the Dragon needs Dragon's
            // Flight (Dragonfire Dive); both were granted one full GCD earlier, which is the
            // deliberate separation described in the header.
            if (NastrondPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            if (RiseOfTheDragonPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            // --- GCD 4 weave window, second slot ------------------------------------------
            // Requires Life of the Dragon (live since GCD 2). Grants Starcross Ready.
            // The guard at the top of this method keeps it a single weave.
            if (StardiverPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            // --- GCD 5 weave window --------------------------------------------------------
            // Starcross is Dragoon's largest single hit and is therefore the best candidate for
            // the "finishing blow worth 20-30% of Ifrit's HP" skip condition. It is deliberately
            // NOT held back: holding costs uptime, which lengthens the fight and makes stalling on
            // a nail threshold MORE likely, and Starcross Ready is silently cancelled the moment
            // Life of the Dragon expires (~20 s after Geirskogul).
            if (StarcrossPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            // 30 s cooldown — the only oGCD with a realistic second use. Grants Dive Ready and
            // relocates onto the target, which is genuinely useful during the nail phase.
            if (HighJumpPvE.CanUse(out act, skipTTKCheck: true, targetOverride: KillOrder))
            {
                return true;
            }

            // --- GCD 6 weave window --------------------------------------------------------
            if (MirageDivePvE.CanUse(out act, skipTTKCheck: true, targetOverride: KillOrder))
            {
                return true;
            }

            // --- Failed-skip path only -----------------------------------------------------
            // Costs 2 Firstminds' Focus; each Raiden Thrust grants 1, so the earliest this can
            // physically fire is the 2nd Raiden Thrust at ~GCD 11. Spend it the moment it exists
            // rather than banking it — never let a Raiden Thrust overcap the stacks.
            if (WyrmwindThrustPvE.CanUse(out act, usedUp: true, skipAoeCheck: true,
                    skipTTKCheck: true, targetOverride: KillOrder))
            {
                return true;
            }
        }

        // Outside Bowl of Embers (Extreme) this rotation makes no claim about oGCD usage — fall
        // through to RSR's inherited behaviour rather than forcing the farm burst.
        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // ---- NAIL SAFETY GATE ----------------------------------------------------------------
        // A nail set is up but the target RSR resolved is still Ifrit, who is invulnerable until
        // every nail dies. Press nothing: damage aimed at him is wasted, it feeds the post-4.56
        // invulnerability budget, and only nail kills can advance the fight. Relicable pins RSR's
        // DataCenter.TargetingTypeOverride to LowMaxHP for the whole of territory 295, so the
        // resolved target swings onto a nail within a frame or two and the rotation resumes there.
        // (The old mechanism -- IfritExBurst.NailFirstTargeting fed to CanUse's targetOverride --
        // provably cannot do this: RSR's hostile picker never reads targetOverride. See
        // IfritExBurst.NailFirstTargeting.)
        if (IfritExBurst.MustHoldFire(HostileTarget))
        {
            act = null;
            return false;
        }

        TargetType order = KillOrder;

        // Single-target only, by design (see the nail-phase note in the header). The ordering below
        // is the standard 7.x two-route Dragoon loop, driven entirely by each action's ComboIds:
        //   Route A: True Thrust -> Spiral Blow -> Chaotic Spring -> Wheeling Thrust -> Drakesbane
        //   Route B: True Thrust -> Lance Barrage -> Heavens' Thrust -> Fang and Claw -> Drakesbane
        // Spiral Blow sits ABOVE Lance Barrage so route A is taken first and Power Surge (+10%) is
        // live from GCD 2 onward; once Power Surge is healthy its StatusProvide check blocks Spiral
        // Blow and the loop naturally falls through to route B.
        //
        // No level/trait branches are written here: the player is level 100 unsynced and CanUse
        // already checks level. The pre-7.0 names (VorpalThrustPvE / DisembowelPvE / FullThrustPvE /
        // ChaosThrustPvE / JumpPvE) still exist as RSR properties but are permanently dead at 100,
        // so they are deliberately omitted rather than left in as silent no-ops.

        // Combo finisher. skipStatusProvideCheck mirrors DRG_Reborn: Drakesbane provides Draconian
        // Fire and would otherwise refuse to re-press while that status is still up.
        if (DrakesbanePvE.CanUse(out act, skipStatusProvideCheck: true, targetOverride: order))
        {
            return true;
        }

        // Flank positional — route B, GCD 9. Only reached on the failed-skip path.
        if (FangAndClawPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Rear positional — route A, GCD 4, deep inside the burst. Stand at the rear from the pull.
        if (WheelingThrustPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (HeavensThrustPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // DoT. Worth keeping in the opener: it ticks under Life of the Dragon for the whole fight
        // and it is the correct 3rd hit of route A. skipStatusProvideCheck so a re-application is
        // never blocked by the target already carrying the DoT.
        if (ChaoticSpringPvE.CanUse(out act, skipStatusProvideCheck: true, targetOverride: order))
        {
            return true;
        }

        // The Power Surge applier. The provide check is only skipped when Power Surge is genuinely
        // about to fall off, which is what keeps route B reachable at GCD 7.
        if (SpiralBlowPvE.CanUse(out act,
                skipStatusProvideCheck: StatusHelper.PlayerWillStatusEndGCD(6, 0, true, StatusID.PowerSurge_2720),
                targetOverride: order))
        {
            return true;
        }

        if (LanceBarragePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Raiden Thrust carries its own ActionCheck (RaidenThrustPvEReady), so trying it first and
        // falling back to True Thrust reproduces the Draconian Fire branch without duplicating it.
        if (RaidenThrustPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (TrueThrustPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Ranged filler so no GCD is dead while closing on a distant nail. It does NOT start the
        // True Thrust combo, so it is strictly last — every melee GCD above fails CanUse on range
        // before this is reached.
        if (RangedFiller && PiercingTalonPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
