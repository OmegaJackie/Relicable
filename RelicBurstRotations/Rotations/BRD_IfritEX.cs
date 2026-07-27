namespace RelicBurstRotations.Rotations;

/// <summary>
/// SCENARIO: solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135, roughly half a
/// geared level-100 weapon), farming "the Bowl of Embers (Extreme)" — TerritoryType 295 — for
/// Nexus relic light. Epic Echo (+300%) applies. Goal is minimum wall-clock time from pull to
/// Ifrit's death. Light credit only pays out on duty COMPLETION with the relic equipped, so a slow
/// run is still a run: never bail just because the skip failed.
///
/// BURST PLAN (see the job plan for the full reasoning). The whole kill is ONE buff window, so
/// nothing is held for a second one and no cooldown-alignment logic exists anywhere in this file.
///
///   PRE-PULL (out of combat, inside 295, hostile in range — EmergencyAbility):
///     0. Tincture (Dexterity Gemdraught) — config-gated, off by default.
///     1. Raging Strikes   (+15% dmg, 20 s) — usable OOC, unlike songs.
///     2. Battle Voice     (+20% DH rate, 20 s) — also usable OOC.
///        Barrage is deliberately NOT pre-pulled: its 10 s window would lapse before the
///        Refulgent Arrow that consumes it.
///
///   IN COMBAT (AttackAbility weaves — the same two buffs re-appear here as a fallback for when
///   the pre-pull did not land, where they cost nothing because StatusProvide declines them):
///     3. Army's Paeon  -> 4. Mage's Ballad -> 5. The Wanderer's Minuet.
///        Songs cannot be used out of combat, and Coda is granted the instant the song action is
///        USED (not when it ends). Burning all three at the pull buys a THREE-Coda Radiant Finale
///        (+6% instead of +2%) and upgrades Radiant Encore from 700 to 1,100 potency. This is the
///        single biggest job-specific gain available here and is something you would never do in a
///        real raid, where you need two minutes of song uptime. Wanderer's is pressed LAST so it
///        is the song left running (its Repertoire -> Pitch Perfect conversion is the only song
///        effect that matters in a 20 s fight).
///     6. Radiant Finale (3 Coda) -> 7. Barrage -> 8. Empyreal Arrow -> Sidewinder ->
///        Heartbreak Shot (all 3 charges) -> Pitch Perfect at 3 stacks.
///
///   GCDs (GeneralGCD, evaluated as a flat priority list every GCD):
///     Radiant Encore (conditional finisher, 1,100) > Barrage'd Refulgent Arrow (3 x 280) >
///     Resonant Arrow (640) > Caustic Bite -> Stormbite (opened with, not Burst Shot: they
///     snapshot under the already-running pre-pull buffs and DoT ticks are the ONLY meaningful
///     source of Repertoire) > Blast Arrow > Apex Arrow above the gauge threshold >
///     Refulgent Arrow on a Hawk's Eye proc > Burst Shot filler.
///
/// DELIBERATE OMISSIONS (do not re-add them):
///   * Iron Jaws — a 100-potency GCD to re-snapshot DoTs that were already applied under full
///     buffs and will outlive a 20 s fight. Net loss here.
///   * Ladonsbite / Shadowbite / Rain of Death — the nails ring the arena 10-20 y apart; BRD's
///     AoE footprints (5 y circles, a 12 y cone) never span two of them, and every one of them is
///     weaker than Burst Shot / Heartbreak Shot on a single target.
///   * Head Graze (Ifrit's EX casts are uninterruptible, nails do not cast), Repelling Shot (a
///     backflip that drags you out of range), Troubadour / Nature's Minne / The Warden's Paean
///     (no solo value at level 100), Peloton (drops on combat entry).
///   * Bloodletter / Heavy Shot / Straight Shot / Venomous Bite / Windbite / Quick Nock /
///     Wide Volley — pre-upgrade actions, dead at level 100.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK:
///   * BRD is a flat, sustained job; its burst is only ~1.4x its own sustained rate versus 2x+ for
///     a true frontloader. Reaching the Infernal Nail phase on a meaningful fraction of pulls is
///     EXPECTED, not an edge case — the nail handling below is first-class, not a fallback.
///   * While any Infernal Nail is alive, damaging Ifrit is strictly harmful (post-4.56 he goes
///     temporarily invulnerable). BRD has a hazard no other job has: the DoTs keep ticking on
///     Ifrit and cannot be cancelled. What we CAN do — and do — is never REFRESH them while nails
///     are up, and never spend Radiant Encore / Resonant Arrow / a Barrage'd Refulgent on a nail.
///   * Soul Voice starts at 0 on every zone-in and only gains 5 per Repertoire proc, so a 20 s
///     window yields roughly 35-50. Apex Arrow is marginal (break-even vs Burst Shot is ~32 gauge)
///     and Blast Arrow (needs an 80+ gauge Apex) is UNREACHABLE on the fast path. Neither is ever
///     waited for; both are simply taken if they happen to become available.
///   * RSR's tincture setting defaults to "high-end duty only" and territory 295 is not flagged
///     high-end, so UseBurstMedicine will often silently no-op. That is expected; not worked around.
/// </summary>
[Rotation("Ifrit EX Burst (BRD)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/BRD_IfritEX.cs")]
[ExtraRotation]
public sealed class BRD_IfritEX : BardRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Pre-pull Raging Strikes and Battle Voice (out of combat)")]
    public bool PrePullBuffs { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Bank all three songs for a 3-Coda Radiant Finale")]
    public bool BankAllCoda { get; set; } = true;

    [Range(0, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
    public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;

    [Range(20, 100, ConfigUnitType.None, 5)]
    [RotationConfig(CombatType.PvE, Name = "Minimum Soul Voice for Apex Arrow")]
    public float ApexSoulVoice { get; set; } = 40;

    // 0.55, deliberately ABOVE the 50% Infernal Nail threshold rather than the old 0.33.
    // Radiant Encore is gated on !NailsUp, so a threshold below 50% could only ever release it in
    // the sliver between the nail spawn and 30% -- i.e. on runs where the skip had already failed.
    // Held damage has to be spent carrying Ifrit THROUGH 50%, not after it.
    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Fire Radiant Encore at or below this target HP")]
    public float EncoreHpThreshold { get; set; } = 0.55f;

    #endregion

    #region Tracking Properties

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"InIfritEx: {IfritExBurst.InIfritEx}");
        ImGui.Text($"InBurst: {IfritExBurst.InBurst}");
        ImGui.Text($"CombatSeconds: {IfritExBurst.CombatSeconds:F1}");
        ImGui.Text($"InOpenerWindow: {IfritExBurst.InOpenerWindowOf(OpenerWindow)}");
        ImGui.Text($"NailsUp: {NailsUp}");
        ImGui.Text($"HostilesInMaxRange: {NumberOfAllHostilesInMaxRange}");
        ImGui.Text($"Song: {Song} | SongTime: {SongTime:F1}");
        ImGui.Text($"Repertoire: {Repertoire} | SoulVoice: {SoulVoice}");
        ImGui.Text($"RS/BV/RF: {HasRagingStrikes}/{HasBattleVoice}/{HasRadiantFinale}");
        ImGui.Text($"Barrage: {HasBarrage} | Resonant: {HasResonantArrow} | HawksEye: {HasHawksEye}");
        ImGui.Text($"ShouldFireEncore: {ShouldFireEncore}");
    }

    #endregion

    #region Extra Methods

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// True when a nail set is up and we should be shooting nails, not Ifrit. Everything that must
    /// never land on a nail (the big single hits) and everything that must never land on Ifrit
    /// (DoT refreshes) is gated on this.
    /// </summary>
    private bool NailsUp => NailPriority && IfritExBurst.ShouldKillNails(HostileTarget);

    /// <summary>
    /// The pull-time window. Out of combat this means "standing in 295 with something to shoot"
    /// (there is no /countdown when you solo-pull a duty, so CountDownAction never runs and the
    /// pre-pull buttons have to be pressed here instead). In combat it is the opener window.
    /// </summary>
    private bool InIfritSetup => IfritExBurst.InIfritEx
        && (InCombat
            ? IfritExBurst.InOpenerWindowOf(OpenerWindow)
            : HasHostilesInMaxRange);

    /// <summary>
    /// Radiant Encore is BRD's only credible answer to the documented skip condition ("the
    /// finishing attack takes out 20-30% of HP in one hit"), so it is held for a real trigger
    /// rather than pressed in a fixed slot. Only ever evaluated after RadiantEncorePvE.CanUse has
    /// already succeeded, i.e. Radiant Encore Ready is definitely present.
    /// </summary>
    private bool ShouldFireEncore
    {
        get
        {
            IBattleChara? target = HostileTarget;

            // (a) it can carry Ifrit through the last threshold or outright kill him.
            if (target != null && target.GetHealthRatio() <= EncoreHpThreshold)
            {
                return true;
            }

            // (b) never lose the +15%/+20%/+6% envelope by holding too long.
            if (HasRagingStrikes && StatusHelper.PlayerWillStatusEnd(3, true, StatusID.RagingStrikes))
            {
                return true;
            }

            // (c) Radiant Encore Ready (30 s from Radiant Finale) is about to lapse.
            return StatusHelper.PlayerWillStatusEnd(3, true, StatusID.RadiantEncoreReady);
        }
    }

    // Solo: there is nobody to heal and healing GCDs are pure DPS loss. Scoped to territory 295 so
    // that outside the farm duty this rotation falls through to RSR's inherited behaviour instead
    // of permanently disabling the heal GCD stages (matches BLM_IfritEX, the canonical template).
    public override bool CanHealSingleSpell => !IfritExBurst.InIfritEx && base.CanHealSingleSpell;

    public override bool CanHealAreaSpell => !IfritExBurst.InIfritEx && base.CanHealAreaSpell;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
        // (BRD's MedicineType is Dexterity, resolved by UseBurstMedicine itself.)
        if (PullTincture && InIfritSetup && UseBurstMedicine(out act))
        {
            return true;
        }

        // Raging Strikes and Battle Voice are the only two burst buttons BRD can press OUT of
        // combat, which buys back the two weave slots the three-song Coda bank needs. Songs and
        // Radiant Finale cannot be pre-pulled (ActionCheck = InCombat / requires a Coda), so they
        // live in AttackAbility below. Both buffs are 20 s, longer than the intended kill.
        if (PrePullBuffs && IfritExBurst.InIfritEx && !InCombat && HasHostilesInMaxRange)
        {
            if (RagingStrikesPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            if (BattleVoicePvE.CanUse(out act, skipTTKCheck: true))
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

        // Outside the farm duty this rotation has nothing special to say — fall straight through
        // to RSR's inherited behaviour.
        if (!IfritExBurst.InBurst)
        {
            return base.AttackAbility(nextGCD, out act);
        }

        TargetType order = KillOrder;
        bool nails = NailsUp;

        // --- Burst buffs ------------------------------------------------------------------
        // Both of these are no-ops when the pre-pull landed (StatusProvide declines them while the
        // buff is up), so this block costs nothing in the normal case and is the fallback when it
        // did not. Held while nails are alive so the ~t=120 s re-burst gets them instead.
        if (!nails)
        {
            if (RagingStrikesPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            if (BattleVoicePvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
        }

        // --- Coda bank --------------------------------------------------------------------
        // Only ever churn songs while Radiant Finale is actually available, because feeding
        // Radiant Finale is the ONLY reason to do it. Once Finale is spent (110 s recast) this
        // block goes quiet, so the nail phase never loses Wanderer's Minuet to a re-bank. It wakes
        // back up at ~t=115 s for the second window, which is exactly when we want it.
        // Songs are self-targeted buffs: never pass targetOverride to them.
        if (!nails && !RadiantFinalePvE.Cooldown.IsCoolingDown)
        {
            if (BankAllCoda && NoSong && !IsLastAbility(ActionID.ArmysPaeonPvE)
                && ArmysPaeonPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            if (BankAllCoda && InArmys && !IsLastAbility(ActionID.MagesBalladPvE)
                && MagesBalladPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Wanderer's last, so it is the song we keep: its Repertoire -> Pitch Perfect
            // conversion is the only song effect worth anything in a fight this short.
            if ((NoSong || InArmys || InMages) && !IsLastAbility(ActionID.TheWanderersMinuetPvE)
                && TheWanderersMinuetPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
        }

        // Radiant Finale, once the bank is full (or once Wanderer's is spent and no more Coda is
        // coming). RadiantFinalePvE's own ActionCheck already refuses at 0 Coda.
        if (!nails && !NoSong && (InWanderers || TheWanderersMinuetPvE.Cooldown.IsCoolingDown)
            && RadiantFinalePvE.CanUse(out act, skipTTKCheck: true))
        {
            return true;
        }

        // Barrage: gated on Raging Strikes so the 10 s window and the 3 x 280 Refulgent Arrow it
        // enables both land inside the damage buffs. Also grants Resonant Arrow Ready (30 s).
        if (!nails && HasRagingStrikes && BarragePvE.CanUse(out act, skipTTKCheck: true))
        {
            return true;
        }

        // --- Damage oGCDs -----------------------------------------------------------------
        // These DO take the nail-first target override: during the nail phase they are the bulk of
        // the damage, and BRD's 25 y range means it kills ring nails without moving at all.

        // 260 potency and a GUARANTEED Repertoire proc (Enhanced Empyreal Arrow). 15 s recast, so
        // it fires roughly twice in the window. Never held.
        if (EmpyrealArrowPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 100 / 220 / 360 at 1 / 2 / 3 stacks — only worth pressing at 3, or to dump before the
        // song lapses. PitchPerfectPvE's ActionCheck already requires Wanderer's + Repertoire > 0.
        if (PitchPerfectPvE.CanUse(out act, skipComboCheck: true, skipAoeCheck: true,
                skipTTKCheck: true, targetOverride: order))
        {
            if (Repertoire >= 3 || (Repertoire > 0 && SongEndAfter(3)))
            {
                return true;
            }
        }

        // 400 potency, 60 s recast: exactly one use on the fast path, no alignment needed.
        if (SidewinderPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 180 x 3 charges on a 15 s recast. usedUp: dump every charge, there is no second window
        // to bank them for.
        if (HeartbreakShotPvE.CanUse(out act, usedUp: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
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

        // Outside Bowl of Embers (Extreme) this is not a good BRD, but it must not be an INERT one:
        // CustomRotation.GeneralGCD is a hard stub and BardRotation does not override it, so
        // returning base here would leave the player auto-attacking with no error. Fall back to a
        // plain single-target filler instead.
        if (!IfritExBurst.InIfritEx)
        {
            return FallbackGCD(out act);
        }

        TargetType order = KillOrder;
        bool nails = NailsUp;

        // 1. Radiant Encore — 1,100 potency at 3 Coda, BRD's biggest single hit and its only shot
        //    at the documented "20-30% of HP in one blow" skip. Conditional, never on a nail.
        if (!nails && RadiantEncorePvE.CanUse(out act, skipComboCheck: true, skipTTKCheck: true,
                targetOverride: order))
        {
            if (ShouldFireEncore)
            {
                return true;
            }
        }

        // 2. The Barrage'd Refulgent Arrow: 3 x 280, second-biggest thing BRD can do, and it has
        //    to land inside Barrage's 10 s window. Never spent on a nail.
        if (!nails && HasBarrage && RefulgentArrowPvE.CanUse(out act, skipComboCheck: true,
                skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 3. Resonant Arrow, 640, gated behind Resonant Arrow Ready from Barrage. Never on a nail.
        if (!nails && ResonantArrowPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 4. DoTs. Caustic Bite FIRST (deliberately inverted from the raid opener): with the
        //    pre-pull buffs already running these snapshot fully buffed on GCD 1-2, and DoT ticks
        //    are the only meaningful Repertoire source. TargetStatusProvide stops them from being
        //    reapplied while they are still up, so this line doubles as 45 s maintenance.
        //    HARD RULE 1: never touch them while a nail is alive. Refreshing a DoT on Ifrit feeds
        //    the undocumented post-4.56 invulnerability budget, and the nails die in one or two
        //    GCDs so DoTing them is a straight loss.
        //    HARD RULE 2 (this is the fix, and it changes the opener): do not APPLY them during the
        //    opener window either. Applying a 45 s DoT at t~2 s of a kill the plan budgets at
        //    10-20 s delivers only ~4-6 ticks, so it is poor value even when the run goes well --
        //    and when the skip fails it is actively harmful, because those two DoTs keep ticking on
        //    Ifrit for up to 45 s after the nails spawn and CANNOT be cancelled. Retargeting cannot
        //    save BRD from that; only not applying them can. Once the opener window has elapsed the
        //    skip has demonstrably failed, the fight is the long nail path, and the DoTs are worth
        //    their GCDs again -- so they are applied then, between nail sets.
        if (!nails && !IfritExBurst.InOpenerWindowOf(OpenerWindow))
        {
            if (CausticBitePvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (StormbitePvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }
        }

        // 5. Blast Arrow, 700. Unreachable on the fast path (needs an 80+ gauge Apex Arrow); this
        //    line only ever fires on the long nail path. Taken if offered, never waited for.
        if (BlastArrowPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 6. Apex Arrow. Scales 140 at 20 gauge to 700 at 100; break-even against Burst Shot's 220
        //    is around 32, hence the configurable floor rather than the usual "wait for 100".
        if (SoulVoice >= ApexSoulVoice && ApexArrowPvE.CanUse(out act, skipAoeCheck: true,
                skipTTKCheck: true, targetOverride: order))
        {
            return true;
        }

        // 7. Refulgent Arrow on any Hawk's Eye proc (280 vs Burst Shot's 220) — never sit on a
        //    proc in a fight this short.
        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true, targetOverride: order))
        {
            return true;
        }

        // 8. Filler. BurstShotPvE's StatusProvide makes it stand down on its own while Hawk's Eye
        //    is up, so step 7 always wins the proc.
        if (BurstShotPvE.CanUse(out act, targetOverride: order))
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
        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true))
        {
            return true;
        }

        if (BurstShotPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
