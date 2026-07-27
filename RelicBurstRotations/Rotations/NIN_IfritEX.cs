namespace RelicBurstRotations.Rotations;

/// <summary>
/// NINJA — solo, unsynced, level 100, ARR relic weapon equipped (iLvl 80-135, i.e. roughly half a
/// geared level-100 character's damage), farming the Bowl of Embers (Extreme) — TerritoryType 295 —
/// for Nexus light. Epic Echo (+300%) applies, so the expected pull-to-death time is 12-25 s, about
/// 8-12 GCDs plus the ~3 s Ten Chi Jin block.
///
/// DESIGN CONSEQUENCE: the opener IS the rotation. Every 60/90/120 s cooldown fires exactly once on
/// the success path, so there is deliberately NO hold / drift / cooldown-alignment / IsBurst logic
/// anywhere in this file. Everything goes the moment it is legal, with skipTTKCheck on the burst
/// oGCDs because RSR's time-to-kill gate would otherwise reject exactly the cooldowns we want.
///
/// PLANNED BURST ORDER (what this file tries to produce; RSR re-derives it every frame from state,
/// it is not a scripted step table):
///   pre-pull  Hide -> Ten/Chi/Jin -> Suiton (grants Shadow Walker; hard prerequisite for Kunai's
///             Bane) -> Kassatsu
///   GCD 1     Spinning Edge          + Dokumori          (40 Ninki, damage-taken-up, grants Higi)
///   GCD 2     Gust Slash             + Bunshin           (spends the 50 Ninki)
///   GCD 3     Phantom Kamaitachi     (700, ranged, does not break the melee combo)
///   GCD 4     Armor Crush            + Kunai's Bane      (LATE weave — puts the 15 s window over
///                                                         Hyosho / TCJ / Tenri Jindo)
///   GCD 5     Hyosho Ranryu (Kassatsu, 1300 — the biggest single hit and the best candidate for
///                            the documented "finishing blow worth 20-30% of Ifrit's HP" skip)
///                                    + Dream Within a Dream
///   GCD 6     Raiton                 + Ten Chi Jin
///   GCD 7-9   TCJ: Fuma Shuriken -> Raiton -> Suiton     (no weave slots exist during TCJ)
///                                    + Meisui (needs the TCJ Suiton's Shadow Walker)
///   GCD 10    Fleeting Raiju         + Bhavacakra (auto-upgrades to Zesho Meppo) + Tenri Jindo
///   then      Fleeting Raiju / Raiton / melee combo until it dies.
///
/// NAIL PHASE (failed skip): while Infernal Nails are alive, damaging Ifrit is STRICTLY HARMFUL —
/// since patch 4.56 he goes temporarily invulnerable and the fight can lock into an unwinnable
/// Hellfire loop. <see cref="IfritExBurst.NailFirstTargeting"/> retargets every damage action onto
/// the nails (TargetType.LowMaxHP) for us. Ninja's single-target ninjutsu are ranged (Raiton /
/// Suiton 20 y, Fuma Shuriken 25 y, Phantom Kamaitachi 20 y, Throwing Dagger 20 y), so nails around
/// the arena ring get sniped without ever repositioning — which is why there is no AoE plan here.
/// Kunai's Bane and Dokumori are additionally held while nails are up so their long cooldowns are
/// not spent on an add that dies to one GCD.
///
/// ASSUMPTIONS A REVIEWER SHOULD CHECK:
///  * The mudra state machine is a trimmed copy of RSR's shipped NIN_Reborn (single target only, no
///    Doton / Huton / Katon / Goka Mekkyaku). A mis-sequenced mudra produces Rabbit Medium and
///    wastes a GCD; the Rabbit Medium recovery branch at the top of GeneralGCD is the safety net.
///  * NIN_Reborn drives its mudras from EmergencyGCD. TEMPLATE.md restricts this file to four
///    overrides, so the machine lives at the top of GeneralGCD instead. That is only safe because
///    solo we never take the heal/defense GCD stages (CanHealSingleSpell/CanHealAreaSpell are
///    false and NIN has no defensive GCD), so GeneralGCD is always reached.
///  * Ten Chi Jin is gated on InTrickAttack so it lands inside the Kunai's Bane window, with a
///    12 s escape hatch in case the pre-pull Suiton was missed and Kunai's Bane never fired.
///  * Kassatsu is held (mudras suppressed) until Kunai's Bane has actually gone out, so Hyosho
///    Ranryu benefits from the debuff. Escape hatch: 15 s.
///  * RSR's tincture setting defaults to "high-end duty only" and territory 295 is NOT high-end, so
///    UseBurstMedicine will often silently no-op. Expected; not worked around.
/// </summary>
[Rotation("Ifrit EX Burst (NIN)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/NIN_IfritEX.cs")]
[ExtraRotation]
public sealed class NIN_IfritEX : NinjaRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
    public bool NailPriority { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use a tincture on the pull")]
    public bool PullTincture { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Hide out of combat (resets the mudra cooldown before the pull)")]
    public bool UseHide { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Pre-charge Suiton out of combat in Ifrit EX (needed for Kunai's Bane)")]
    public bool PrepSuiton { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Forked Raiju instead of Fleeting Raiju when out of melee range (dashes to the target)")]
    public bool UseForkedRaiju { get; set; } = false;

    [Range(0, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
    public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;

    #endregion

    #region Tracking Properties

    /// <summary>
    /// The ninjutsu we are currently spelling out. Null means "press weaponskills instead".
    /// Plain field, never touched by a field initializer that reads game state — RSR instantiates
    /// rotations at load time, off the game thread.
    /// </summary>
    private IBaseAction? _ninAim;

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"InIfritEx: {IfritExBurst.InIfritEx}");
        ImGui.Text($"InBurst: {IfritExBurst.InBurst}");
        ImGui.Text($"CombatSeconds: {IfritExBurst.CombatSeconds:F1}");
        ImGui.Text($"InOpenerWindow: {IfritExBurst.InOpenerWindowOf(OpenerWindow)}");
        ImGui.Text($"ShouldKillNails: {IfritExBurst.ShouldKillNails(HostileTarget)}");
        ImGui.Text($"HostilesInMaxRange: {NumberOfAllHostilesInMaxRange}");
        ImGui.Text($"NinAim: {(_ninAim == null ? "none" : _ninAim.Name)}");
        ImGui.Text($"NinjutsuIdle: {NinjutsuIdle}  IsExecutingMudra: {IsExecutingMudra}");
        ImGui.Text($"Ninki: {Ninki}  Kazematoi: {Kazematoi}  RaijuStacks: {RaijuStacks}");
        ImGui.Text($"HasKassatsu: {HasKassatsu}  IsShadowWalking: {IsShadowWalking}  HasTenChiJin: {HasTenChiJin}");
        ImGui.Text($"InTrickAttack: {InTrickAttack}  ShadowWalkerNeeded: {ShadowWalkerNeeded}");
        ImGui.Text($"HoldKassatsu: {HoldKassatsu}");
    }

    #endregion

    #region Extra Methods

    /// <summary>Target override for every damage action: nails first while a nail set is up.</summary>
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;

    // Solo: never spend a GCD or an oGCD healing, it is pure DPS loss.
    public override bool CanHealSingleSpell => false;

    public override bool CanHealAreaSpell => false;

    /// <summary>
    /// True when no ninjutsu is currently charged, i.e. the Ninjutsu button is still un-adjusted.
    /// Reconstructed from the job base's public <c>*PvEReady</c> flags (each of which is
    /// <c>GetAdjustedActionId(NinjutsuPvE) == &lt;that action&gt;</c>) because the equivalent
    /// "NoActiveNinjutsu" helper NIN_Reborn uses is private to that file.
    /// </summary>
    private static bool NinjutsuIdle =>
        !RabbitMediumPvEActive
        && !FumaShurikenPvEReady && !KatonPvEReady && !RaitonPvEReady && !HyotonPvEReady
        && !HutonPvEReady && !DotonPvEReady && !SuitonPvEReady
        && !GokaMekkyakuPvEReady && !HyoshoRanryuPvEReady;

    /// <summary>
    /// Hold the Kassatsu -&gt; Hyosho Ranryu mudras until Kunai's Bane has actually gone out, so the
    /// 1300-potency hit lands inside the damage-taken-up window. Escape hatch at 15 s in case the
    /// pre-pull Suiton was missed and Kunai's Bane can never fire.
    /// </summary>
    private bool HoldKassatsu =>
        HasKassatsu
        && !IsExecutingMudra
        && !KunaisBanePvE.Cooldown.IsCoolingDown
        && CombatElapsedLess(15);

    /// <summary>
    /// While a nail set is up, Kunai's Bane (60 s) and Dokumori (120 s) would be spent on an add
    /// that dies to a single GCD. Hold them for Ifrit instead.
    /// </summary>
    private bool DebuffsWouldBeWasted =>
        NailPriority && IfritExBurst.ShouldKillNails(HostileTarget);

    /// <summary>
    /// Mudras may only be spelled out in combat, or out of combat in Ifrit EX when pre-charging the
    /// opener Suiton (there is no /countdown when you solo-pull a duty, so CountDownAction never
    /// runs and the pre-pull setup has to happen here).
    /// </summary>
    private bool MudrasAllowed =>
        HasHostilesInMaxRange
        && (InCombat || (PrepSuiton && IfritExBurst.InIfritEx));

    private void SetNinjutsu(IBaseAction act)
    {
        if (RabbitMediumPvEActive)
        {
            return;
        }

        // Never re-aim mid-sequence: the mudras already pressed decide what comes out.
        if (_ninAim != null && !NinjutsuIdle)
        {
            return;
        }

        _ninAim = act;
    }

    private void ClearNinjutsu()
    {
        _ninAim = null;
    }

    /// <summary>
    /// Decide which ninjutsu to spell out next. Single target only — the nails are spread around the
    /// arena ring, so Katon / Goka Mekkyaku / Doton / Huton are never worth a press here.
    /// </summary>
    private void ChoiceNinjutsu()
    {
        if (!MudrasAllowed || HasTenChiJin || RabbitMediumPvEActive)
        {
            return;
        }

        if (HasKassatsu)
        {
            // Kassatsu is only ever cashed into Hyosho Ranryu (1300). HoldKassatsu keeps the mudras
            // suppressed until Kunai's Bane is out; the aim itself is set either way so that the
            // sequence starts on the very first legal frame.
            if (HyoshoRanryuPvE.EnoughLevel && HyoshoRanryuPvE.IsEnabled && !IsLastGCD(false, HyoshoRanryuPvE))
            {
                SetNinjutsu(HyoshoRanryuPvE);
            }

            return;
        }

        if (_ninAim != null || !NinjutsuIdle)
        {
            return;
        }

        // usedUp: true — burn both mudra charges. There is exactly one burst window; nothing is
        // gained by banking a charge for a second one that will never happen.
        if (!TenPvE.CanUse(out _, usedUp: true))
        {
            return;
        }

        // Shadow Walker is the hard prerequisite for Kunai's Bane
        // (ModifyKunaisBanePvE.ActionCheck = (IsHidden || IsShadowWalking) && !HasTenChiJin),
        // so Suiton outranks everything whenever Kunai's Bane is about to be available.
        // NOTE: deliberately NOT gated on !IsHidden. Hide also satisfies Kunai's Bane, but Hidden is
        // lost the instant we attack, so the pre-pull Suiton is still required.
        if (ShadowWalkerNeeded && !IsShadowWalking
            && SuitonPvE.EnoughLevel && SuitonPvE.IsEnabled && JinPvE.Info.IsQuestUnlocked())
        {
            SetNinjutsu(SuitonPvE);
            return;
        }

        // Raiton (740) is the bread-and-butter GCD and refills Raiju Ready; swap to Fuma Shuriken
        // once Raiju is capped at 3 stacks so the buff is not wasted.
        if (RaitonPvE.EnoughLevel && RaitonPvE.IsEnabled && ChiPvE.Info.IsQuestUnlocked()
            && RaijuStacks < 3)
        {
            SetNinjutsu(RaitonPvE);
            return;
        }

        if (FumaShurikenPvE.EnoughLevel && FumaShurikenPvE.IsEnabled && TenPvE.Info.IsQuestUnlocked())
        {
            SetNinjutsu(FumaShurikenPvE);
        }
    }

    /// <summary>Reset the aim once the ninjutsu has resolved, or once its precondition evaporated.</summary>
    private void MaintainNinjutsu()
    {
        if (!MudrasAllowed)
        {
            ClearNinjutsu();
            return;
        }

        if (IsLastAction(false, FumaShurikenPvE, KatonPvE, RaitonPvE, HyotonPvE, DotonPvE, SuitonPvE)
            || (_ninAim == SuitonPvE && IsShadowWalking)
            || (_ninAim == HyoshoRanryuPvE && IsLastGCD(false, HyoshoRanryuPvE))
            || (_ninAim == HyoshoRanryuPvE && !HasKassatsu))
        {
            ClearNinjutsu();
        }
    }

    /// <summary>
    /// The Ten Chi Jin block: three instant ninjutsu at ~1 s each, entirely inside the Kunai's Bane
    /// window. Single-target route only — Fuma Shuriken -&gt; Raiton -&gt; Suiton. The TCJ Suiton is
    /// what re-grants Shadow Walker for the Meisui weave immediately afterwards.
    /// Mudra buttons are replaced during TCJ, hence the _188xx variants and the AdjustId probes
    /// (copied verbatim from RSR's shipped NIN_Reborn.DoTenChiJin).
    /// </summary>
    private bool DoTenChiJin(out IAction? act)
    {
        act = null;

        if (!HasTenChiJin)
        {
            return false;
        }

        uint tenId = AdjustId(TenPvE.ID);
        uint chiId = AdjustId(ChiPvE.ID);
        uint jinId = AdjustId(JinPvE.ID);

        if (tenId == FumaShurikenPvE_18873.ID
            && !IsLastAction(false, FumaShurikenPvE_18875, FumaShurikenPvE_18873)
            && FumaShurikenPvE_18873.CanUse(out act))
        {
            return true;
        }

        if (chiId == RaitonPvE_18877.ID
            && !IsLastAction(false, RaitonPvE_18877)
            && RaitonPvE_18877.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        if (jinId == SuitonPvE_18881.ID
            && !IsLastAction(false, SuitonPvE_18881)
            && SuitonPvE_18881.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Spell out whatever <see cref="_ninAim"/> currently is. Each branch is
    /// "resolved ninjutsu is loaded -> fire it", otherwise "press the next mudra".
    /// </summary>
    private bool DoNinjutsu(out IAction? act, TargetType order)
    {
        act = null;

        if (HasTenChiJin)
        {
            return false;
        }

        // No aim, but something is charged anyway (stray press / aim cleared mid-sequence): flush it.
        if (_ninAim == null)
        {
            return DischargeStrayNinjutsu(out act, order);
        }

        // Kassatsu -> Hyosho Ranryu. RSR declares this sequence as [ChiPvE_18806, JinPvE_18807].
        if (_ninAim == HyoshoRanryuPvE)
        {
            if (HoldKassatsu)
            {
                return false;
            }

            if (RabbitMediumPvEActive)
            {
                ClearNinjutsu();
                return false;
            }

            if (HyoshoRanryuPvEReady)
            {
                return HyoshoRanryuPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order);
            }

            if (FumaShurikenPvEReady)
            {
                return JinPvE_18807.CanUse(out act, usedUp: true);
            }

            if (NinjutsuIdle)
            {
                return ChiPvE_18806.CanUse(out act, usedUp: true);
            }

            return false;
        }

        if (RabbitMediumPvEActive)
        {
            ClearNinjutsu();
            return false;
        }

        // Suiton: Ten -> Chi -> Jin.
        if (_ninAim == SuitonPvE)
        {
            if (SuitonPvEReady)
            {
                return SuitonPvE.CanUse(out act, targetOverride: order);
            }

            if (RaitonPvEReady)
            {
                return JinPvE_18807.CanUse(out act, usedUp: true);
            }

            if (FumaShurikenPvEReady)
            {
                return ChiPvE_18806.CanUse(out act, usedUp: true);
            }

            if (NinjutsuIdle)
            {
                return TenPvE.CanUse(out act, usedUp: true);
            }

            return false;
        }

        // Raiton: Ten -> Chi.
        if (_ninAim == RaitonPvE)
        {
            if (RaitonPvEReady)
            {
                return RaitonPvE.CanUse(out act, targetOverride: order);
            }

            if (FumaShurikenPvEReady)
            {
                return ChiPvE_18806.CanUse(out act, usedUp: true);
            }

            if (NinjutsuIdle)
            {
                return TenPvE.CanUse(out act, usedUp: true);
            }

            return false;
        }

        // Fuma Shuriken: Ten.
        if (_ninAim == FumaShurikenPvE)
        {
            if (FumaShurikenPvEReady)
            {
                return FumaShurikenPvE.CanUse(out act, targetOverride: order);
            }

            if (NinjutsuIdle)
            {
                return TenPvE.CanUse(out act, usedUp: true);
            }
        }

        return DischargeStrayNinjutsu(out act, order);
    }

    /// <summary>
    /// Deadlock guard. If a ninjutsu ends up charged that is NOT the one we aimed at — a stray
    /// manual mudra press, an aim change mid-sequence, a user hotbar press — the aim-driven branches
    /// above all decline and nothing ever clears the charged mudra, because ChoiceNinjutsu only
    /// re-aims while <see cref="NinjutsuIdle"/>. Fire whatever is loaded and reset the aim.
    /// </summary>
    private bool DischargeStrayNinjutsu(out IAction? act, TargetType order)
    {
        act = null;

        if (NinjutsuIdle || RabbitMediumPvEActive || HasTenChiJin)
        {
            return false;
        }

        bool fired =
            (HyoshoRanryuPvEReady && HyoshoRanryuPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
            || (GokaMekkyakuPvEReady && GokaMekkyakuPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
            || (SuitonPvEReady && SuitonPvE.CanUse(out act, targetOverride: order))
            || (RaitonPvEReady && RaitonPvE.CanUse(out act, targetOverride: order))
            || (KatonPvEReady && KatonPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
            || (HyotonPvEReady && HyotonPvE.CanUse(out act, targetOverride: order))
            // Doton is a self-centred ground AoE (Setting.TargetType == Self), so no targetOverride.
            || (DotonPvEReady && DotonPvE.CanUse(out act, skipAoeCheck: true))
            || (HutonPvEReady && HutonPvE.CanUse(out act, skipAoeCheck: true))
            || (FumaShurikenPvEReady && FumaShurikenPvE.CanUse(out act, targetOverride: order));

        if (fired)
        {
            ClearNinjutsu();
            return true;
        }

        return false;
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (IfritExBurst.InBurst)
        {
            // Solo duty entry has no countdown, so CountDownAction never runs; the tincture goes here.
            if (PullTincture && IfritExBurst.InIfritOpener(OpenerWindow) && UseBurstMedicine(out act))
            {
                return true;
            }

            // Kassatsu, if it was not pre-applied out of combat. Never press it mid-mudra: it would
            // change which ninjutsu the already-queued mudras resolve into.
            // The mudra buttons have two ids each (the bare one and the _188xx "second/third press"
            // variant this file actually presses), so all six have to be in the guard list.
            if (NinjutsuIdle && !IsExecutingMudra
                && !nextGCD.IsTheSameTo(false,
                    ActionID.TenPvE, ActionID.TenPvE_18805,
                    ActionID.ChiPvE, ActionID.ChiPvE_18806,
                    ActionID.JinPvE, ActionID.JinPvE_18807)
                && KassatsuPvE.CanUse(out act, skipTTKCheck: true))
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

        if (IfritExBurst.InBurst && !HasTenChiJin)
        {
            TargetType order = KillOrder;

            // Dokumori first: 40 Ninki is what funds Bunshin two GCDs later, and it grants Higi so
            // the Ninki spender upgrades to Zesho Meppo.
            // NOTE: ModifyDokumoriPvE.ActionCheck is "Ninki <= 60 && IsLongerThan(10)". skipTTKCheck
            // does NOT bypass an ActionCheck, so on a very fast kill this can legitimately refuse —
            // that is why it sits first in the chain rather than being held for alignment.
            if (!DebuffsWouldBeWasted && DokumoriPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
            {
                return true;
            }

            // Kunai's Bane: 700 potency + the 15 s damage-taken-up window. Hard-gated by RSR on
            // Shadow Walker or Hidden, which is what the pre-pull / TCJ Suiton exists to provide.
            if (!DebuffsWouldBeWasted
                && KunaisBanePvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    skipStatusProvideCheck: IsShadowWalking))
            {
                return true;
            }

            if (BunshinPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Ten Chi Jin wants to land inside the Kunai's Bane window (RSR blocks it while Kassatsu
            // is up, which already forces it after Hyosho Ranryu). The CombatElapsedLess escape hatch
            // covers the case where the pre-pull Suiton was missed so Kunai's Bane never fired.
            if ((InTrickAttack || !CombatElapsedLess(12))
                && TenChiJinPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Meisui: consumes the TCJ Suiton's Shadow Walker, refunds 50 Ninki and buffs the next
            // Ninki spender (Zesho Meppo 700 -> 850).
            // HARD GATE on Kunai's Bane already being spent: Meisui's only requirement is
            // StatusNeed = [ShadowWalker], so without this it would happily eat the pre-pull Suiton's
            // Shadow Walker — the one thing Kunai's Bane cannot fire without.
            if ((!KunaisBanePvE.EnoughLevel || KunaisBanePvE.Cooldown.IsCoolingDown)
                && MeisuiPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }

            // Tenri Jindo (1100) — the Ten Chi Jin follow-up, second biggest hit in the plan.
            if (TenriJindoPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true,
                    targetOverride: order))
            {
                return true;
            }

            if (DreamWithinADreamPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
            {
                return true;
            }

            // Ninki spender. DELIBERATELY BhavacakraPvE, never ZeshoMeppoPvE: RSR's
            // ModifyZeshoMeppoPvE.ActionCheck is "Ninki <= 50 && ZeshoMeppoPvEReady", so pressing
            // Zesho Meppo directly silently refuses above 50 Ninki. The game's AdjustId upgrades
            // Bhavacakra to Zesho Meppo on its own while Higi (from Dokumori) is active.
            // Single target only — Hellfrog / Deathfrog Medium (250 / 400) lose badly to
            // Bhavacakra / Zesho Meppo (400 / 700-850) on a ring of spread-out nails.
            if (BhavacakraPvE.CanUse(out act, skipTTKCheck: true, targetOverride: order))
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
        TargetType order = KillOrder;

        // --- Hide, BEFORE anything touches the mudra machine -------------------------------------
        // This used to live at the bottom of the method behind
        //     UseHide && !InCombat && _ninAim == null && TenPvE.Cooldown.IsCoolingDown
        // which can never be satisfied: out of combat in 295, MudrasAllowed is already true, so
        // ChoiceNinjutsu aims Suiton on the first frame Ifrit is within 25 y -- i.e. before any
        // mudra has been pressed, so TenPvE is not yet cooling down. From that frame on _ninAim is
        // non-null, and by the time Ten IS cooling down the Suiton has resolved, which starts
        // combat. The two halves of the guard were mutually exclusive and Hide never fired once.
        //
        // Pressing it here, above the state machine, is the whole point: Hide resets the Ten/Chi/Jin
        // cooldown and grants Hidden (which satisfies Kunai's Bane), so the pre-pull Suiton no
        // longer has to be paid for out of the charges the in-combat Raiton and Kassatsu ->
        // Hyosho Ranryu sequence need. IsHidden stops it from toggling itself back off.
        if (UseHide && !InCombat && IfritExBurst.InIfritEx && !IsHidden
            && _ninAim == null && NinjutsuIdle
            && HidePvE.CanUse(out act))
        {
            return true;
        }

        // --- mudra state machine bookkeeping (runs every frame) ---
        // Deliberately ABOVE the nail safety gate: the bookkeeping is what clears a stale aim and
        // a half-spelled mudra. Skipping it while we hold fire would deadlock the ninjutsu machine
        // for the whole nail phase.
        MaintainNinjutsu();
        ChoiceNinjutsu();

        // A mis-sequenced mudra leaves Rabbit Medium loaded; clear it before anything else or the
        // whole ninjutsu chain deadlocks.
        if (RabbitMediumPvEActive)
        {
            if (RabbitMediumPvE.CanUse(out act))
            {
                ClearNinjutsu();
                return true;
            }

            ClearNinjutsu();
        }

        // ---- NAIL SAFETY GATE ----------------------------------------------------------------
        // A nail set is up but the target RSR resolved is still Ifrit, who is invulnerable until
        // every nail dies. Press nothing: damage aimed at him is wasted, it feeds the post-4.56
        // invulnerability budget, and only nail kills can advance the fight. Relicable pins RSR's
        // DataCenter.TargetingTypeOverride to LowMaxHP for the whole of territory 295, so the
        // resolved target swings onto a nail within a frame or two and the rotation resumes there.
        // (The old mechanism -- IfritExBurst.NailFirstTargeting fed to CanUse's targetOverride --
        // provably cannot do this: RSR's hostile picker never reads targetOverride. See
        // IfritExBurst.NailFirstTargeting.)
        // The aim is deliberately left alone rather than cleared: MaintainNinjutsu above already
        // times out a stale sequence, and tearing the aim down every frame would make the mudra
        // machine unable to re-form once the nails are gone.
        if (IfritExBurst.MustHoldFire(HostileTarget))
        {
            act = null;
            return false;
        }

        // Ten Chi Jin owns the GCD completely while it is up — no weave slots exist there either.
        if (DoTenChiJin(out act))
        {
            return true;
        }

        if (DoNinjutsu(out act, order))
        {
            return true;
        }

        // Everything below is a plain weaponskill; never interleave one into a half-spelled mudra.
        if (IsExecutingMudra)
        {
            return base.GeneralGCD(out act);
        }

        // Phantom Kamaitachi: 700 potency at 20 y, granted by Bunshin, does not break the combo.
        if (PhantomKamaitachiPvE.CanUse(out act, skipAoeCheck: true, targetOverride: order))
        {
            return true;
        }

        // Raiju Ready (700). Fleeting is the melee version; Forked is the 20 y version but dashes
        // to the target, so it is opt-in.
        if (FleetingRaijuPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (UseForkedRaiju && ForkedRaijuPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Melee combo. No positional gating at all: Ifrit turns to face a solo player, and the fight
        // brief is explicit that repositioning costs more time than the positional bonus is worth.
        // Armor Crush at 0 Kazematoi is still the stronger finisher than an unbuffed Aeolian Edge.
        if (Kazematoi == 0 && ArmorCrushPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (Kazematoi > 0 && AeolianEdgePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (ArmorCrushPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (GustSlashPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        if (SpinningEdgePvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // Ranged filler (20 y) — keeps uptime while walking between nails around the arena ring.
        if (ThrowingDaggerPvE.CanUse(out act, targetOverride: order))
        {
            return true;
        }

        // (Hide moved to the TOP of this method -- see the note there. It cannot live below the
        // ninjutsu machine, because the machine has already claimed _ninAim by the time we get
        // here.)

        return base.GeneralGCD(out act);
    }

    #endregion
}
