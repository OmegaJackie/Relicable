namespace RelicBurstRotations.Rotations;

/// <summary>
/// SCHOLAR — solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135), farming
/// the Bowl of Embers (Extreme) (TerritoryType 295) for Nexus light.
///
/// WHY THIS ONE IS DIFFERENT FROM THE OTHER NINE
/// ---------------------------------------------
/// Every other job in this assembly is built around the documented "skip" strategy: land a
/// finishing blow worth 20-30% of Ifrit's HP and never see a nail. Scholar structurally cannot do
/// that. Its whole damage kit is one 320-potency 1.5 s filler (Broil IV), a 220-potency instant
/// (Ruin II), an 850-over-30 s DoT (Biolysis), a 100-potency oGCD (Energy Drain) and a
/// 700-over-15 s AoE DoT (Baneful Impaction). Its biggest single hit is a critting Broil IV.
/// The fight brief names SCH explicitly as a job that cannot do skip strategies.
///
/// So this rotation ASSUMES THE NAIL PHASE HAPPENS — all three sets, 2-3.5 minutes of wall clock.
/// Consequences baked into the code below:
///   * Chain Stratagem (120 s) will come up 2-3 times, Aetherflow (60 s) 3-4 times, Dissipation
///     (180 s) once or twice. This is a real sustain loop, not a one-shot dump.
///   * Everything is still fired on cooldown with zero alignment/hold logic — there is no second
///     raid buff to align to, only your own next Chain Stratagem.
///   * The one genuine hold is Chain Stratagem during a nail set (see AttackAbility).
///
/// BURST PLAN, IN ORDER (from the job plan; RSR expresses it as a priority list, not a script)
/// ------------------------------------------------------------------------------------------
///   pre-pull : Summon Eos            - Dissipation hard-requires an active faerie
///   pre-pull : Grade 3 Gemdraught of Mind (tincture; see the trap note below)
///   GCD  1   : Broil IV              - the pull itself; ranged, so zero travel loss
///   weave    : Chain Stratagem       - +10% crit on Ifrit for 20 s, grants Impact Imminent
///   GCD  2   : Biolysis (instant)    - snapshots the crit debuff into all 30 s of the DoT
///   weave x2 : Baneful Impaction, Aetherflow
///   GCD 3-5  : Broil IV, weaving Energy Drain x3 (dump the gauge)
///   weave    : Dissipation           - refills the gauge; +20% healing and the faerie dismissal
///                                      are both worthless solo, so it is a free 300 potency
///   GCD 6-8  : Broil IV, weaving Energy Drain x3
///   then     : sustain loop - Broil IV filler, Biolysis on refresh, every oGCD on cooldown,
///              Ruin II / Swiftcast / Expedient while running the nail ring.
///
/// DEVIATION FROM THE BALANCE'S OPENER (deliberate): the standard opener weaves Dissipation before
/// Aetherflow so its +20% healing potency lands where the party needs it. Solo that buff is worth
/// nothing, so Aetherflow goes first purely to start its 60 s timer ~15 s earlier — over a 2-3
/// minute fight that is one extra full Aetherflow cycle, i.e. three extra Energy Drains.
///
/// NAIL RULE (load-bearing): while any Infernal Nail is alive, deal ZERO damage to Ifrit. Since
/// patch 4.56 he gains temporary invulnerability once a damage budget is exceeded during a nail
/// set, and the failure state is an unrecoverable Hellfire loop. <see cref="KillOrder"/> retargets
/// every damage action onto the low-max-HP nails; Biolysis is suppressed entirely (it needs ~12 s
/// of ticking to beat a Broil IV and nails should not live that long); Chain Stratagem is held for
/// Ifrit because its crit window belongs on the boss.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK
/// -----------------------------------
///  1. TINCTURE: RSR's UseBurstMedicine silently refuses when its tincture setting is the default
///     "high-end duty only" — territory 295 is an ARR EX trial and is NOT flagged high-end. The
///     PullTincture option therefore no-ops unless the user changes that RSR setting. Expected;
///     per TEMPLATE.md we do not work around it. SCH's MedicineType is Mind, so it must be a
///     Grade 3 Gemdraught of Mind.
///  2. NO COUNTDOWN on a solo duty pull, so CountDownAction never runs. The tincture and the
///     pre-pull faerie live in EmergencyAbility / GeneralGCD instead.
///  3. OpenerWindow defaults to 20 s (the assembly-wide default), but for SCH it only marks
///     "still frontloading" — it is NOT the expected kill time. Nothing important is gated on it
///     except the tincture and the "don't spend a GCD re-summoning Eos mid-opener" guard.
///  4. Chain Stratagem carries a TimeToKill = 10 action config in RSR, so skipTTKCheck: true is
///     mandatory on it or it silently stops firing exactly when the target is nearly dead.
///  5. UNVERIFIED: whether applying Chain Stratagem (a pure debuff, zero damage) to Ifrit during a
///     nail set counts against the 4.56 invulnerability budget. It should not, but the patch note
///     is vague, so the conservative choice is taken here: Chain Stratagem is HELD while nails
///     are up. Flip HoldChainStratagemDuringNails if testing proves it safe.
///  6. Outside territory 295 this behaves as an ordinary (if spartan) solo SCH damage rotation:
///     every Ifrit-specific branch is either gated on IfritExBurst.InBurst or degrades to a no-op,
///     and every override still ends in the base call.
/// </summary>
[Rotation("Ifrit EX Burst (SCH)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/SCH_IfritEX.cs")]
[ExtraRotation]
public sealed class SCH_IfritEX : ScholarRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [Range(0, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
    public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;

    [RotationConfig(CombatType.PvE, Name = "Summon Eos (required for Dissipation)")]
    public bool SummonFairy { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Hold Chain Stratagem while Infernal Nails are alive (recommended)")]
    public bool HoldChainStratagemDuringNails { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Swiftcast a Broil while moving between nails")]
    public bool SwiftcastWhileMoving { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Expedient for movement speed between nails")]
    public bool ExpedientWhileMoving { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Art of War II when 2+ enemies are within 5y (rarely worth it)")]
    public bool UseArtOfWar { get; set; } = false;

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
        ImGui.Text($"AetherflowStacks: {SCHAetherFlowStacks}");
        ImGui.Text($"HasImpactImminent: {HasImpactImminent}");
        ImGui.Text($"HasDissipation: {HasDissipation}");
        ImGui.Text($"FairyDismissed: {FairyDismissed}");
        ImGui.Text($"IsMoving: {IsMoving}");
    }

    #endregion

    #region Extra Methods

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// True while we must not touch Ifrit (a nail set is up). Outside territory 295 this is always
    /// false, so every branch guarded by it behaves normally elsewhere.
    /// </summary>
    private bool NailsUp => IfritExBurst.ShouldKillNails(HostileTarget);

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss. MANDATORY on SCH — the
    // shipped SCH_Reborn counts alive party healers and turns GCD healing ON when there is only
    // one, which solo is always.
    // Solo in Ifrit EX there is nobody to heal and a heal GCD/oGCD is pure DPS loss. SCOPED TO
    // TERRITORY 295: leaving these unconditionally false meant that anyone running this rotation
    // outside the farm duty -- after a failed restore, or by picking it in RSR's own dropdown --
    // had healing silently and permanently disabled with no warning.
    public override bool CanHealSingleSpell => !IfritExBurst.InIfritEx && base.CanHealSingleSpell;

    public override bool CanHealAreaSpell => !IfritExBurst.InIfritEx && base.CanHealAreaSpell;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
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
            // skipTTKCheck everywhere below: nails have a tiny apparent TTK and Ifrit spends long
            // stretches near a phase threshold, so RSR's TTK gate would eat the whole kit.

            // Chain Stratagem: +10% crit on the target for 20 s. It is a DEBUG-ON-TARGET, so it is
            // wasted on a nail we are about to delete — hold it for Ifrit while nails are alive.
            // (RSR's own ModifyChainStratagemPvE sets TimeToKill = 10, hence skipTTKCheck.)
            if (!(HoldChainStratagemDuringNails && NailsUp)
                && ChainStratagemPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Baneful Impaction: 700 potency over 15 s, consumes Impact Imminent (RSR already gates
            // it on that status via StatusNeed). Fire it immediately after every Chain Stratagem so
            // the whole DoT sits inside the crit window; never let Impact Imminent expire.
            // targetOverride sends it into the nail cluster during a nail set, which is legal —
            // it deals no damage to Ifrit.
            if (BanefulImpactionPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
            {
                return true;
            }

            // Aetherflow before Dissipation — see the header note on the deliberate deviation from
            // The Balance's opener. RSR gates both on (!HasAetherflow && InCombat), and Dissipation
            // additionally on an active faerie, so no extra conditions are needed here.
            if (AetherflowPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            if (DissipationPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Every Aetherflow stack becomes an Energy Drain. Solo there is nothing to heal, so a
            // stack is never reserved for Lustrate/Indomitability/Excogitation/Sacred Soil.
            if (EnergyDrainPvE.CanUse(out act, skipTTKCheck: true, targetOverride: KillOrder))
            {
                return true;
            }

            // Movement tools. These only matter during the nail phase (running the arena ring);
            // Ifrit himself is stationary with no forced movement in the first ~15 s.
            // Swiftcast converts a would-be Ruin II back into a full Broil IV — the GCD block below
            // checks !HasSwift before falling back to Ruin II, so the two cooperate.
            if (SwiftcastWhileMoving && IsMoving && !HasSwift
                && SwiftcastPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Expedient: taken purely for the Expedience movement-speed buff; its 10% mitigation is
            // a free bonus. Costs an oGCD slot and no GCD.
            if (ExpedientWhileMoving && IsMoving
                && ExpedientPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }
        }

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

        // Summon Eos. RSR's ModifySummonEosPvE already gates this on (!HasPet && !FairyDismissed),
        // so it fires pre-pull and again after Dissipation's 30 s dismissal expires — which is what
        // we want for a second Dissipation in a long run. It is a full GCD, so it is suppressed
        // during the opener window to avoid trading a Broil IV for it.
        if (SummonFairy
            && !(IfritExBurst.InIfritEx && InCombat && IfritExBurst.InOpenerWindowOf(OpenerWindow))
            && SummonEosPvE.CanUse(out act))
        {
            return true;
        }

        // Biolysis. 850 potency over 30 s and it snapshots Chain Stratagem, but it needs ~11-12 s of
        // ticking to beat a single Broil IV — so never spend it on a nail. RSR's TargetStatusProvide
        // handles the refresh window for us.
        if (!NailsUp && BiolysisPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Level-sync fallbacks for the same slot (territory 295 syncs to 50 if entered normally:
        // Biolysis is 72, Bio II 26, Bio 2). CanUse already handles the level check.
        if (!NailsUp && BioIiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (!NailsUp && BioPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Art of War II is 180 potency in a 5y circle centred on the PLAYER vs Broil IV's 320
        // single-target, so it only breaks even at 2 targets — and the nails ring the arena rather
        // than clustering. Off by default; no targetOverride because it is self-centred.
        if (UseArtOfWar && NumberOfHostilesInRangeOf(5) >= 2)
        {
            if (ArtOfWarIiPvE.CanUse(out act, skipAoeCheck: true))
            {
                return true;
            }

            if (ArtOfWarPvE.CanUse(out act, skipAoeCheck: true))
            {
                return true;
            }
        }

        // Ruin II (220, instant) ONLY while actually moving, and only when Swiftcast is not up —
        // if we have Swiftcast we would rather spend it on a full-potency Broil IV below.
        // Standing still, Ruin II is a flat 100-potency loss, so it is never pressed there.
        if (IsMoving && !HasSwift && RuinIiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Broil IV is >90% of all GCDs. The Broil III/II/Broil/Ruin ladder below is the
        // level-sync fallback chain; CanUse rejects the ones the player has not unlocked.
        if (BroilIvPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (BroilIiiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (BroilIiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (BroilPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Last-resort instant filler: if a hardcast was rejected for any reason (movement with
        // Poslock off, knockback window, etc.) Ruin II still keeps damage flowing.
        if (RuinIiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (RuinPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
