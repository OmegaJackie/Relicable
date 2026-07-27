namespace RelicBurstRotations.Rotations;

/// <summary>
/// MONK — solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135, roughly 0.50x the
/// damage of current gear), farming the Bowl of Embers (Extreme), TerritoryType 295, for Nexus
/// light. Epic Echo (+300%) makes this a 12-20 second kill, i.e. about 7-11 Monk GCDs.
///
/// CONSEQUENCE: this is not a rotation, it is a fixed opener with a short tail. Every 2-minute
/// (Brotherhood), 90-second (Riddle of Wind) and 60-second (Riddle of Fire) cooldown fires exactly
/// once and never returns. Only Perfect Balance (2 charges) is used twice. So: no cooldown
/// alignment, no burst-window holds, no Nadi banking, no IsBurst gating. Spend everything at the
/// pull. Phantom Rush is UNREACHABLE here (needs a third blitz with both Nadi open, ~13+ GCDs
/// away) — it is listed below only so the branch exists, never as a goal.
///
/// PLANNED SEQUENCE (what this file tries to produce)
///   pre-pull (out of combat, in 295):  Form Shift  ->  Meditation (opens all 5 Chakra out of
///                                      combat)  ->  Thunderclap in (deletes the 2-3 s walk from
///                                      the arena edge)  ->  optional tincture
///   GCD 1  Dragon Kick            (consumes Formless Fist, banks Opo-opo's Fury)
///     weave  Perfect Balance      (The Balance's rule: PB only ever follows an Opo GCD)
///     weave  Brotherhood          (DEVIATION: t=0, not 5 s/7 s — there is no party to align with)
///   GCD 2  Leaping Opo            (PB stack 1 -> Opo-opo Beast Chakra, 460 guaranteed crit)
///     weave  Riddle of Fire       (DEVIATION: one GCD early; +15%/20 s covers more of a 15 s kill)
///     weave  Riddle of Wind       (90 s cd, never returns; arms Wind's Reply)
///   GCD 3  Dragon Kick            (PB stack 2)
///     weave  The Forbidden Chakra (400 potency from the pre-pull Chakra, zero GCD cost)
///   GCD 4  Leaping Opo            (PB stack 3 -> three IDENTICAL Chakra = Lunar blitz)
///   GCD 5  Elixir Burst           (900, opens Lunar Nadi)
///   GCD 6  Dragon Kick            (form/Fury bridge across the two Replies)
///   GCD 7  Fire's Reply           (1400 — but see the note on the Reply guards in GeneralGCD:
///                                 the blitz's own Formless Fist usually makes Wind's Reply land
///                                 first and pushes Fire's Reply one GCD later)
///   GCD 8  Wind's Reply           (1040)
///   GCD 9  Leaping Opo            (spends Fire's Reply's Formless Fist; Ifrit is usually dead here)
///   tail   Perfect Balance charge 2 -> Dragon Kick / Leaping Opo / Dragon Kick -> Elixir Burst
///
/// DOUBLE LUNAR, NOT SOLAR/LUNAR. The PB window is filled with Opo-opo GCDs only. Solar is ~140
/// potency richer on paper but needs a REAR positional (Demolish), banks Raptor/Coeurl Fury this
/// fight will never spend, and only pays off by enabling a third blitz that cannot happen. Double
/// lunar is positional-free, which is what an automated rotation wants.
///
/// NAIL PHASE. Post-patch-4.56, damaging Ifrit while Infernal Nails are alive makes him
/// temporarily invulnerable and can lock the fight into an unwinnable Hellfire loop. While
/// <see cref="IfritExBurst.ShouldKillNails"/> is true this rotation (a) retargets every damage
/// action onto the nails via <c>targetOverride: TargetType.LowMaxHP</c>, and (b) HOLDS Perfect
/// Balance, every Masterful Blitz, and both Replies — all of those are self-centred or
/// target-centred AoE and would splash Ifrit if you are anywhere near him. Nails die to roughly
/// one GCD at these damage numbers, so the loss is trivial and the downside is catastrophic.
/// Thunderclap stays available so Monk can chain-dash between ring positions, which is where the
/// nail phase actually spends its time.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK
///  * "Perfect Balance can be used out of combat" was flagged UNCONFIRMED in the job plan. It is
///    now RESOLVED as NO: RSR's own gate is
///    <c>ModifyPerfectBalancePvE: ActionCheck = () =&gt; InCombat &amp;&amp; ...</c>
///    (MonkRotation.cs:380). PB therefore stays where The Balance puts it — weaved after GCD 1.
///  * Six-sided Star is deliberately NEVER pressed. At 10 Chakra under Brotherhood it is Monk's
///    largest single button (~1580) and is the only realistic candidate for the documented
///    "finishing blow worth 20-30% of Ifrit's HP" skip condition — but it has a DOUBLE recast, and
///    two Forbidden Chakra (800, free) plus two filler GCDs (460+320) equal the same 1580 over the
///    same wall clock while staying divisible. If a future implementer wants to gamble on the skip
///    condition, this is the knob to turn.
///  * No AoE line at all. Monk's AoE GCDs are 120-160 potency vs 320/460 single target, the nails
///    are one per clock position (never 3 in a 5 y radius), and every Monk AoE is self-centred —
///    pressing one next to Ifrit during nails is the single most likely way to brick a run.
///    Enlightenment / Howling Fist are likewise omitted: they are AoE, and they eat the Chakra that
///    The Forbidden Chakra wants.
///  * Outside territory 295 this behaves as a plain frontloaded Monk: the burst oGCDs and the nail
///    logic are gated on <see cref="IfritExBurst"/> and fall through to the base class, exactly as
///    the canonical BLM_IfritEX template does.
///  * Monk's burst is spread across ~9 GCDs rather than concentrated in one nuke, so it will land
///    in the nail phase more often than a true frontload job (NIN/BLM). The nail path here is
///    treated as a first-class case, not an edge case.
/// </summary>
[Rotation("Ifrit EX Burst (MNK)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/MNK_IfritEX.cs")]
[ExtraRotation]
public sealed class MNK_IfritEX : MonkRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Hold Perfect Balance, Masterful Blitz and the Replies while nails are up (they are AoE and would splash Ifrit)")]
    public bool HoldAoeDuringNails { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Auto Perfect Balance (always weaved after an Opo-opo GCD)")]
    public bool AutoPerfectBalance { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Form Shift out of combat to bank Formless Fist for the first Dragon Kick")]
    public bool AutoFormShift { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Thunderclap to close the gap (arena run-in and nail-to-nail travel)")]
    public bool AutoThunderclap { get; set; } = true;

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
        ImGui.Text($"HoldAoeForNails: {HoldAoeForNails}");
        ImGui.Text($"HostilesInMaxRange: {NumberOfAllHostilesInMaxRange}");
        ImGui.Text($"Chakra: {Chakra}  OpoOpoFury: {OpoOpoFury}");
        ImGui.Text($"PerfectBalance: {HasPerfectBalance}  FormlessFist: {HasFormlessFist}");
        ImGui.Text($"Brotherhood: {InBrotherhood}  RoF: {HasRiddleOfFire}");
        ImGui.Text($"FiresRumination: {HasFiresRumination}  WindsRumination: {HasWindsRumination}");
        ImGui.Text($"Nadi - Lunar: {HasLunar}  Solar: {HasSolar}");
    }

    #endregion

    #region Extra Methods

    /// <summary>
    /// Target override for every damage action: nails first while a nail set is up, otherwise
    /// <c>default</c> (== <c>TargetType.Big</c>), which is RSR's normal behaviour and a no-op.
    /// </summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// True while we must not fire anything with a splash radius. Every Masterful Blitz
    /// (Elixir Burst / Rising Phoenix / Celestial Revolution / Phantom Rush) is self-centred
    /// falloff AoE, Fire's Reply is a 20 y AoE around the target and Wind's Reply is a line — any
    /// of them can clip Ifrit while we are killing nails, which is exactly what triggers his
    /// post-4.56 invulnerability. Perfect Balance is held too, so no blitz is ever built here.
    /// </summary>
    private bool HoldAoeForNails =>
        HoldAoeDuringNails && NailPriority && IfritExBurst.ShouldKillNails(HostileTarget);

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss.
    public override bool CanHealSingleSpell => false;

    public override bool CanHealAreaSpell => false;

    /// <summary>
    /// Opo-opo Form GCDs, single target only (Arm of the Destroyer / Shadow of the Destroyer are
    /// deliberately absent — see the header). Leaping Opo gates on <c>OpoOpoFury &gt; 0</c> and
    /// Dragon Kick on <c>OpoOpoFury == 0</c>, so the two are mutually exclusive and the order below
    /// is just "the big one first". Bootshine is the sub-92 fallback, same shape MNK_Reborn uses.
    /// </summary>
    private bool OpoOpoGCD(TargetType order, out IAction? act)
    {
        if (LeapingOpoPvE.EnoughLevel)
        {
            if (LeapingOpoPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
            {
                return true;
            }
        }

        if (DragonKickPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        if (BootshinePvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        act = null;
        return false;
    }

    /// <summary>
    /// Raptor Form GCDs. DEVIATION FROM THE JOB PLAN, and it is a correction, not a preference:
    /// the plan's "strictly alternate Dragon Kick -&gt; Leaping Opo forever" filler is impossible.
    /// RSR's own gates (MonkRotation.cs:549-565) show every Opo action needs
    /// <c>InOpoopoForm || HasFormlessFist || HasPerfectBalance</c>, and each Opo GCD pushes you into
    /// Raptor Form. Outside Perfect Balance / Formless Fist the form chain is a hard 3-GCD cycle, so
    /// the Raptor and Coeurl branches must exist or the rotation stalls the moment PB drops.
    /// Fourpoint Fury / Rockbreaker (the AoE members) are still omitted.
    /// </summary>
    private bool RaptorGCD(TargetType order, out IAction? act)
    {
        if (RisingRaptorPvE.EnoughLevel)
        {
            if (RisingRaptorPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
            {
                return true;
            }
        }

        if (TwinSnakesPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        if (TrueStrikePvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        act = null;
        return false;
    }

    /// <summary>
    /// Coeurl Form GCDs. Pouncing Coeurl / Demolish carry a REAR positional; we never reposition
    /// for it (Ifrit is stationary and melee uptime is free, so RSR's own True North handling in
    /// the base ability chain is the only concession made).
    /// </summary>
    private bool CoeurlGCD(TargetType order, out IAction? act)
    {
        if (PouncingCoeurlPvE.EnoughLevel)
        {
            if (PouncingCoeurlPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
            {
                return true;
            }
        }

        if (DemolishPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        if (SnapPunchPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        act = null;
        return false;
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (IfritExBurst.InIfritEx)
        {
            // Pre-pull, out of combat only: Meditation opens ALL FIVE Chakra when used outside
            // combat, which is what makes The Forbidden Chakra (400 potency, zero GCD cost)
            // available in the very first weave slot instead of ~15 s in. It also triggers the
            // weaponskill recast, hence the hard !InCombat gate — it must never delay a live GCD.
            // The four Meditation buttons are the same action at four unlock tiers; RSR's
            // ActionCheck is Chakra < 5 on all of them, so whichever one the player actually has
            // resolves and the rest no-op.
            if (!InCombat && Chakra < 5)
            {
                if (ForbiddenMeditationPvE.CanUse(out act))
                {
                    return true;
                }

                if (EnlightenedMeditationPvE.CanUse(out act))
                {
                    return true;
                }

                if (InspiritedMeditationPvE.CanUse(out act))
                {
                    return true;
                }

                if (SteeledMeditationPvE.CanUse(out act))
                {
                    return true;
                }
            }

            // Gap closer. This lives in EmergencyAbility rather than AttackAbility on purpose:
            // AttackAbility is skipped entirely when HasHostilesInRange is false (3 y for melee),
            // which is exactly the situation Thunderclap exists to fix. It covers both the run-in
            // from the arena edge and the ring-to-ring travel of the nail phase, where travel time
            // — not damage — is what dominates the clock. No targetOverride: Thunderclap's
            // SpecialType is HostileFriendlyMovingForward and an override would replace that.
            // KNOWN LIMITATION: it dashes to RSR's chosen target, which during the nail phase may
            // still be Ifrit rather than the nail our GCDs are actually hitting.
            if (AutoThunderclap && !HasHostilesInRange
                && ThunderclapPvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
            {
                return true;
            }

            // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes
            // here. Expect this to silently no-op: RSR's default TinctureUseType is "high-end duty
            // only" and territory 295 is not flagged high-end. That is expected, not a bug.
            if (PullTincture && IfritExBurst.InIfritOpener(OpenerWindow) && UseBurstMedicine(out act))
            {
                return true;
            }

            // Perfect Balance. Placed in EmergencyAbility, not AttackAbility, for the same reason
            // MNK_Reborn does it: from AttackAbility there is a real chance RSR does not get it
            // pressed inside the first weave window.
            // The IsLastGCD gate is The Balance's hard rule — PB must follow an Opo-opo GCD so its
            // three free-form stacks land on Opo GCDs and produce a Lunar blitz.
            // usedUp: true dumps both charges as they come; skipTTKCheck because RSR ships PB-
            // adjacent burst cooldowns with TimeToKill = 10 and this kill is shorter than that.
            if (AutoPerfectBalance && IfritExBurst.InBurst && !HoldAoeForNails
                && IsLastGCD(true, DragonKickPvE, LeapingOpoPvE, BootshinePvE)
                && PerfectBalancePvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
            {
                return true;
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
            // Everything below is skipTTKCheck: RSR ships Brotherhood, Riddle of Fire and Riddle of
            // Wind with ActionConfig.TimeToKill = 10 (MonkRotation.cs:470-501). On a 12-20 s kill
            // AverageTTK sits under that threshold for most of the fight, so without this the TTK
            // gate would reject precisely the three cooldowns the whole plan is built on.
            //
            // No targetOverride on any of these three: Brotherhood's Setting.TargetType is Self and
            // both Riddles are IsFriendly, so an override would replace their declared targeting.

            // DEVIATION FROM THE BALANCE: Brotherhood fires at t=0 instead of the standard 5 s/7 s
            // delay. That delay exists only to line the 20 s party buff up with other players'
            // 2-minute windows; solo there is nobody to align with, and firing it now means its
            // +5% covers every GCD of the kill and Meditative Brotherhood starts flooding Chakra
            // immediately (which is what pays for the 2nd and 3rd Forbidden Chakra).
            if (BrotherhoodPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // DEVIATION: standard openers put Riddle of Fire at GCD 3. Fired as early as possible
            // instead — +15% for 20 s applied at t~2 s covers more of a 15 s kill than the same
            // buff at t~4 s. Also arms Fire's Reply via Fire's Rumination.
            if (RiddleOfFirePvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // 90 s cooldown; it will never come back in this fight, so there is nothing to hold it
            // for. Its real value here is arming Wind's Reply (1040), not the auto-attack speed.
            if (RiddleOfWindPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // 400 potency, single target, zero GCD cost. Fired immediately on the pre-pull Chakra
            // rather than held into Riddle of Fire: holding it two GCDs would gain ~60 potency but
            // delay 400 potency by ~4 s, and for minimum time-to-kill earlier always wins.
            // Brotherhood refills the gauge, so expect 2-3 casts across the kill. RSR's own gate is
            // InCombat && Chakra >= 5, so no manual gauge check is needed.
            if (TheForbiddenChakraPvE.CanUse(out act, skipTTKCheck: true, targetOverride: KillOrder))
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
        bool holdAoe = HoldAoeForNails;

        // Pre-pull setup, out of combat, in the farm duty only. Form Shift banks Formless Fist so
        // that GCD 1 (Dragon Kick) satisfies its Opo-opo Form requirement from a cold start instead
        // of opening at reduced value. RSR refuses it automatically once Formless Fist is up
        // (StatusProvide = [FormlessFist]).
        if (AutoFormShift && !InCombat && IfritExBurst.InIfritEx
            && FormShiftPvE.CanUse(out act))
        {
            return true;
        }

        if (!holdAoe)
        {
            // Masterful Blitz spenders, fired the instant they are available — no Riddle-of-Fire
            // window gating, because there is only ever one window. All are skipAoeCheck: RSR
            // classifies them as AoE and the user's AoEType setting would otherwise refuse them
            // against a single target.
            //
            // Only Elixir Burst is actually expected to fire (double lunar). Phantom Rush needs
            // both Nadi open and is unreachable inside this fight; Rising Phoenix belongs to the
            // solar branch we deliberately do not build; Celestial Revolution is the 600-potency
            // consolation prize you get from two mismatched Chakra types — if it ever fires, the
            // Perfect Balance window was filled wrong. They are present so the rotation cannot
            // deadlock holding an unspendable blitz.
            if (PhantomRushPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // TornadoKickPvE / FlintStrikePvE / ElixirFieldPvE are the pre-90 forms of the three
            // blitzes above; kept as fallbacks so the file is not silently level-100-only.
            if (TornadoKickPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (RisingPhoenixPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (FlintStrikePvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (ElixirBurstPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (ElixirFieldPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (CelestialRevolutionPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // Fire's Reply is listed above Wind's Reply so that whenever BOTH are legal on the same
            // GCD the 1400 goes out first — Monk's largest hit in this fight and its only real shot
            // at the documented "finishing blow worth 20-30% of Ifrit's HP" skip condition.
            //
            // The !HasPerfectBalance && !HasFormlessFist guard is MNK_Reborn's (minus its extra
            // IsLastGCD-was-an-Opo clause), and it matters: Fire's Reply grants Formless Fist, so
            // firing it while a Formless Fist or a PB window is already open throws that grant away.
            // The PlayerWillStatusEnd escape hatch stops us from ever letting the Rumination expire
            // unspent.
            //
            // CONSEQUENCE, and it is intentional: every Masterful Blitz also grants Formless Fist,
            // so on the GCD straight after Elixir Burst this guard is false and Wind's Reply (whose
            // guard is only !HasPerfectBalance) goes first. The realised tail is therefore
            // Elixir Burst -> Wind's Reply -> Opo (spends the blitz's Formless Fist) -> Fire's
            // Reply, not the Fire-then-Wind order the header sketches. Same GCD count, same two
            // Replies, Fire's Reply one GCD later. Do not "fix" this by dropping the guard without
            // re-checking it against the Formless Fist bookkeeping below.
            if ((!HasPerfectBalance && !HasFormlessFist)
                || StatusHelper.PlayerWillStatusEnd(5, true, StatusID.FiresRumination))
            {
                if (FiresReplyPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
                {
                    return true;
                }
            }

            // Wind's Reply grants nothing that constrains the form chain, so it is freely placeable
            // — which is exactly why it is the one that gets pushed back rather than Fire's Reply.
            if (!HasPerfectBalance
                || StatusHelper.PlayerWillStatusEnd(5, true, StatusID.WindsRumination))
            {
                if (WindsReplyPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
                {
                    return true;
                }
            }
        }

        // Bookend every Perfect Balance / blitz with an Opo GCD: Formless Fist is always spent on
        // an Opo action (Leaping Opo at 460 with a guaranteed crit, or Dragon Kick to re-arm the
        // Fury), never on a Raptor or Coeurl button.
        if (HasFormlessFist && OpoOpoGCD(order, out act))
        {
            return true;
        }

        // THE DOUBLE-LUNAR WINDOW. While Perfect Balance is up, press Opo-opo actions and nothing
        // else: three identical Beast Chakra = Elixir Burst (900) and the Lunar Nadi. This is the
        // whole reason the solar branch is not built — no positionals, no banked Fury that this
        // fight will never spend. The second Elixir Burst deliberately overcaps the Lunar Nadi;
        // that costs nothing, because the Solar Nadi that would enable Phantom Rush is unreachable
        // inside a 12-20 s kill anyway.
        if (HasPerfectBalance && OpoOpoGCD(order, out act))
        {
            return true;
        }

        // Filler. Outside PB / Formless Fist the form chain is a hard Opo -> Raptor -> Coeurl
        // cycle, so all three branches are tried and the form status decides which one is legal.
        // Order is Coeurl -> Raptor -> Opo (MNK_Reborn's order): the later a form is in the cycle,
        // the more urgent it is to clear it.
        if (CoeurlGCD(order, out act))
        {
            return true;
        }

        if (RaptorGCD(order, out act))
        {
            return true;
        }

        if (OpoOpoGCD(order, out act))
        {
            return true;
        }

        // Last resort: keep Formless Fist rolling if there is genuinely nothing to press (e.g.
        // mid-travel in the nail phase with no target in range). Scoped to territory 295 so that
        // outside the farm duty this rotation falls straight through to the base class instead of
        // making Form Shift a universal terminal GCD.
        if (AutoFormShift && IfritExBurst.InIfritEx && FormShiftPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
