using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Data;
using Relicable.Model;

namespace Relicable.Steps;

// Atma stage upgrade: once all 12 atmas are held, perform the Zenith -> Atma enhancement at
// Jalzahn ("Relic Weapon Zenith Enhancement": the UNEQUIPPED Zenith weapon + the 12 atmas forge
// the il100 Atma/Zodiac weapon). The travel/interact/menu machinery lives in
// JalzahnUpgradeExecutorBase; this class supplies the Atma-specific behaviour that differs from
// the Nexus upgrade:
//   * the weapon must be UNEQUIPPED to appear in Jalzahn's turn-in list, so it is unequipped
//     before the trip and picked in the menu by the HELD Zenith weapon's name (not the equipped
//     one), and the result (an Atma weapon delivered unequipped) is re-equipped afterwards;
//   * completion is the equipped weapon reaching the Atma tier -- but since the atmas are consumed
//     by the trade, the item count is never the signal;
//   * the trade is gated on the "Up in Arms" quest (which unlocks Jalzahn's enhancement services)
//     and on holding all 12 atmas and a Zenith weapon.
//
// SEAM: the "Relic Weapon Zenith Enhancement" main-menu wording is confirmed against game data
// (CustomTalk 721061), but the turn-in submenu structure is not, so the weapon pick + confirm are
// best-effort with logging and safe-fail (see the base class).
public sealed class AtmaUpgradeExecutor : JalzahnUpgradeExecutorBase
{
    // "Up in Arms" (Quest.csv 66971, once-ever): Gerolt -> Jalzahn; it introduces Jalzahn and
    // unlocks his relic enhancement services. Without it complete, the Zenith enhancement is not
    // offered, so a first-relic player who never accepted it would farm 12/12 then stall at Jalzahn.
    private const uint UpInArmsQuestId = 66971;

    public override StepType Handles => StepType.AtmaUpgrade;

    protected override RelicStage TargetStage => RelicStage.Atma;

    // Deliberately EMPTY: an "atma" needle would match Jalzahn's sibling main-menu line "Relic
    // Weapon Atma Enhancement" (CustomTalk 721062) before "zenith enhancement" is tried. The
    // weapon-pick (by the held Zenith name) covers the turn-in submenu, so no sub needle is needed.
    protected override string[] SubMenuNeedles => System.Array.Empty<string>();

    // Jalzahn's main menu -> the Zenith enhancement branch (CustomTalk 721061). Full phrase so it
    // cannot match the "Atma/Novus/Animus Enhancement" siblings or "Zodiac Weapon Awakening".
    protected override string[] MainMenuNeedles => new[] { "zenith enhancement" };

    protected override string FlowLabel => "Atma upgrade (Jalzahn)";

    protected override string RegisterFailGuidance =>
        "Jalzahn's dialogue ended but no Atma weapon appeared. Make sure the Zenith weapon is " +
        "UNEQUIPPED, all 12 atmas are held, and 'Up in Arms' is complete, then choose 'Relic Weapon " +
        "Zenith Enhancement' at Jalzahn (Fallgourd Float, North Shroud) and select the weapon to turn " +
        "in. Do it manually if this persists (see the logged menu entries above).";

    // The held Zenith weapon's display name, captured at Start; the turn-in menu lists it (the
    // weapon is unequipped, so the equipped-hand name would be wrong/empty).
    private string _menuWeaponName = string.Empty;
    // True when this executor unequipped the Zenith weapon (so Stop can restore it on an abort).
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

    // Completion: an Atma-tier weapon is EQUIPPED, so the engine's stage read advances to Atma and
    // the run proceeds to Animus. OnInteractTick / TryFinishEarly equip the freshly-traded weapon.
    protected override bool IsUpgraded(ExecutionContext ctx)
        => GameState.EquippedRelicStage() >= RelicStage.Atma;

    // Already have the Atma weapon (the trade happened, or a re-select mid-flow): equip it if it is
    // sitting unequipped and finish, WITHOUT travelling. Runs before BlockedReason because the
    // consumed atmas make the count 0, which would otherwise trip the <12 block.
    protected override bool TryFinishEarly(ExecutionContext ctx) => EquipHeldAtmaWeapon();

    protected override string? BlockedReason(ExecutionContext ctx)
    {
        if (!GameState.IsQuestComplete(UpInArmsQuestId))
            return "the 'Up in Arms' quest is not complete, so Jalzahn does not offer the Zenith " +
                   "enhancement. Accept it from Gerolt (Hyrstmill, North Shroud) and finish it -- it " +
                   "sends you to Jalzahn -- then /relic start.";
        var held = GameState.AtmaCollectedCount();
        if (held < 12)
            return $"only {held}/12 atmas held -- the Zenith enhancement needs all 12. Finish the atma FATE farm first.";
        if (!GameState.TryFindHeldRelic(RelicWeaponStages.IsZenithWeapon, includeEquipped: true, out _, out _, out _))
            return "no Zenith weapon found in your hands, armoury chest, or bags. Trade your base relic + " +
                   "Thavnairian Mist at the Furnace (Gerolt, Hyrstmill) for its Zenith form first.";
        return null;
    }

    // Before the trip: the weapon must be UNEQUIPPED to appear in Jalzahn's turn-in list. Capture
    // the held Zenith weapon's name for the menu pick; if it is equipped, unequip it.
    protected override void OnBeforeTrip(ExecutionContext ctx)
    {
        if (GameState.TryFindHeldRelic(RelicWeaponStages.IsZenithWeapon, includeEquipped: true,
                out var container, out var slot, out var id))
        {
            _menuWeaponName = GameState.ItemName(id);
            if (container == InventoryType.EquippedItems && GameState.TryUnequipWeapon(slot))
                _unequipped = true;
        }
    }

    // Each Interact tick: if the trade produced an Atma weapon sitting unequipped, equip it so the
    // completion (Atma tier equipped) fires. Returns true when it did the equip.
    protected override bool OnInteractTick(ExecutionContext ctx) => EquipHeldAtmaWeapon();

    protected override void OnStop(ExecutionContext ctx)
    {
        // If we unequipped the Zenith and the trade did not happen (aborted mid-flow), the character
        // is left bare-handed; re-equip a held relic so the next Start reads the correct stage.
        if (_unequipped && GameState.EquippedRelicItemId() == 0
            && GameState.TryFindRelicInBags(out var c, out var s))
            GameState.TryEquipFromBag(c, s);
    }

    // Equip an Atma-tier relic weapon that is held but not currently in the main hand. True when it
    // did an equip (there was an unequipped Atma weapon to move in); false otherwise.
    private static bool EquipHeldAtmaWeapon()
    {
        if (GameState.EquippedRelicStage() >= RelicStage.Atma)
            return false;
        if (GameState.TryFindHeldRelic(id => RelicWeaponStages.StageOf(id) == RelicStage.Atma,
                includeEquipped: false, out var c, out var s, out _))
        {
            GameState.TryEquipFromBag(c, s);
            return true;
        }
        return false;
    }
}
