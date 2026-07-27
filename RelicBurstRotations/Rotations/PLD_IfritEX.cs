namespace RelicBurstRotations.Rotations;

/// <summary>
/// PALADIN — Bowl of Embers (Extreme), TerritoryType 295.
///
/// SCENARIO (fixed, do not generalise): solo, unsynced, level 100, wielding a low-item-level ARR
/// Zodiac relic weapon (~0.50x the damage of current gear) while Epic Echo grants +300% damage and
/// max HP. Net effect: the kill is expected to take 10-20 s / 4-8 GCDs. The duty is being farmed
/// 42-63 times for Nexus light, so the only metric is wall-clock time from pull to boss death.
///
/// CONSEQUENCE FOR THE ROTATION: Fight or Flight (60 s CD, 20 s duration = exactly 8 GCDs at a
/// 2.50 s GCD) and Imperator (60 s CD) each fire ONCE and cover the whole fight. There is no second
/// burst window to protect and no raid buff to align to, so there is deliberately NO cooldown
/// alignment / drift / hold logic anywhere in this file. Everything is spent at the pull.
///
/// BURST PLAN, in order (potencies are 7.5 Job Guide values; x1.25 inside Fight or Flight):
///   pre-pull  Iron Will (left to RSR's own TankStance stage), Sprint (RSR's SpeedAbility stage)
///   pre-pull  tincture, optional, config-gated — see PullTincture
///   pre-pull  Fight or Flight            (so that Imperator itself is buffed)
///   oGCD      Imperator            580   (25 y — pulls from the arena edge, zero travel loss;
///                                         grants Requiescat x4 + Confiteor Ready)
///   GCD 1     Confiteor           1000
///   weave     Intervene            150   (doubles as the 20 y gap-closer into melee)
///   GCD 2     Blade of Faith       760
///   weave     Circle of Scorn      140 + DoT
///   weave     Expiacion            450
///   GCD 3     Blade of Truth       880
///   weave     Intervene            150   (second charge)
///   GCD 4     Blade of Valor      1000   (grants Blade of Honor Ready)
///   weave     Blade of Honor      1000   <-- Valor + Honor back to back is ~2500 buffed potency
///                                            inside one second. This pair IS Paladin's answer to
///                                            the documented nail-skip requirement ("finishing
///                                            attack takes out 20-30% of Ifrit's HP in one hit").
///   GCD 5     Goring Blade         700
///   GCD 6+    Fast Blade > Riot Blade > Royal Authority, then Atonement > Supplication >
///             Sepulchre and a Divine Might Holy Spirit. Imperator does NOT grant Atonement Ready
///             in PvE (that is the PvP version), so the physical combo is the only route back to
///             the Atonement package.
///
/// DELIBERATE DEVIATIONS FROM THE BALANCE'S STANDARD PLD OPENER:
///   * No pre-pull hardcast Holy Spirit — there is no countdown when you solo-pull a duty.
///   * The magic burst is FRONTLOADED. The standard line spends GCDs 1-3 on Fast Blade / Riot
///     Blade / Royal Authority (220/330/460, all unbuffed) before entering it; in a 10-20 s kill
///     that is 40-75% of the fight at a quarter of the potency.
///   * The Requiescat-buffed Holy Spirit (700) is dropped entirely — all four Requiescat stacks
///     belong to Confiteor + the three Blades, which are 760-1000 each.
///   * All mitigation is left to the base class. Rampart / Sheltron / Bulwark / Divine Veil /
///     Passage of Arms / Cover / Intervention would only steal weave slots from Blade of Honor,
///     Circle of Scorn, Expiacion and Intervene. Hallowed Ground and Guardian stay as untouched
///     insurance via base.
///   * Clemency is hard-disabled through CanHealSingleSpell — PLD_Reborn's own override counts
///     alive party healers, and solo that count is zero, which would turn Clemency ON.
///
/// NAIL PHASE — THE SAFETY-CRITICAL PART:
///   Since patch 4.56, dealing damage to Ifrit while Infernal Nails are alive makes him temporarily
///   INVULNERABLE and can lock the fight into an unwinnable Hellfire loop. The third nail set
///   contains a LARGE CENTRAL NAIL standing next to Ifrit, and every Paladin AoE/falloff action
///   (Imperator, Confiteor, Blade of Faith/Truth/Valor, Blade of Honor, Expiacion, Circle of Scorn,
///   Total Eclipse, Prominence, Holy Circle) splashes 5 y. Hitting that central nail with any of
///   them would splash Ifrit. Therefore, while IfritExBurst.ShouldKillNails is true this rotation
///   uses ONLY pure single-target weaponskills — Goring Blade, the Atonement chain, the Fast Blade
///   combo, Divine Might Holy Spirit, and Shield Lob (100 potency, 20 y instant) to start damage on
///   the next nail while running to it. Fight or Flight and Imperator are BANKED during nails so a
///   fresh 20 s window is available the instant the last nail dies; the nails die to roughly one
///   GCD each anyway, so the buff would be pure overkill.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK:
///   1. Blade of Honor is placed in AttackAbility, which RSR skips while HasHostilesInRange is
///      false (3 y for a tank). This is safe only because Intervene has already closed the gap by
///      GCD 2 and Blade of Honor cannot be ready before GCD 4. If a run ever ends up out of melee
///      with Blade of Honor Ready up, it will simply be delayed, not lost.
///   2. Divine Might Holy Spirit is gated on the StatusID rather than on PaladinRotation's
///      HasDivineMight property, purely because the XML does not document that property's type.
///      Both are equivalent; swap if preferred.
///   3. Level gating is intentionally absent per TEMPLATE.md rule 8 — the player is level 100 and
///      unsynced. TerritoryType 295 has ClassJobLevelSync = 50, so entering SYNCED would remove
///      Imperator (96), Blade of Honor (100), Blade of Faith/Truth/Valor (90), Expiacion (86),
///      Confiteor (80) and the Atonement chain (76) and collapse this plan. CanUse would simply
///      decline them and the rotation degrades to the Fast Blade combo, but the design is not
///      tuned for that.
///   4. Ifrit (Extreme) and Infernal Nail HP are unknown, so nothing here is conditioned on an
///      absolute HP number.
///   5. Iron Will and Sprint are deliberately NOT pressed here — RSR's own TankStance and
///      SpeedAbility stages own them, and they run before AttackAbility in the ability chain.
/// </summary>
[Rotation("Ifrit EX Burst (PLD)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/PLD_IfritEX.cs")]
[ExtraRotation]
public sealed class PLD_IfritEX : PaladinRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

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
        ImGui.Text($"NailPhase (splash banned): {NailPhase}");
        ImGui.Text($"HostilesInMaxRange: {NumberOfAllHostilesInMaxRange}");
        ImGui.Text($"RequiescatStacks: {RequiescatStacks}");
        ImGui.Text($"HasFightOrFlight: {HasFightOrFlight}");
    }

    #endregion

    #region Extra Methods

    /// <summary>
    /// True while an Infernal Nail set is up and we must not splash Ifrit. Also the gate that banks
    /// Fight or Flight / Imperator for the moment the last nail dies.
    /// </summary>
    private bool NailPhase => NailPriority && IfritExBurst.ShouldKillNails(HostileTarget);

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss. On Paladin specifically this
    // is what stops Clemency (a 2.5 s GCD) — PLD_Reborn's own CanHealSingleSpell override counts
    // alive party healers, which solo is zero, and would therefore enable it.
    public override bool CanHealSingleSpell => false;

    public override bool CanHealAreaSpell => false;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
        // NOTE: RSR's own tincture setting defaults to "high-end duty only" and territory 295 is
        // NOT high-end, so UseBurstMedicine will often silently no-op. That is expected.
        if (PullTincture && IfritExBurst.InIfritOpener(OpenerWindow) && UseBurstMedicine(out act))
        {
            return true;
        }

        if (IfritExBurst.InIfritEx && !NailPhase)
        {
            // Fight or Flight and Imperator live in EmergencyAbility rather than AttackAbility on
            // purpose: AttackAbility is skipped entirely while HasHostilesInRange is false (3 y for
            // a tank), and both of these want to fire from the arena edge before we have closed.
            // Fight or Flight first so that Imperator itself lands inside the +25% window.
            if (HasHostilesInMaxRange && FightOrFlightPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // THE PULL. 580 potency at 25 y; grants Requiescat x4 + Confiteor Ready, which is the
            // entire reason the physical opener can be skipped. "Requiescat" is only the pre-96
            // name of this button (and the status name); at 100 the action is Imperator.
            if (ImperatorPvE.CanUse(out act, skipAoeCheck: true, usedUp: true, skipTTKCheck: true))
            {
                return true;
            }
        }

        // Intervene as the gap-closer. Kept out of AttackAbility for the same range reason: this is
        // exactly the case where we are NOT yet in melee. It is a pure single-target dash with no
        // splash, so it is also the safe way to travel between nails (the brief notes travel time,
        // not damage, is the bottleneck on the nail path).
        if (IfritExBurst.InIfritEx && !HasHostilesInRange && !IsMoving
            && IntervenePvE.CanUse(out act, usedUp: true, skipTTKCheck: true, targetOverride: KillOrder))
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
            if (NailPhase)
            {
                // Splash ban: Blade of Honor, Circle of Scorn and Expiacion all hit 5 y and would
                // clip Ifrit off the large central nail of the third set. Intervene is the only
                // safe oGCD here — pure single target, and it is the travel tool between nails.
                if (IntervenePvE.CanUse(out act, usedUp: true, skipTTKCheck: true, targetOverride: KillOrder))
                {
                    return true;
                }

                return base.AttackAbility(nextGCD, out act);
            }

            // skipTTKCheck on every burst oGCD: the kill is short, and RSR's time-to-kill gate
            // would otherwise reject exactly the cooldowns this plan depends on.

            // Biggest single oGCD PLD has (1000 potency, 1 s recast). It is weaved immediately
            // after Blade of Valor on purpose — never delay it to a later slot.
            if (BladeOfHonorPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            if (ExpiacionPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            if (CircleOfScornPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // Both charges are free potency inside the window; never bank one for movement, there
            // is nothing to move for (Ifrit is stationary, no untargetable windows).
            if (!IsMoving && IntervenePvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
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

        if (IfritExBurst.InIfritEx && NailPhase)
        {
            // ---- NAIL PATH: pure single-target weaponskills only, no splash, no burst. ----
            if (GoringBladePvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (SepulchrePvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (SupplicationPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // Divine Might only — an unbuffed Holy Spirit is a hardcast and would cost travel time.
            if (StatusHelper.PlayerHasStatus(true, StatusID.DivineMight)
                && HolySpiritPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (AtonementPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (RoyalAuthorityPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (RiotBladePvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (FastBladePvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // 100 potency, 20 y instant — starts damage on the next nail while still running to it.
            if (ShieldLobPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            return base.GeneralGCD(out act);
        }

        // ---- BURST PATH ----
        // The Confiteor chain is combo-locked (Confiteor > Faith > Truth > Valor), so listing order
        // between these four is cosmetic; CanUse enforces the sequence. usedUp/skipAoeCheck are set
        // because these are falloff "AoE" spells that RSR would otherwise gate on a target count.
        if (ConfiteorPvE.CanUse(out act, usedUp: true, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        if (BladeOfFaithPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        if (BladeOfTruthPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        if (BladeOfValorPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 700 potency, gated by Goring Blade Ready from Fight or Flight. Placed after the Confiteor
        // chain because 700 < 760/880/1000, but it must still land inside the 20 s window.
        if (GoringBladePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Filler, highest potency first: Sepulchre 540 > Supplication 500 = Divine Might Holy
        // Spirit 500 > Atonement 460 = Royal Authority 460 > Riot Blade 330 > Fast Blade 220.
        if (SepulchrePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (SupplicationPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Deviation from The Balance: the Requiescat-buffed Holy Spirit (700) is intentionally NOT
        // used — all four stacks belong to Confiteor and the three Blades (760-1000 each). Only the
        // Divine Might proc is spent here, and only because it is instant.
        if (StatusHelper.PlayerHasStatus(true, StatusID.DivineMight)
            && HolySpiritPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (AtonementPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (RoyalAuthorityPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (RiotBladePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (FastBladePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Ranged filler so a forced reposition never costs a whole GCD.
        if (ShieldLobPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
