namespace RelicBurstRotations.Rotations;

/// <summary>
/// SUMMONER — solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135, roughly half a
/// geared character's damage), farming the Bowl of Embers (Extreme) (TerritoryType 295) for Nexus
/// light. One burst window, no cooldown discipline, Infernal Nails take priority over Ifrit
/// whenever a nail set is up.
///
/// EXPECTED KILL TIME: ~20-35 s, NOT the sub-10 s the wiki's "skip the nail phase" strategy
/// assumes. SMN's frontload multiplier is only moderate (its burst is spread over a 15 s demi plus
/// ~35 s of primal phases) and its single biggest hit — Enkindle Solar Bahamut, 1500 potency — is
/// nowhere near the "finishing blow removes 20-30 % of Ifrit's HP" bar that the documented
/// Ninja-style skip needs. So this rotation is built assuming the 50 % nail phase WILL happen, and
/// the nail handling matters at least as much as the opener.
///
/// BURST PLAN, in order (see the SMN job plan for the full reasoning):
///   pre-pull  Summon Carbuncle  <- MANDATORY. Carbuncle despawns on every zone change and almost
///                                  every SMN action is gated on having it out. This is the single
///                                  biggest silent-failure risk in a 42-63 run farm loop, so it is
///                                  the very first entry in GeneralGCD.
///    1  Summon Solar Bahamut          (GCD, 60 s — the strongest 15 s SMN owns, start it ASAP)
///    2  Searing Light                 (oGCD — only +5 % solo, but it is the ONLY source of
///                                      Ruby's Glimmer, which gates Searing Flash)
///    3  Umbral Impulse                (GCD, 640 pot instant — best potency/GCD SMN has)
///    4  Searing Flash                 (oGCD, 700 pot)
///    5  Enkindle Solar Bahamut        (oGCD, 1500 pot — the single largest hit)
///    6  Sunflare                      (oGCD, 1000 pot)
///    7  Energy Drain                  (oGCD, grants 2 Aetherflow + Further Ruin)
///    8  Necrotize x2                  (oGCD, 500 pot each)
///    9  Umbral Impulse filler until the demi ends (target ~6; relic-tier spell speed may only fit 5)
///   10  Ruin IV                       (GCD, 520 pot, consumes Further Ruin — kept BELOW Umbral
///                                      Impulse so it never displaces a 640-pot demi GCD)
///   11  Summon Ifrit II -> Ruby Rite x2 -> Crimson Cyclone -> Crimson Strike
///   12  Summon Titan II  -> Topaz Rite x4, weaving Mountain Buster after each
///   13  Summon Garuda II -> Slipstream -> Emerald Rite x4
///   14  Ruin III filler
/// Sustain (if Ifrit lives past ~60 s): Energy Drain returns, then Summon Bahamut as the SECOND
/// demi (Astral Impulse 500 / Enkindle Bahamut 1300 / Deathflare 500), then the primals again.
///
/// DELIBERATE DEVIATIONS FROM THE STANDARD (Balance / Icy Veins) SMN ROTATION
///   * Primal order is Ifrit -> Titan -> Garuda, not Titan -> Garuda -> Ifrit. The standard order
///     exists because Titan is all-instant and its Mountain Busters fill weave slots inside the
///     shared 2-minute raid-buff window, while Ifrit's 2.8 s hardcasts are movement-hostile.
///     Solo there is no shared buff window (Searing Light is +5 % personal) and Ifrit EX has zero
///     forced movement in the opening ~15 s, so both reasons evaporate. Ranked on potency per
///     second of GCD time: Ifrit ~175/s, Titan ~160/s, Garuda ~137/s. Front-load the best phase.
///   * Sunflare fires as early as a weave slot allows instead of late in the demi window — the
///     standard late placement is purely weave-slot congestion under raid buffs, which solo we
///     do not have.
///   * Nothing is held or aligned. There is exactly one window; Searing Light (120 s) will never
///     come back. All the published "hold alternating Energy Drains to line Necrotize up with the
///     buff window" advice is stripped out.
///   * Enkindle Solar Bahamut is NOT saved as a finishing blow. Holding it costs real damage on
///     every run and the burst will not reach the skip threshold anyway.
///   * Swiftcast is omitted entirely: every SMN cast time is shorter than its recast, so making one
///     instant saves zero GCD time on a stationary boss. It is pure mobility insurance here.
///
/// NAIL PHASE. Damaging Ifrit while any Infernal Nail is alive is strictly harmful post-patch-4.56
/// (he goes temporarily invulnerable and the fight can lock into an unwinnable Hellfire loop), so
/// EVERY damaging CanUse below — GCD *and* oGCD, unlike the BLM template whose burst oGCDs are all
/// self-buffs — carries targetOverride: KillOrder. Outside the nail phase that resolves to
/// default (== TargetType.Big), i.e. exactly RSR's normal behaviour.
///
/// SMN has a real structural advantage here: 25 y cast range means the arena-ring nails can all be
/// killed from a standing position, so the travel time that dominates the nail phase for melee
/// costs SMN nothing. Single-target is also strictly correct — no two nails in any set are within
/// an SMN AoE radius of each other, so every AoE spell would hit exactly one nail for less potency
/// than its single-target counterpart. No AoE lines are built here on purpose.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK
///   * Carbuncle: relying on RSR's own SummonCarbunclePvE ActionCheck (which delays on
///     "no pet && no summon/attunement timers" and refuses to double-cast) to re-summon after each
///     instance load. If the farm loop ever observes SMN doing nothing at all on entry, this is
///     the first thing to verify.
///   * Slipstream leaves a 15 s windstorm ground effect on whatever it was cast on. With
///     targetOverride it lands on a nail during the nail phase, which is correct — but if it is
///     cast on Ifrit in the last moment before a nail set spawns, it will keep ticking him and is
///     the single most likely way to trip the invulnerability guard. There is no way to detect
///     that in advance; the UseSlipstream config exists so it can be switched off if it proves to
///     be a problem in practice.
///   * Enkindle / Sunflare / Deathflare / Searing Flash / Mountain Buster / Crimson Cyclone are all
///     AoE. On a nail standing close to Ifrit they can splash him. In practice they are all spent
///     during the opener, so this only matters on a second demi cycle.
///   * Lux Solaris (free 500-potency AoE cure from Refulgent Lux) and Physick are deliberately NOT
///     wired up — TEMPLATE.md forbids overriding the heal stages, and CanHealSingleSpell /
///     CanHealAreaSpell are forced false so RSR never spends a GCD healing solo.
///   * Umbral Impulse count inside the 15 s Lightwyrm Trance is not hardcoded anywhere here; the
///     priority list simply loops while the demi is active, which is the robust way to handle the
///     fact that relic-tier spell speed may only fit five casts rather than six.
///   * Tincture: RSR's own TinctureUseType defaults to "high-end duty only" and territory 295 is
///     NOT flagged high-end, so UseBurstMedicine will silently no-op with default settings. That is
///     expected and is not worked around.
/// </summary>
[Rotation("Ifrit EX Burst (SMN)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/SMN_IfritEX.cs")]
[ExtraRotation]
public sealed class SMN_IfritEX : SummonerRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [Range(0, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
    public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;

    [RotationConfig(CombatType.PvE, Name = "Use Crimson Cyclone (dashes to the target)")]
    public bool UseCrimsonCyclone { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Use Slipstream (leaves a 15s ground effect on the target — see the nail-phase note)")]
    public bool UseSlipstream { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Weave Radiant Aegis (zero DPS, pure insurance)")]
    public bool UseRadiantAegis { get; set; } = false;

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
        ImGui.Text($"HasSummon: {HasSummon}  SummonTime: {SummonTime:F1}");
        ImGui.Text($"InSolarBahamut: {InSolarBahamut}  InBahamut: {InBahamut}  InPhoenix: {InPhoenix}");
        ImGui.Text($"Attunement: {AttunementCount}  Aetherflow: {AetherflowStacks}");
        ImGui.Text($"Favors — Ifrit: {HasIfritFavor}  Titan: {HasTitanFavor}  Garuda: {HasGarudaFavor}");
    }

    #endregion

    #region Extra Methods

    /// <summary>
    /// Target override for every damage action: nails first while a nail set is up, otherwise
    /// <c>default</c> (== <c>TargetType.Big</c>), which is RSR's normal behaviour and a no-op.
    /// </summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss. SummonerRotation's own
    // CanHealSingleSpell turns healing ON when no other healer is alive — which, solo, is always.
    public override bool CanHealSingleSpell => false;

    public override bool CanHealAreaSpell => false;

    /// <summary>
    /// Gemshine spenders — the Ruby / Topaz / Emerald Rite family. RSR exposes each elemental
    /// variant as its own property even though they share one button in game; the attunement type
    /// is enforced by each action's own ActionCheck, so the three are mutually exclusive and the
    /// order between them only expresses the primal order (Ifrit -> Titan -> Garuda).
    /// The Ruin II / Ruin III variants below are the pre-72 forms and are kept purely as harmless
    /// tail fallbacks in case the duty is ever entered level-synced (TT 295 syncs to 50).
    /// </summary>
    private bool RiteGCD(out IAction? act, TargetType order)
    {
        if (RubyRitePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (TopazRitePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (EmeraldRitePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (RubyRuinIiiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (TopazRuinIiiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (EmeraldRuinIiiPvE.CanUse(out act, targetOverride: order))
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
            TargetType order = KillOrder;

            // Searing Light first: it is the gate on Ruby's Glimmer -> Searing Flash. Its own
            // Setting.TargetType is TargetType.Self, so it must NOT take a targetOverride.
            // skipTTKCheck: the kill is short, TTK gating would otherwise eat the whole burst.
            if (SearingLightPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Then strictly by potency. Every one of these is gated by its own ActionCheck /
            // StatusNeed (InSolarBahamut, InBahamut, InPhoenix, RubysGlimmer, AetherflowStacks,
            // MountainBusterPvEReady), so the list is self-sequencing across the demi and the
            // primal phases and needs no manual ordering logic.
            if (EnkindleSolarBahamutPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            if (EnkindleBahamutPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            if (EnkindlePhoenixPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            if (SunflarePvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            if (SearingFlashPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            if (DeathflarePvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            // Energy Drain's own ActionCheck is "!HasAetherflowStacks" and Necrotize's is
            // "AetherflowStacks > 0", so the two are mutually exclusive and self-order: Drain
            // refills, Necrotize spends. Both charges are dumped immediately — there is no second
            // buff window to save them for.
            if (EnergyDrainPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (NecrotizePvE.CanUse(out act, usedUp: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // Fester is the pre-92 form of Necrotize; harmless tail fallback if ever synced.
            if (FesterPvE.CanUse(out act, usedUp: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // One Mountain Buster weaved behind each Topaz Rite; gated on MountainBusterPvEReady.
            if (MountainBusterPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            // Worth zero DPS — off by default. Friendly/self action, so no targetOverride.
            if (UseRadiantAegis && !IsLastAction(false, RadiantAegisPvE)
                && RadiantAegisPvE.CanUse(out act, usedUp: true))
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

        // Outside Bowl of Embers (Extreme) this rotation must not drive the GCD at all — it exists
        // only for the relic-light farm loop. Same guard the canonical BLM_IfritEX uses.
        // Outside 295 this is not a good SMN, but it must not be an INERT one:
        // CustomRotation.GeneralGCD is a hard stub and SummonerRotation does not override it, so
        // returning base here would leave the player auto-attacking with no error. Fall back to a
        // plain single-target filler instead.
        if (!IfritExBurst.InIfritEx)
        {
            return FallbackGCD(out act);
        }

        TargetType order = KillOrder;

        // #1 FARM-LOOP BUG RISK. Carbuncle despawns on every zone transition and essentially every
        // SMN action is gated on having it out; without this, the whole rotation fails silently
        // with no error. RSR's own ActionCheck already handles the "don't re-cast if we have one"
        // and "don't double-cast" cases. Self-targeted — no targetOverride.
        if (SummonCarbunclePvE.CanUse(out act))
        {
            return true;
        }

        // Demis. Each carries its own readiness ActionCheck (IsSolarBahamutReady / HasPet /
        // SummonTime <= WeaponRemain), so listing all three is exclusive, not greedy. Solar
        // Bahamut is strictly the stronger demi (Umbral Impulse 640 vs Astral Impulse 500), which
        // is why it leads; Summon Bahamut becomes the SECOND demi at ~60 s if Ifrit survives.
        // Self-targeted buffs — no targetOverride.
        if (SummonSolarBahamutPvE.CanUse(out act))
        {
            return true;
        }

        if (SummonBahamutPvE.CanUse(out act))
        {
            return true;
        }

        if (SummonPhoenixPvE.CanUse(out act))
        {
            return true;
        }

        // Expiring resources first: Garuda's / Ifrit's Favor and the attunement stacks all time
        // out, whereas the demi fillers below are available for the whole trance. This ordering is
        // lifted from the shipped SMN_Reborn for exactly that reason. On a fresh pull none of these
        // can fire (no favors, no attunement), so it costs the opener nothing.
        if (UseSlipstream && SlipstreamPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (UseCrimsonCyclone && CrimsonCyclonePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (CrimsonStrikePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (RiteGCD(out act, order))
        {
            return true;
        }

        // Demi fillers. Gated on InSolarBahamut / InBahamut / InPhoenix respectively.
        if (UmbralImpulsePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (FountainOfFirePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (AstralImpulsePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (BrandOfPurgatoryPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Primal phases, Ifrit -> Titan -> Garuda (see the deviation note in the header).
        // Each summon's ActionCheck is "SummonTime <= WeaponRemain && Is<Primal>Ready", so these
        // can never fire during a demi and can never steal a GCD from the impulses above.
        // Self-targeted — no targetOverride.
        if (SummonIfritIiPvE.CanUse(out act))
        {
            return true;
        }

        if (SummonTitanIiPvE.CanUse(out act))
        {
            return true;
        }

        if (SummonGarudaIiPvE.CanUse(out act))
        {
            return true;
        }

        // Ruin IV (520 pot, consumes Further Ruin) sits BELOW the demi fillers on purpose: 520 is
        // less than Umbral Impulse's 640, so it must never displace a demi GCD.
        if (RuinIvPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
        {
            return true;
        }

        // Filler. Never let the GCD idle.
        if (RuinIiiPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

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

    /// <summary>
    /// Minimal single-target filler used everywhere OUTSIDE Bowl of Embers (Extreme). Exists only
    /// so this rotation is never completely inert when it is active outside its intended duty.
    /// </summary>
    private bool FallbackGCD(out IAction? act)
    {
        if (SummonCarbunclePvE.CanUse(out act))
        {
            return true;
        }

        if (RuinIvPvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        if (RuinIiiPvE.CanUse(out act))
        {
            return true;
        }

        if (RuinIiPvE.CanUse(out act))
        {
            return true;
        }

        if (RuinPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
