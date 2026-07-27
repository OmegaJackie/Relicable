namespace RelicBurstRotations.Rotations;

/// <summary>
/// WARRIOR — solo/unsynced Bowl of Embers (Extreme) (TerritoryType 295) relic-light farm.
///
/// SCENARIO
/// Level 100, unsynced, solo, wielding a low-iLvl ARR Zodiac relic (iLvl 80-135). Epic Echo grants
/// +300% damage and max HP, the relic costs roughly half a geared character's damage, and the net
/// result is a 10-25 second kill — i.e. ONE burst window. Every 60 s and 120 s cooldown fires
/// exactly once, so there is no alignment, no drift handling and no holding. The rotation IS the
/// opener.
///
/// BURST PLAN (the order this file encodes, by priority rather than by literal script)
///   oGCD/pull : Defiance (tank stance; no damage penalty in modern FFXIV, keeps Ifrit parked)
///               tincture (optional, see note below)
///               Inner Release  — 3 free auto-CDH Fell Cleaves + Primal Rend Ready + Inner Strength
///                                (Inner Strength also nullifies Vulcan Burst's knockback for 15 s)
///   GCD 1     : Primal Rend        720p guaranteed CDH, 20 y jump = free gap closer from the edge
///   weave     : Upheaval           420p, biggest oGCD available at that instant; Primal Rend's
///                                  animation lock only fits ONE weave, so it gets the biggest one
///   GCD 2     : Primal Ruination   800p, the highest raw potency in the kit
///   GCD 3-5   : Fell Cleave x3     Inner Release stacks, each a guaranteed CDH (~950 effective)
///   weaves    : Onslaught x3       150p each, 3 charges that will never come back — pure free damage
///   weave     : Primal Wrath       700p, unlocked exactly when the 3rd Fell Cleave grants the 3rd
///                                  Burgeoning Fury stack. This is why no Infuriate may come earlier.
///   weave     : Infuriate          50 gauge + Nascent Chaos
///   GCD 6     : Inner Chaos        700p, ALWAYS a guaranteed CDH (~1150 effective)
///   weave     : Infuriate (2nd charge)
///   GCD 7     : Inner Chaos        the finisher — the biggest single hit WAR can place last, which
///                                  is what the documented "skip the nail phase" heuristic wants
///                                  (finishing blow worth 20-30% of Ifrit's HP).
///   after     : if Ifrit lived, the fight is long — establish Surging Tempest and run the combo.
///
/// HARD ORDERING CONSTRAINT
/// Nascent Chaos replaces Fell Cleave with Inner Chaos on the bar. Pressing Infuriate before all
/// three Inner Release Fell Cleaves have landed burns a free Fell Cleave AND costs the third
/// Burgeoning Fury stack, i.e. costs Primal Wrath (700p) outright. Hence Infuriate is gated on
/// <c>InnerReleaseStacks == 0</c> AND "Inner Release has already been pressed", which also blocks a
/// pre-pull Infuriate.
///
/// NAIL PHASE
/// While Infernal Nails are alive, damaging Ifrit is strictly harmful (post-4.56 he goes temporarily
/// invulnerable and the fight can lock into an unwinnable Hellfire loop). Every damaging CanUse in
/// GeneralGCD — plus Upheaval and Onslaught — therefore carries
/// <c>targetOverride: KillOrder</c>, which resolves to <see cref="TargetType.LowMaxHP"/> (nails have
/// far less max HP than Ifrit) while nails are up and to <c>default</c> otherwise. Nails are spread
/// around the arena ring, so this stays strictly single-target: no Overpower / Mythril Tempest /
/// Decimate / Chaotic Cyclone / Orogeny appear anywhere in this file. Orogeny in particular shares
/// Upheaval's 30 s recast and is a straight downgrade below 3 targets.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK
///  1. Inner Release is deliberately gated on <c>IfritExBurst.InBurst</c> (in combat), NOT pressed
///     pre-pull as the plan's step 3 describes. Rationale: RSR's ability pipeline only runs
///     meaningfully once there is something to do, and an out-of-combat press risks the 15 s Inner
///     Release window expiring while the character idles at the entrance. Cost is one GCD: the
///     opener becomes Tomahawk/Heavy Swing -> weave Inner Release -> Primal Rend, which is the
///     plan's own documented fallback opener. If in-game testing shows RSR happily presses it out
///     of combat, relax the gate to <c>IfritExBurst.InIfritEx</c>.
///  2. Onslaught is duplicated into EmergencyAbility for the out-of-melee-range case only.
///     AttackAbility is skipped entirely when <c>HasHostilesInRange</c> is false (3 y for a tank),
///     which is exactly the run-in and the nail-to-nail travel, i.e. precisely when a 20 y rush is
///     most valuable. In range it is handled by AttackAbility as a normal weave.
///  3. Primal Wrath is the ONLY damage action here with no <c>targetOverride</c>. It is a 5 y AoE
///     centred on the player, so its declared targeting is not a hostile pick and overriding it
///     could break the action. While killing nails the player is standing at a nail and far from
///     Ifrit, so this cannot accidentally chip Ifrit during the nail phase.
///  4. Tincture: RSR's <c>UseBurstMedicine</c> hard-refuses when its TinctureUseType setting is the
///     default "high-end duty only" — territory 295 is a 2.1 Extreme and is not flagged high-end,
///     so the tincture will usually silently no-op. That is expected; the config defaults to off.
///  5. Mitigation is deliberately absent from the burst path (every press steals a weave slot from
///     Onslaught / Upheaval / Primal Wrath / Infuriate). Bloodwhetting, Thrill of Battle,
///     Equilibrium, Damnation, Shake It Off, Rampart, Arm's Length and Holmgang are all left to
///     RSR's inherited defence/heal stages, which this file does not override.
/// </summary>
[Rotation("Ifrit EX Burst (WAR)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/WAR_IfritEX.cs")]
[ExtraRotation]
public sealed class WAR_IfritEX : WarriorRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Keep Defiance up in Bowl of Embers (Extreme)")]
    public bool KeepDefiance { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Establish Surging Tempest if the kill runs long")]
    public bool UseSurgingTempest { get; set; } = true;

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
        ImGui.Text($"BeastGauge: {BeastGauge}");
        ImGui.Text($"InnerReleaseStacks: {InnerReleaseStacks}");
        ImGui.Text($"NascentChaos: {HasNascentChaos}");
        ImGui.Text($"Wrathful: {IsWrathful}");
        ImGui.Text($"InfuriateAllowed: {InfuriateAllowed}");
    }

    #endregion

    #region Extra Methods

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// Nascent Chaos (from Infuriate) swaps Fell Cleave for Inner Chaos on the bar. While it is up
    /// no Fell Cleave branch may run, and no further Infuriate may be pressed (it would be wasted).
    /// </summary>
    private static bool HasNascentChaos =>
        StatusHelper.PlayerHasStatus(true, StatusID.NascentChaos);

    /// <summary>Third Burgeoning Fury stack landed — Inner Release has become Primal Wrath.</summary>
    private static bool IsWrathful =>
        StatusHelper.PlayerHasStatus(false, StatusID.Wrathful);

    /// <summary>
    /// THE ordering rule for this job. Infuriate is legal only once every Inner Release stack has
    /// been spent (so no free Fell Cleave and no Burgeoning Fury stack is lost), only once Inner
    /// Release has actually been pressed at least once (blocks the tempting pre-pull Infuriate),
    /// only when Nascent Chaos is not already up (it would be overwritten), and only when the 50
    /// gauge it grants will not overcap.
    /// </summary>
    private bool InfuriateAllowed =>
        InnerReleaseStacks == 0
        && InnerReleasePvE.Cooldown.IsCoolingDown
        && !HasNascentChaos
        && BeastGauge <= 50;

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss.
    public override bool CanHealSingleSpell => false;

    public override bool CanHealAreaSpell => false;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (IfritExBurst.InIfritEx)
        {
            // Tank stance. No damage penalty in modern FFXIV; it only raises enmity, max HP and
            // healing received, and it guarantees Ifrit never wanders. Self-limiting: once Defiance
            // is up both the status check and CanUse refuse. Self-targeted, so no targetOverride.
            if (KeepDefiance
                && !StatusHelper.PlayerHasStatus(true, StatusID.Defiance)
                && DefiancePvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
            if (PullTincture && IfritExBurst.InIfritOpener(OpenerWindow) && UseBurstMedicine(out act))
            {
                return true;
            }

            if (IfritExBurst.InBurst)
            {
                // Inner Release lives here rather than in AttackAbility because AttackAbility is
                // skipped whenever no hostile is within 3 y (tank melee range) — i.e. during the
                // run-in from the arena edge and during every nail-to-nail hop. Self-targeted.
                // skipTTKCheck: a ~15 s kill would otherwise have RSR's time-to-kill gate refuse
                // exactly the cooldown the whole plan is built around.
                if (InnerReleasePvE.CanUse(out act, skipTTKCheck: true))
                {
                    return true;
                }

                // Onslaught doubles as a 20 y gap closer. Only out of melee range, so in-range
                // weaving stays with AttackAbility below and this cannot steal a burst weave slot.
                if (!HasHostilesInRange
                    && OnslaughtPvE.CanUse(out act, usedUp: true, skipTTKCheck: true,
                        targetOverride: KillOrder))
                {
                    return true;
                }
            }
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
            // skipTTKCheck on everything below: the kill is short and TTK gating would otherwise
            // reject precisely the burst cooldowns this rotation exists to dump.

            // 700p, the largest oGCD WAR has, and only available in the brief Wrathful window
            // opened by the third Inner Release Fell Cleave. Always first.
            // NOTE: no targetOverride — Primal Wrath is a 5 y AoE centred on the player, not a
            // hostile-target pick, so overriding its declared targeting could break it.
            if (IsWrathful && PrimalWrathPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // 420p and the only repeatable (30 s) damage oGCD, so its timer must start immediately.
            // skipStatusNeed is MANDATORY: RSR declares Upheaval's StatusNeed as [SurgingTempest]
            // (WarriorRotation.ModifyUpheavalPvE) so that a normal WAR never Upheavals before the
            // 10% buff is up. This plan deliberately never applies Surging Tempest inside the burst
            // (three GCDs of combo never pays back in a ~7-GCD kill), and StatusHelper's
            // "will this status end" returns TRUE when the status is absent entirely — so without
            // this flag Upheaval would be rejected on literally every frame of the fight.
            if (UpheavalPvE.CanUse(out act, skipStatusNeed: true, skipAoeCheck: true,
                    skipTTKCheck: true, targetOverride: KillOrder))
            {
                return true;
            }

            // See InfuriateAllowed — this gate is the single most important line in the file.
            // Self-targeted resource action, so no targetOverride.
            if (InfuriateAllowed && InfuriatePvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
            {
                return true;
            }

            // 150p x3 charges that will never come back inside this kill: dump them into otherwise
            // empty weave slots. Ranked below Infuriate so it can never delay an Inner Chaos.
            if (OnslaughtPvE.CanUse(out act, usedUp: true, skipTTKCheck: true,
                    targetOverride: KillOrder))
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

        // Primal Rend: 720p guaranteed critical direct hit AND a 20 y jump, so the run-in from the
        // arena edge (and every hop to the next Infernal Nail) costs zero time. skipAoeCheck so its
        // 50% splash cannot make RSR's AoE-count config reject it on a single boss.
        if (PrimalRendPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
        {
            return true;
        }

        // 800p — the highest raw potency in the kit, and its Ready buff only lasts 20 s.
        if (PrimalRuinationPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
        {
            return true;
        }

        // 700p ALWAYS a guaranteed critical direct hit (~1150 effective): WAR's biggest single GCD.
        // Ranked above Fell Cleave, which is safe because Nascent Chaos is only ever up after the
        // three Inner Release Fell Cleaves have already been spent (see InfuriateAllowed).
        if (InnerChaosPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // The three free auto-CDH Fell Cleaves. skipStatusProvideCheck mirrors WAR_Reborn: with
        // Inner Release stacks the action costs no Beast Gauge, and RSR needs to be told so.
        if (!HasNascentChaos && InnerReleaseStacks > 0
            && FellCleavePvE.CanUse(out act, skipStatusProvideCheck: true, targetOverride: order))
        {
            return true;
        }

        // Gauge spender outside Inner Release (only reachable if the kill runs past the burst).
        if (!HasNascentChaos && BeastGauge >= 50
            && FellCleavePvE.CanUse(out act, skipStatusProvideCheck: true, targetOverride: order))
        {
            return true;
        }

        // ---- Sustain path. Only reached once every burst spender above is unavailable, i.e. the
        // skip failed and this is now a real 1.5-3 minute fight. Surging Tempest (+10% for 60 s) is
        // deliberately NOT part of the burst: three GCDs to apply it never pays off inside a
        // ~7-GCD kill, but it pays for itself many times over the moment the fight runs long.
        if (UseSurgingTempest
            && StatusHelper.PlayerWillStatusEndGCD(3, 0, true, StatusID.SurgingTempest)
            && StormsEyePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Storm's Path is the higher-potency finisher and also feeds Beast Gauge; Storm's Eye above
        // only steals the finisher slot when the buff actually needs refreshing.
        if (StormsPathPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (MaimPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (HeavySwingPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // 20 y, 150p ranged filler: never drop a GCD while travelling to the next nail.
        if (TomahawkPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
