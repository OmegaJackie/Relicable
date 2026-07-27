namespace RelicBurstRotations.Rotations;

/// <summary>
/// WHITE MAGE — solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135),
/// farming the Bowl of Embers (Extreme) (TerritoryType 295) for Nexus light.
///
/// WHY THIS ONE IS DIFFERENT FROM THE OTHER NINE
/// --------------------------------------------
/// Every other rotation in this assembly assumes a 10-20 s kill and exactly ONE burst window.
/// WHM cannot frontload hard enough to skip the Infernal Nail phases: its whole opener is roughly
/// 4.9k potency, nowhere near a 100%-to-0 push. So WHM plays the FULL fight —
/// burst -> 50% -> 4 nails -> burst -> 30% -> 7 nails -> burst -> ~20/10% -> 13 nails -> phase 4 —
/// which is 90-150 s. Consequences baked into this file:
///   * Presence of Mind (120 s) fires TWICE, so it is a plain "on cooldown" call, not an opener-only
///     branch. Assize (40 s) fires 3-4 times. Both are gated on <see cref="IfritExBurst.InBurst"/>
///     (in territory 295 AND in combat), not on an opener window.
///   * MP is a real constraint over 40-55 GCDs. Thin Air and Lucid Dreaming are load-bearing here,
///     unlike on the sub-20 s burst jobs where they are dead weight.
///   * The nail phase is part of the plan, not a failure path.
///
/// BURST PLAN, IN ORDER
/// --------------------
///   PRE-PULL (out of combat, inside 295): 3x Afflatus Rapture to burn the 3 lilies that accrue
///     during the loading screen and bloom the Blood Lily, so Afflatus Misery (1400 potency, the
///     single biggest hit WHM owns) is live on GCD 2 at zero in-combat GCD cost.
///   GCD 1  Dia          — front-loaded, NOT held to the end of a buff window. Presence of Mind is a
///                         haste, not a damage buff, so there is nothing for the DoT to snapshot
///                         solo. 85 on application + 85/3 s for 30 s = 935 potency for one GCD.
///   oGCD   Presence of Mind — weaved immediately after Dia. (The plan's ideal was a pre-pull PoM;
///                         RSR sets ActionCheck = () => InCombat on it, so that is impossible —
///                         this is the plan's own documented fallback, costing under one GCD.)
///   GCD 2  Afflatus Misery (1400) — spent immediately, never held across a phase boundary.
///   oGCD   Assize (400) — fired on cooldown from the first free weave slot onward, never delayed
///                         for alignment (there are no raid buffs to align to).
///   GCD 3-5 Glare IV x3 (640 each) — all three Sacred Sight stacks back to back. Ifrit is
///                         stationary with no mechanics in the first 15 s, so there is nothing to
///                         save the instants for.
///   then   Glare III filler (350), Dia refresh, Misery whenever the Blood Lily re-blooms,
///          PoM/Assize on cooldown, Thin Air + Lucid Dreaming on cooldown for MP.
///
/// NAIL PHASE
/// ----------
/// While any Infernal Nail is alive, damaging Ifrit is STRICTLY HARMFUL — since patch 4.56 he goes
/// temporarily invulnerable and the fight can lock into an unwinnable Hellfire loop. All damage
/// GCDs therefore carry <c>targetOverride: KillOrder</c>, which resolves to
/// <see cref="TargetType.LowMaxHP"/> (nails have far less max HP than Ifrit) while nails are up and
/// to <c>default</c> otherwise. Dia is additionally suppressed entirely during nails and near a
/// threshold, because a ticking DoT is the one thing that keeps damaging Ifrit after the rotation
/// stops pressing buttons.
///
/// Holy III is deliberately absent: 150 potency in an 8 y point-blank circle does not break even
/// against a 350-potency Glare III until 3 targets are inside 8 y, and the nails are spread around
/// the arena perimeter. Assize is the only real AoE win and it is already on cooldown.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK
/// -----------------------------------
///  1. Level 100 UNSYNCED. TerritoryType 295 has ClassJobLevelSync 50; entering synced deletes
///     Glare III/IV, Dia and Afflatus Misery and this plan collapses. No level gates are written
///     (per TEMPLATE.md rule 8) — <c>CanUse</c> already checks level, so a synced entry degrades to
///     whatever RSR still allows rather than misbehaving.
///  2. The pre-pull Blood Lily bloom relies on RSR actually invoking GeneralGCD while out of combat.
///     UNVERIFIED in-game. If it never fires, the only cost is that Afflatus Misery arrives a few
///     GCDs later; nothing breaks.
///  3. "Re-bloom while travelling between nails" uses <c>IsMoving</c> as the travel proxy. UNVERIFIED
///     that this lines up with real nail traversal; it is deliberately conservative (it only ever
///     spends GCDs that would otherwise be dead) and can be switched off in the config.
///  4. The 90-150 s estimate is extrapolated, not measured (Ifrit EX / Infernal Nail HP could not be
///     sourced). If real runs come in much shorter the second Presence of Mind simply never happens;
///     the priority order is unaffected.
/// </summary>
[Rotation("Ifrit EX Burst (WHM)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/WHM_IfritEX.cs")]
[ExtraRotation]
public sealed class WHM_IfritEX : WhiteMageRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [Range(0, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
    public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;

    [RotationConfig(CombatType.PvE,
        Name = "Bloom the Blood Lily before the pull (spends banked lilies out of combat)")]
    public bool PreBloomBloodLily { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Re-bloom the Blood Lily while travelling between Infernal Nails")]
    public bool RebloomDuringNails { get; set; } = true;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE,
        Name = "Stop refreshing Dia on Ifrit below this HP (avoids ticking him past a nail threshold)")]
    public float DiaHpThreshold { get; set; } = 0.55f;

    [RotationConfig(CombatType.PvE, Name = "Use Thin Air to save MP (this is a long fight for WHM)")]
    public bool UseThinAir { get; set; } = true;

    [Range(0, 10000, ConfigUnitType.None, 100)]
    [RotationConfig(CombatType.PvE, Name = "Use Thin Air below this MP", Parent = nameof(UseThinAir))]
    public float ThinAirMp { get; set; } = 7000;

    [RotationConfig(CombatType.PvE, Name = "Use Lucid Dreaming (RSR's own MP threshold still applies)")]
    public bool UseLucid { get; set; } = true;

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
        ImGui.Text($"Lily: {Lily}  BloodLily: {BloodLily}  LilyTime: {LilyTime:F1}");
        ImGui.Text($"SacredSightStacks: {SacredSightStacks}");
        ImGui.Text($"HasPresenceOfMind: {HasPresenceOfMind}  HasThinAir: {HasThinAir}");
        ImGui.Text($"CurrentMp: {CurrentMp}  IsMoving: {IsMoving}");
        ImGui.Text($"DiaAllowed: {DiaAllowed}");
    }

    #endregion

    #region Extra Methods

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// Whether Dia may be applied/refreshed right now.
    ///
    /// Outside Bowl of Embers (Extreme) this is always true (GeneralGCD has already declined by
    /// then; the value is only surfaced in the debug readout). Inside 295 it is false while nails
    /// are up (a ticking DoT would
    /// keep damaging Ifrit and can trigger the 4.56 invulnerability / Hellfire lock) and false once
    /// Ifrit is close to a nail threshold, because the DoT would carry him across it unattended.
    /// </summary>
    private bool DiaAllowed
    {
        get
        {
            if (!IfritExBurst.InIfritEx)
            {
                return true;
            }

            if (IfritExBurst.ShouldKillNails(HostileTarget))
            {
                return false;
            }

            IBattleChara? target = HostileTarget;
            if (target is null)
            {
                return true;
            }

            // Nails have tiny HP and die in 1-2 GCDs; 85 up-front potency on them is a loss anyway,
            // and the ratio gate below is written for Ifrit.
            if (IfritExBurst.IsInfernalNail(target))
            {
                return false;
            }

            return target.GetHealthRatio() > DiaHpThreshold;
        }
    }

    /// <summary>
    /// Spend a lily on an instant, self-centred Afflatus GCD purely to advance the Blood Lily.
    /// Rapture first (no target needed at all); Afflatus Solace is the fallback for users who have
    /// RSR's AoE handling switched off, since it is single-target and feeds the Blood Lily equally.
    /// Both already carry <c>ActionCheck = () =&gt; Lily &gt; 0 &amp;&amp; BloodLily &lt; 3</c>, so
    /// <c>CanUse</c> does the gauge gating for us.
    /// </summary>
    private bool FeedBloodLily(out IAction? act)
    {
        if (AfflatusRapturePvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        if (AfflatusSolacePvE.CanUse(out act))
        {
            return true;
        }

        act = null;
        return false;
    }

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss. MANDATORY on WHM — RSR's own
    // WHM logic turns healing ON when no other healer is alive, which solo is always.
    // Solo in Ifrit EX there is nobody to heal and a heal GCD/oGCD is pure DPS loss. SCOPED TO
    // TERRITORY 295: leaving these unconditionally false meant that anyone running this rotation
    // outside the farm duty -- after a failed restore, or by picking it in RSR's own dropdown --
    // had healing silently and permanently disabled with no warning.
    public override bool CanHealSingleSpell => !IfritExBurst.InIfritEx && base.CanHealSingleSpell;

    public override bool CanHealAreaSpell => !IfritExBurst.InIfritEx && base.CanHealAreaSpell;

    // Also off: at level 100 with the Epic Echo (+300% max HP) nothing in this fight threatens the
    // player, so Tetragrammaton / Benediction / Divine Benison weaves are stolen burst slots.
    public override bool CanHealSingleAbility => !IfritExBurst.InIfritEx && base.CanHealSingleAbility;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
        // MedicineType is Mind on WhiteMageRotation, so UseBurstMedicine picks the Gemdraught of Mind.
        // NOTE: RSR's TinctureUseType defaults to "high-end duty only" and territory 295 is NOT
        // high-end, so this will usually silently no-op. That is expected, per TEMPLATE.md rule 7.
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
            // Presence of Mind: 120 s. Unlike every other rotation here this genuinely fires twice
            // over a 90-150 s WHM kill, so there is no opener gate — press it the instant it is up.
            // skipTTKCheck because RSR ships TimeToKill = 10 on it, which a short relic kill trips.
            // No targetOverride: it is a self-buff (IsFriendly), so overriding its targeting is wrong.
            if (PresenceOfMindPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Assize: 400 potency, 20 y self-centred, 40 s. Never held. skipAoeCheck because it is an
            // AoE that will normally see only one target on Ifrit and would otherwise be refused.
            // It is also the only AoE that meaningfully pays off during a nail set — stand on the
            // ring between nails rather than next to Ifrit when it comes up.
            if (AssizePvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // MP sustain. Irrelevant on a 15 s burst job, mandatory across 40-55 WHM GCDs.
            if (UseThinAir && CurrentMp <= ThinAirMp
                && ThinAirPvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
            {
                return true;
            }

            // LucidDreamingPvE already carries RSR's own "CurrentMp < LucidDreamingMpThreshold &&
            // InCombat" ActionCheck, so no extra threshold is needed here.
            if (UseLucid && LucidDreamingPvE.CanUse(out act, skipTTKCheck: true))
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

        // --- Outside Bowl of Embers (Extreme): decline and hand the decision straight back ---------
        // Same shape as BLM_IfritEX. It matters more here than anywhere else: AttackAbility is
        // already gated on IfritExBurst.InBurst, so running the damage loop outside 295 would give a
        // WHM that presses Glare and Dia but never Presence of Mind, Assize, Thin Air or Lucid
        // Dreaming, and never heals either (CanHealSingleSpell / CanHealAreaSpell /
        // CanHealSingleAbility are all false) — strictly worse than declining.
        // NOTE: CustomRotation.GeneralGCD is a stub (act = null; return false) and WhiteMageRotation
        // does not override it, so outside 295 this rotation is deliberately inert. It is a
        // single-duty farm rotation, not a general WHM — pick a normal one before leaving the farm.
        // ...but it must not be INERT either: CustomRotation.GeneralGCD is a hard stub and
        // WhiteMageRotation does not override it, so returning base here would leave the player
        // auto-attacking with no error and no obvious cause. Fall back to a plain single-target
        // filler instead. (Healing is re-enabled outside 295 by the CanHeal* properties above.)
        if (!IfritExBurst.InIfritEx)
        {
            return FallbackGCD(out act);
        }

        // --- PRE-PULL: bloom the Blood Lily before combat starts (opener steps 1-3) ---------------
        // Three lilies accrue during the loading screen and run-in; converting them now makes
        // Afflatus Misery (1400) available on GCD 2 for zero in-combat GCD cost. Out of combat there
        // is nothing else worth pressing, so this branch owns the whole pre-pull and then declines.
        if (!InCombat)
        {
            if (PreBloomBloodLily && FeedBloodLily(out act))
            {
                return true;
            }

            return base.GeneralGCD(out act);
        }

        TargetType order = KillOrder;

        // --- Dia: 935 potency over its full duration for one GCD, so it outranks everything --------
        // DiaAllowed suppresses it while nails are up and near a threshold; CanUse's own
        // TargetStatusProvide handles "already applied", so this naturally fires on GCD 1 and then
        // only on refresh.
        if (DiaAllowed && DiaPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // --- Afflatus Misery: 1400 potency, the biggest single hit WHM owns ------------------------
        // CanUse gates on BloodLily == 3 for us. Never held across a phase boundary: whatever is
        // legal to damage right now (Ifrit, or a nail) is what it goes on.
        if (AfflatusMiseryPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // --- Glare IV: 640 vs Glare III's 350, gated by CanUse on SacredSightStacks > 0 ------------
        // Spend all three back to back rather than saving them for movement; Ifrit is stationary.
        if (GlareIvPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
        {
            return true;
        }

        // --- Re-bloom while travelling between nails -----------------------------------------------
        // The single best WHM-specific optimisation in this fight: travel GCDs are otherwise dead,
        // and each set of three converts into another 1400-potency Afflatus Misery. Only while a
        // nail set is up AND we are actually moving, so it can never displace Ifrit uptime.
        if (RebloomDuringNails && IfritExBurst.ShouldKillNails(HostileTarget) && IsMoving
            && FeedBloodLily(out act))
        {
            return true;
        }

        // --- Filler ---------------------------------------------------------------------------------
        // Glare III, 350 potency. Its 1.5 s cast fits inside the 2.5 s GCD so it never clips.
        // Holy III is deliberately NOT here: 150 potency needs 3 targets inside 8 y to beat this,
        // and the nails are spread around the arena ring.
        if (GlareIiiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    /// <summary>
    /// Minimal single-target filler used everywhere OUTSIDE Bowl of Embers (Extreme). Exists only
    /// so this rotation is never completely inert when it is active outside its intended duty.
    /// </summary>
    private bool FallbackGCD(out IAction? act)
    {
        if (AfflatusMiseryPvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        if (DiaPvE.CanUse(out act))
        {
            return true;
        }

        if (GlareIiiPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
