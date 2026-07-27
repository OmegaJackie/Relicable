namespace RelicBurstRotations.Rotations;

/// <summary>
/// CANONICAL TEMPLATE — see TEMPLATE.md.
///
/// SCENARIO (fixed, do not generalise): solo, unsynced, level 100, an ARR Zodiac relic weapon
/// equipped (iLvl 80-135, roughly half a current weapon's contribution), farming
/// the Bowl of Embers (Extreme) — TerritoryType 295 — for Nexus light. Epic Echo (+300%) applies,
/// so the expected kill is 10-20 seconds — about 5-11 GCDs. That is ONE burst window: every 60 s
/// and 120 s cooldown fires exactly once, nothing recharges mid-fight, and there is no second
/// two-minute window to align to. The rotation IS the opener.
///
/// EFFECTIVE SINGLE-TARGET POTENCY IN ASTRAL FIRE III (Astral Fire III multiplies *fire-aspected*
/// spells by 1.8x; Xenoglossy / Foul / Paradox / the Thunder line are UNASPECTED and get nothing):
///     Flare Star 500x1.8 = 900  >  Xenoglossy 890 (flat)  >  Despair 350x1.8 = 630
///     >  Fire IV 300x1.8 = 540  ==  Paradox 540 (flat)  >  Fire III 290x1.8 = 522
/// That ranking — not the standard rotation's MP/gauge bookkeeping — drives every ordering below.
///
/// PLAN, IN ORDER:
///   Pre-pull, out of combat, inside 295 only:
///     1. Transpose out of Astral Fire (you always finish a kill in fire) so Umbral Soul is usable.
///     2. Umbral Soul — out of combat this grants Umbral Ice III + 3 Umbral Hearts + 10,000 MP.
///        Doing this in Umbral Ice is what makes the pull's Fire III grant Astral Fire III,
///        Thunderhead, and the Paradox marker.
///     3. Swiftcast, only once Ifrit is actually in range and the gauge setup is done.
///        Fire III is the one spell 7.2 did NOT shorten (still 3.5 s), so it is a hard ~1 s clip on
///        the very first GCD. There is no /countdown when you solo-pull a duty, so this cannot live
///        in CountDownAction and there is no weave slot before the first in-combat GCD either —
///        it has to be pressed out of combat.
///   Pull:
///     4. Fire III (instant under Swiftcast) — the only way into Astral Fire III.
///     5. Ley Lines / Amplifier / Manafont / (optional) Triplecast woven as slots appear.
///     6. Xenoglossy dumps — 890 potency, instant, no MP, no setup. Deliberately spent early
///        rather than saved for movement (the standard reason to hold Polyglot); the fight may end
///        at GCD 6. One stack is held back for the execute — see below.
///     7. Fire IV x6 -> Flare Star -> Despair.
///     8. EXECUTE: the documented nail-phase skip needs the finishing blow to remove 20-30% of
///        Ifrit's HP in one hit. Below <see cref="ExecuteHpThreshold"/> the reserved Polyglot is
///        released and Flare Star / Xenoglossy jump the queue.
///   Sustain (only once the opener window has elapsed, i.e. the skip has already failed):
///     High Thunder, Paradox, and the Blizzard III -> Blizzard IV -> Fire III ice loop are unlocked.
///     Their break-even all sit past ~20 s, so they are pure loss inside the burst.
///
/// NAILS: while Infernal Nails are alive, damaging Ifrit is strictly harmful (post-4.56 he goes
/// temporarily invulnerable and the fight can lock into an unwinnable Hellfire loop). Every damage
/// action therefore takes <c>targetOverride: KillOrder</c>, which becomes TargetType.LowMaxHP while
/// nails are up. Single target only — the nails ring the arena, so AoE nets ~2 targets at best and
/// each nail dies to about one GCD anyway.
///
/// !! VERIFIED LIMITATION (RSR 7.5.1.17, ActionTargetInfo.FindTargetByType): <c>targetOverride</c> is
/// only consulted for FRIENDLY target selection. For a hostile action both the default and the
/// override switch fall through to <c>_ =&gt; FindHostile()</c>, and FindHostileRaw sorts purely by
/// <c>DataCenter.TargetingType</c> (the user's RSR "Targeting Type" list). So passing LowMaxHP here
/// is currently a NO-OP — it is retained because it is free, correct in intent, and would start
/// working if RSR ever wires the override into the hostile path. Until then, nail prioritisation has
/// to come from outside the rotation: set RSR's Targeting Type to "Low Max HP" for this farm, or push
/// the nail NameIds through RSR's <c>AddPriorityNameID</c> IPC from Relicable.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK:
///   * That the Astral Fire / Umbral Ice gauge survives leaving and re-entering the instance.
///     Steps 1-2 assume it does (that is also how Polyglot banks for free during load screens).
///     If it does not, the neutral-gauge branch in GeneralGCD still opens with Fire III and the only
///     loss is a little MP efficiency a 10-20 s fight will never feel.
///   * That RSR's tincture setting is not left on its default "high-end duty only" — territory 295
///     is NOT high-end, so UseBurstMedicine will silently no-op. Expected; not worked around.
///   * ExecuteHpThreshold is a guess at the skip window (the 4.56 invulnerability budget is
///     deliberately undocumented). 30% is the top of the documented 20-30% range.
/// </summary>
[Rotation("Ifrit EX Burst (BLM)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/BLM_IfritEX.cs")]
[ExtraRotation]
public sealed class BLM_IfritEX : BlackMageRotation
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
        Name = "Pre-pull gauge setup in Ifrit EX (Transpose into Umbral Ice, then Umbral Soul)")]
    public bool PrepullSetup { get; set; } = true;

    [RotationConfig(CombatType.PvE,
        Name = "Pre-pull Swiftcast so the opening Fire III is instant", Parent = nameof(PrepullSetup))]
    public bool PrepullSwiftcast { get; set; } = true;

    // DEFAULT OFF, deliberately. Holding a Polyglot can only lose time here:
    //   * the first Infernal Nail set spawns at 50% HP, but InExecuteWindow does not open until
    //     30%, so on any run where nails appear the reserve is never released as an execute at
    //     all -- ShouldKillNails flips InExecuteWindow false and the held Xenoglossy degrades to
    //     filler;
    //   * meanwhile 890 potency (about one of the ~5-11 GCDs available) is withheld across exactly
    //     the 100%->50% stretch that decides whether the nail phase happens in the first place;
    //   * and it buys nothing anyway -- Flare Star (900 effective) already fires unheld and is the
    //     larger hit, so the reserved Xenoglossy is never a killing blow Flare Star would not have
    //     been.
    // Left as a config so the behaviour is still reachable, but nothing should turn it on.
    [RotationConfig(CombatType.PvE,
        Name = "Hold one Polyglot back as the execute (finishing) hit -- costs time, see remarks")]
    public bool ReservePolyglot { get; set; } = false;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE,
        Name = "Execute below this target HP (release the reserved Polyglot, jump the queue)")]
    public float ExecuteHpThreshold { get; set; } = 0.30f;

    [RotationConfig(CombatType.PvE,
        Name = "Use High Thunder once the opener window has elapsed (the skip has failed)")]
    public bool SustainThunder { get; set; } = true;

    // Post-7.2 every spell left in this plan is either instant or a 2.0 s cast under a 2.5 s recast,
    // so Triplecast saves no time at all and is purely a weave-safety / anti-knockback tool.
    // Off by default: an extra weave can only clip.
    [RotationConfig(CombatType.PvE, Name = "Use Triplecast (damage-neutral post-7.2)")]
    public bool UseTriplecast { get; set; } = false;

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
        ImGui.Text($"InExecuteWindow: {InExecuteWindow}");
        ImGui.Text($"AF/UI: {AstralFireStacks}/{UmbralIceStacks}  Hearts: {UmbralHearts}");
        ImGui.Text($"Souls: {AstralSoulStacks}  Polyglot: {PolyglotStacks}  MP: {CurrentMp}");
    }

    #endregion

    #region Extra Methods

    /// <summary>
    /// Target override for every damage action: nails first while a nail set is up.
    /// See the class remarks — RSR 7.5.1.17 ignores <c>targetOverride</c> on the hostile path, so this
    /// is inert today and must be backed by RSR's Targeting Type / priority-NameId configuration.
    /// </summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    /// <summary>
    /// True once the current target is low enough that the next big hit should be the killing blow.
    /// Deliberately does NOT also require <c>IsBossFromIcon()</c>: if that check ever failed we would
    /// sit on the reserved Polyglot forever and throw away 890 potency, which is worse than the
    /// occasional early release. Nails are excluded because once nails are up the skip has failed
    /// and there is nothing left to execute.
    /// </summary>
    private bool InExecuteWindow
    {
        get
        {
            IBattleChara? target = HostileTarget;

            if (target is null || IfritExBurst.ShouldKillNails(target))
            {
                return false;
            }

            return target.GetHealthRatio() <= ExecuteHpThreshold;
        }
    }

    /// <summary>
    /// How many Polyglot stacks must be left untouched by the filler. One while we are saving the
    /// execute hit; zero once the target is in execute range or nails are up (at which point the
    /// skip has failed and Xenoglossy is simply the best thing to point at a nail).
    /// </summary>
    private int PolyglotFloor =>
        ReservePolyglot && !InExecuteWindow && !IfritExBurst.ShouldKillNails(HostileTarget) ? 1 : 0;

    /// <summary>
    /// True when <paramref name="nextGCD"/> is one of the spells in this plan that actually has a
    /// cast bar, i.e. the only case where Swiftcast buys any time at all. Compared by action id so
    /// it does not depend on reference identity of the IAction RSR hands back.
    /// </summary>
    private bool NextGcdIsHardcast(IAction? nextGCD)
    {
        if (nextGCD is null)
        {
            return false;
        }

        uint id = nextGCD.ID;

        // Fire III is instant under Firestarter, so Swiftcast would be thrown away on it.
        if (id == FireIiiPvE.ID)
        {
            return !HasFire;
        }

        return id == FireIiiPvE.ID
            || id == BlizzardIiiPvE.ID
            || id == FireIvPvE.ID
            || id == BlizzardIvPvE.ID
            || id == DespairPvE.ID
            || id == FlareStarPvE.ID
            || id == HighThunderPvE.ID;
    }

    // Solo in Ifrit EX: never spend a GCD healing, it is pure DPS loss. Scoped to territory 295 so
    // that outside the farm the base behaviour (CustomRotation returns true for both) is untouched.
    public override bool CanHealSingleSpell => !IfritExBurst.InIfritEx && base.CanHealSingleSpell;

    public override bool CanHealAreaSpell => !IfritExBurst.InIfritEx && base.CanHealAreaSpell;

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (IfritExBurst.InIfritEx)
        {
            // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
            if (PullTincture && IfritExBurst.InIfritOpener(OpenerWindow) && UseBurstMedicine(out act))
            {
                return true;
            }

            if (PrepullSetup && !InCombat)
            {
                // You always finish a kill in Astral Fire. Transpose flips to Umbral Ice, which is the
                // only state in which Umbral Soul is usable (RSR gates it on InUmbralIce).
                if (InAstralFire && TransposePvE.CanUse(out act, skipTTKCheck: true))
                {
                    return true;
                }

                // Ifrit inside casting range, so the 10 s Swiftcast buff is not burned during the
                // walk in.
                //
                // NOTE the gauge preconditions that used to be here (InUmbralIce && UmbralHearts >= 3
                // && CurrentMp >= 9000) are gone. Astral Fire / Umbral Ice decay ~15 s out of combat
                // and the farm loop is clear -> walk out -> re-queue -> loading screen, so the gauge
                // is NEUTRAL on every real zone-in: those conditions were unreachable, which meant
                // Swiftcast was never pressed on any pull at all. See the in-combat weave below for
                // the case where the pull happens before this ever gets a frame.
                if (PrepullSwiftcast
                    && HasHostilesInMaxRange
                    && SwiftcastPvE.CanUse(out act, skipTTKCheck: true))
                {
                    return true;
                }
            }

            // In-combat Swiftcast. The pre-pull branch above only fires if we get a frame out of
            // combat with Ifrit already in range, which AutoDuty's pull timing does not guarantee.
            // A 60 s cooldown that shaves ~1.5 s off a 10-20 s kill must never simply be skipped,
            // so weave it onto the next real hardcast. CanUse fails while it is already rolling, so
            // this cannot double-spend with the pre-pull press.
            if (InCombat && NextGcdIsHardcast(nextGCD)
                && SwiftcastPvE.CanUse(out act, skipTTKCheck: true))
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
            // skipTTKCheck everywhere: the kill is short, and RSR's time-to-kill gate would otherwise
            // reject exactly the cooldowns we want. Ley Lines is the worst offender — RSR ships it with
            // TimeToKill = 15, which a 10-20 s kill fails outright.
            //
            // DEVIATION FROM THE PLAN: the plan wanted Ley Lines pre-pull, placed toward the arena
            // centre so the whole nail ring stays in range. A rotation cannot control where the player
            // is standing, and pre-pull placement risks spending most of the 20 s duration walking, so
            // it fires on the pull instead. Cost is at most one GCD of uptime.
            if (LeyLinesPvE.CanUse(out act, usedUp: true, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // +1 Polyglot. RSR's own ActionCheck is
            // "(InAstralFire || InUmbralIce) && !EnochianEndAfter(10) && !IsPolyglotStacksMaxed",
            // so it already refuses to overcap and no extra guard is needed here.
            if (AmplifierPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Full MP + 3 Umbral Hearts + refreshed Astral Fire without the Umbral Ice detour.
            // skipStatusProvideCheck is MANDATORY: RSR declares Manafont's StatusProvide as
            // [Thunderhead], and this plan never spends Thunderhead inside the burst (High Thunder is
            // a loss under ~20 s), so Thunderhead is always still up and Manafont would never fire.
            if (CurrentMp < 1000
                && ManafontPvE.CanUse(out act, skipStatusProvideCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            if (UseTriplecast && TriplecastPvE.CanUse(out act, usedUp: true, skipTTKCheck: true))
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

        // Outside Bowl of Embers (Extreme) this is NOT a good BLM, but it must never be an INERT
        // one. CustomRotation.GeneralGCD is a hard stub (act = null; return false) and
        // BlackMageRotation does not override it, so returning base here would leave a player who
        // wandered out of the duty (or who simply picked this rotation in RSR's own dropdown)
        // standing still auto-attacking with no error and no obvious cause. Fall back to a plain
        // single-target filler instead: mediocre, but it presses buttons.
        if (!IfritExBurst.InIfritEx)
        {
            return FallbackGCD(out act);
        }

        // ---- Pre-pull gauge setup -------------------------------------------------------------
        if (!InCombat)
        {
            // Out of combat a single Umbral Soul grants Umbral Ice III + 3 Umbral Hearts + 10,000 MP.
            // The condition self-terminates once the gauge is full, so this cannot spin.
            if (PrepullSetup
                && InUmbralIce
                && (UmbralIceStacks < 3 || UmbralHearts < 3 || CurrentMp < 10000)
                && UmbralSoulPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            return base.GeneralGCD(out act);
        }

        TargetType order = KillOrder;
        bool nails = IfritExBurst.ShouldKillNails(HostileTarget);

        // "Sustain" == the opener window has elapsed, i.e. the sub-20 s skip has demonstrably failed
        // and the fight is now the 1.5-3 minute nail path. Only then do the long-payback tools earn
        // their GCD.
        bool sustain = !IfritExBurst.InOpenerWindowOf(OpenerWindow);
        int polyglotFloor = PolyglotFloor;

        // ---- Execute --------------------------------------------------------------------------
        // The documented skip wants the finishing blow to remove 20-30% of Ifrit's HP in one hit.
        // Flare Star first (900 effective) with Xenoglossy (890, instant, unconditional, no MP and no
        // resource check that can deny it) as the literal killing blow.
        if (InExecuteWindow)
        {
            if (FlareStarPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (XenoglossyPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }
        }

        // ---- Astral Fire phase ----------------------------------------------------------------
        if (InAstralFire)
        {
            // RSR's ActionCheck is AstralSoulStacks == 6, so no explicit gauge guard is needed.
            if (FlareStarPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            if (PolyglotStacks > polyglotFloor
                && XenoglossyPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // Despair converts the MP tail into damage at a better rate per GCD than another Fire IV,
            // but it grants no Astral Soul, so it must not steal MP from the six Fire IVs that unlock
            // Flare Star. Gating on "cannot afford two more Fire IV" keeps it as the phase terminator.
            if (CurrentMp < 1600 && DespairPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // The filler and the only Astral Soul generator in this plan.
            // RSR's ActionCheck is InAstralFire && AstralSoulStacks <= 5.
            if (FireIvPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // High Thunder is 150 direct + 60 per 3 s tick, all unaspected. Break-even against one
            // Fire IV (540 effective) needs ~19.5 s of remaining fight, so it is a loss inside the
            // burst and only earns its GCD on the failed-skip path. Never on a nail — nails die to
            // about one GCD, so the DoT would never tick.
            if (sustain && SustainThunder && !nails
                && HighThunderPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // Paradox is UNASPECTED 540 — exactly equal to a Fire IV — while costing 1600 MP instead
            // of 800 and granting zero Astral Soul, so Fire IV strictly dominates it inside the burst.
            // It is only worth a GCD in the sustain loop, where MP genuinely is the constraint and the
            // Firestarter proc has time to be spent.
            if (sustain && ParadoxPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // Free and instant under Firestarter (RSR sets MPOverride to 0 when HasFire), otherwise a
            // 2000 MP hardcast. Below Fire IV on effective potency, so it sits here as the proc dump.
            if (FireIiiPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // Everything above failed, which in practice means MP is exhausted and Manafont is down.
            // Blizzard III costs nothing and starts the refill. Intentionally NOT gated on `sustain`:
            // stalling with no castable GCD would be strictly worse than a slightly early ice phase.
            if (BlizzardIiiPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }
        }
        // ---- Umbral Ice recovery (failed-skip path only; the burst never enters ice) -----------
        else if (InUmbralIce)
        {
            // Free and instant in Umbral Ice.
            if (ParadoxPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // RSR's ActionCheck is InUmbralIce && UmbralHearts < 3.
            if (BlizzardIvPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            // Unaspected, so it is unaffected by the Umbral Ice damage penalty — the best filler while
            // the MP refills.
            if (PolyglotStacks > polyglotFloor
                && XenoglossyPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // Back to Astral Fire III as soon as it is affordable.
            if (FireIiiPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (BlizzardIiiPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }
        }
        // ---- Neutral gauge --------------------------------------------------------------------
        // Reached when the pre-pull setup did not run (config off, or the gauge did not survive the
        // instance transition). Fire III still grants Astral Fire III from neutral, so the pull is
        // unchanged; only the Umbral Hearts and the Paradox marker are lost.
        else
        {
            if (FireIiiPvE.CanUse(out act, targetOverride: order))
            {
                return true;
            }

            if (PolyglotStacks > polyglotFloor
                && XenoglossyPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }
        }

        return base.GeneralGCD(out act);
    }

    /// <summary>
    /// Minimal single-target filler used everywhere OUTSIDE Bowl of Embers (Extreme). Deliberately
    /// dumb — no Polyglot reserve, no sustain logic, no cooldowns — its only job is to stop this
    /// rotation from being completely inert when it is active outside its intended duty.
    /// </summary>
    private bool FallbackGCD(out IAction? act)
    {
        if (InAstralFire)
        {
            if (FlareStarPvE.CanUse(out act, skipAoeCheck: true))
            {
                return true;
            }

            if (XenoglossyPvE.CanUse(out act))
            {
                return true;
            }

            if (CurrentMp < 1600 && DespairPvE.CanUse(out act))
            {
                return true;
            }

            if (FireIvPvE.CanUse(out act))
            {
                return true;
            }

            if (ParadoxPvE.CanUse(out act))
            {
                return true;
            }

            if (FireIiiPvE.CanUse(out act))
            {
                return true;
            }

            if (BlizzardIiiPvE.CanUse(out act))
            {
                return true;
            }
        }
        else if (InUmbralIce)
        {
            if (ParadoxPvE.CanUse(out act))
            {
                return true;
            }

            if (BlizzardIvPvE.CanUse(out act))
            {
                return true;
            }

            if (XenoglossyPvE.CanUse(out act))
            {
                return true;
            }

            if (FireIiiPvE.CanUse(out act))
            {
                return true;
            }
        }
        else
        {
            if (FireIiiPvE.CanUse(out act))
            {
                return true;
            }

            if (XenoglossyPvE.CanUse(out act))
            {
                return true;
            }

            if (BlizzardIiiPvE.CanUse(out act))
            {
                return true;
            }
        }

        return base.GeneralGCD(out act);
    }

    #endregion
}
