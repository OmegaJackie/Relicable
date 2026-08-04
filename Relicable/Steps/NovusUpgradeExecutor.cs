using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Data;
using Relicable.Model;
using Relicable.Novus;

namespace Relicable.Steps;

// Novus stage, final step (once the Sphere Scroll reads 75/75): perform the Animus -> Novus
// enhancement at Jalzahn (Hyrstmill, North Shroud, reached from Fallgourd Float) -- his "Relic Weapon
// Animus Enhancement" branch, verified in game data as CustomTalk 721069 on ENpcBase 1008948
// (Jalzahn), gated by the Novus quest "Star Light, Star Bright" (67000). It turns in the UNEQUIPPED
// Animus weapon together with the filled Sphere Scroll for the il115 Novus weapon.
//
// The travel/interact/menu machinery lives in JalzahnUpgradeExecutorBase; this class is the exact
// analogue of the sibling Atma -> Animus trade (AnimusUpgradeExecutor):
//   * the weapon must be UNEQUIPPED to appear in Jalzahn's turn-in list, so it is unequipped before
//     the trip (staying on the same job -- the soul crystal, not the weapon, sets your class) and
//     picked in the menu by the HELD Animus weapon's name, and the result (a Novus weapon delivered
//     unequipped) is re-equipped afterwards;
//   * completion is the equipped weapon reaching the Novus tier -- the scroll is consumed, so no item
//     count can be the signal;
//   * the trade is gated on the scroll actually being full and on holding an Animus weapon.
//
// SEAM: the "Relic Weapon Animus Enhancement" main-menu wording is confirmed against game data
// (CustomTalk 721069), but the turn-in submenu structure is not, so the weapon pick + confirm are
// best-effort with logging and safe-fail (see the base class's menu-stuck detector).
public sealed class NovusUpgradeExecutor : JalzahnUpgradeExecutorBase
{
    // "Star Light, Star Bright" (Quest.csv 67000): the quest CustomTalk 721069 hangs off, i.e. what
    // makes Jalzahn offer the Animus enhancement at all. Zodiac stage quests park at sequence 0xFF
    // for the whole grind instead of completing, so "complete" alone is the wrong test -- accepted
    // (a live sequence) counts too, and only "never picked up" blocks.
    private const uint StarLightStarBrightQuestId = 67000;

    public override StepType Handles => StepType.NovusUpgrade;

    protected override RelicStage TargetStage => RelicStage.Novus;

    // Deliberately EMPTY, mirroring the Atma and Animus flows: a broad "novus" sub needle would match
    // Jalzahn's sibling main-menu line "Relic Weapon Novus Enhancement" (the Novus -> Nexus branch)
    // before the turn-in weapon pick is tried, and walk into the wrong upgrade. The weapon pick (by
    // the held Animus weapon's name) covers the turn-in submenu, so no sub needle is needed.
    protected override string[] SubMenuNeedles => System.Array.Empty<string>();

    // Jalzahn's main menu -> the Animus enhancement branch (CustomTalk 721069). Full phrase so it
    // cannot substring-match the "Zenith/Atma/Novus Enhancement" siblings (none of them contain
    // "animus enhancement").
    protected override string[] MainMenuNeedles => new[] { "animus enhancement" };

    protected override string FlowLabel => "Novus upgrade (Jalzahn)";

    protected override string RegisterFailGuidance =>
        "Jalzahn's dialogue ended but no Novus weapon appeared. Make sure the Animus weapon is " +
        "UNEQUIPPED and the Sphere Scroll is at its cap, then choose 'Relic Weapon Animus " +
        "Enhancement' at Jalzahn (Hyrstmill, North Shroud -- teleport to Fallgourd Float) and select " +
        "the weapon to turn in. Do it manually if this persists (see the logged menu entries above).";

    // The held Animus weapon's display name, captured at Start; the turn-in menu lists it (the weapon
    // is unequipped by then, so the equipped-hand name would be empty).
    private string _menuWeaponName = string.Empty;
    // True when this executor unequipped the Animus weapon (so Stop can restore it on an abort).
    private bool _unequipped;

    // This executor is a reused singleton (one per StepType), so per-run fields MUST be cleared each
    // Start -- especially _unequipped, which is otherwise a one-way latch that would make OnStop
    // re-equip a bag relic on a later, unrelated abort.
    protected override void OnReset()
    {
        _menuWeaponName = string.Empty;
        _unequipped = false;
    }

    protected override string WeaponMenuName(ExecutionContext ctx) => _menuWeaponName;

    // Completion: a Novus-tier weapon is EQUIPPED, so the engine's stage read advances to Novus and
    // the run proceeds to the Nexus Light farm. OnInteractTick / TryFinishEarly equip the freshly
    // traded weapon (it lands unequipped).
    protected override bool IsUpgraded(ExecutionContext ctx)
        => GameState.EquippedRelicStage() >= RelicStage.Novus;

    // Already have the Novus weapon (the trade happened, or a re-select mid-flow): equip it if it is
    // sitting unequipped and finish, WITHOUT travelling. Runs before BlockedReason.
    protected override bool TryFinishEarly(ExecutionContext ctx) => EquipHeldNovusWeapon();

    protected override string? BlockedReason(ExecutionContext ctx)
    {
        if (!GameState.IsQuestComplete(StarLightStarBrightQuestId) &&
            GameState.QuestSequence(StarLightStarBrightQuestId) == 0)
            return "the 'Star Light, Star Bright' quest has not been picked up, so Jalzahn does not " +
                   "offer the Animus enhancement yet. Take it from him (Hyrstmill, North Shroud) -- it " +
                   "is what hands out the Sphere Scroll -- then /relic start.";

        if (!NovusScrollState.IsScrollFull(ctx.Config))
        {
            var (cur, max) = NovusScrollState.Progress(ctx.Config);
            return $"the Sphere Scroll is not full yet ({cur}/{max} infused at the last reading). " +
                   "Finish the melding route first -- open /relic novus, or open the scroll's window " +
                   "once so the count can be read if you melded it elsewhere.";
        }

        if (!GameState.TryFindHeldRelic(id => RelicWeaponStages.StageOf(id) == RelicStage.Animus,
                includeEquipped: true, out _, out _, out _))
            return "no Animus weapon found in your hands, armoury chest, or bags. The Animus -> Novus " +
                   "enhancement turns in the Animus weapon, so obtain it first (the Atma enhancement " +
                   "at Jalzahn with the nine Trials of the Braves books).";

        return null;
    }

    // Before the trip: the weapon must be UNEQUIPPED to appear in Jalzahn's turn-in list. Capture the
    // held Animus weapon's name for the menu pick; if it is equipped, unequip it (this keeps the same
    // job -- the soul crystal, not the weapon, determines the class).
    protected override void OnBeforeTrip(ExecutionContext ctx)
    {
        if (GameState.TryFindHeldRelic(id => RelicWeaponStages.StageOf(id) == RelicStage.Animus,
                includeEquipped: true, out var container, out var slot, out var id))
        {
            _menuWeaponName = GameState.ItemName(id);
            if (container == InventoryType.EquippedItems)
            {
                // Remember the tier before it leaves the hands: stage is read off the EQUIPPED weapon,
                // so for the length of this trip the live read is None -- which would widen Auto
                // selection back to stages this character finished long ago. See RelicStageMemo.
                RelicStageMemo.Note(RelicStage.Animus);
                if (GameState.TryUnequipWeapon(slot))
                    _unequipped = true;
                else
                    RelicStageMemo.Clear(); // nothing moved; the live read is still authoritative
            }
        }
    }

    // Each Interact tick: if the trade produced a Novus weapon sitting unequipped, equip it so the
    // completion (Novus tier equipped) fires. Returns true when it did the equip.
    protected override bool OnInteractTick(ExecutionContext ctx) => EquipHeldNovusWeapon();

    protected override void OnStop(ExecutionContext ctx)
    {
        // The scrolls are consumed by the trade, so their recorded counters describe a scroll that no
        // longer exists. Drop them once the Novus weapon is in hand: a repeat relic then re-arms the
        // melding work from an unknown state instead of inheriting a stale "full".
        if (GameState.EquippedRelicStage() >= RelicStage.Novus)
        {
            NovusScrollState.Clear(ctx.Config, () => Plugin.PluginInterface.SavePluginConfig(ctx.Config));

            // Paladin carries TWO relic pieces (Curtana + Holy Shield) with a scroll each, so the
            // enhancement is two turn-ins. Completion is the equipped MAIN HAND reaching Novus -- that
            // is deliberately not tightened to "both held", so a line that hands over both at once can
            // never false-fail here. Say so instead when only one arrived.
            if (ctx.Config.NovusWeapon == NovusWeaponProfile.Paladin && !HoldsHolyShieldNovus())
                Diagnostics.DebugLog.Warn("Curtana Novus is done, but no Holy Shield Novus is held. " +
                    "Paladin's shield is a second turn-in: fill its Sphere Scroll (22 points) and run " +
                    "'Relic Weapon Animus Enhancement' at Jalzahn again for it.");
        }

        // If we unequipped the Animus weapon and the trade did not happen (aborted mid-flow), the
        // character is left bare-handed; re-equip a held relic so the next Start reads the right stage.
        if (_unequipped && GameState.EquippedRelicItemId() == 0
            && GameState.TryFindRelicInBags(out var c, out var s))
            GameState.TryEquipFromBag(c, s);
    }

    // Holy Shield Novus (7872) anywhere -- hands, armoury, or bags.
    private static bool HoldsHolyShieldNovus()
        => GameState.TryFindHeldRelic(id => id == 7872, includeEquipped: true, out _, out _, out _);

    // Is this item id a Novus relic? Both tests, to match GameState's own stage read: the eleven
    // Novus ids are an explicit set there, and the names are also "<base> Novus" so the suffix table
    // resolves them too. Either alone would be enough; together they cannot disagree with the engine.
    private static bool IsNovusWeapon(uint id)
        => GameState.NovusRelicItemIds.Contains(id) || RelicWeaponStages.StageOf(id) == RelicStage.Novus;

    // Equip a Novus-tier relic weapon that is held but not currently in the main hand. True when it
    // did an equip (there was an unequipped Novus weapon to move in); false otherwise.
    private static bool EquipHeldNovusWeapon()
    {
        if (GameState.EquippedRelicStage() >= RelicStage.Novus)
            return false;
        if (GameState.TryFindHeldRelic(IsNovusWeapon, includeEquipped: false, out var c, out var s, out _))
        {
            GameState.TryEquipFromBag(c, s);
            return true;
        }
        return false;
    }
}
