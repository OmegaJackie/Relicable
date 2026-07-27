using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Data;
using Relicable.Model;

namespace Relicable.Steps;

// Animus stage, final step (after all 9 Trials of the Braves books are complete): perform the
// Atma -> Animus enhancement at Jalzahn (Fallgourd Float, North Shroud) -- his "Relic Weapon Atma
// Enhancement" branch (CustomTalk 721062) turns in the UNEQUIPPED Atma weapon (with the completed
// books) for the il110 Animus weapon. The travel/interact/menu machinery lives in
// JalzahnUpgradeExecutorBase; this class supplies the Animus-specific behaviour, which is the exact
// analogue of the sibling Zenith -> Atma trade (AtmaUpgradeExecutor):
//   * the weapon must be UNEQUIPPED to appear in Jalzahn's turn-in list, so it is unequipped before
//     the trip (staying on the same job -- unequipping the main hand does not change your class,
//     the soul crystal does) and picked in the menu by the HELD Atma weapon's name, and the result
//     (an Animus weapon delivered unequipped) is re-equipped afterwards;
//   * completion is the equipped weapon reaching the Animus tier -- the books are consumed, so no
//     item count can be the signal;
//   * the trade is gated on the "Trials of the Braves" quest (66972, which unlocks the Animus stage
//     and introduces G'Jusana) and on holding an Atma weapon to turn in.
//
// SEAM: the "Relic Weapon Atma Enhancement" main-menu wording is confirmed against game data
// (CustomTalk 721062, cross-referenced by the sibling Atma flow), but the turn-in submenu structure
// is not, so the weapon pick + confirm are best-effort with logging and safe-fail (see the base
// class's menu-stuck detector).
public sealed class AnimusUpgradeExecutor : JalzahnUpgradeExecutorBase
{
    // "Trials of the Braves" (Quest.csv 66972, once-ever): introduces G'Jusana and opens the Animus
    // stage. It is complete long before book 9 (it wraps up after the first book), so a run that has
    // finished all 9 books has it done; the check only guards a malformed state where the engine
    // somehow reached the enhancement without the stage ever being unlocked.
    private const uint TrialsOfTheBravesQuestId = 66972;

    public override StepType Handles => StepType.AnimusUpgrade;

    protected override RelicStage TargetStage => RelicStage.Animus;

    // Deliberately EMPTY, mirroring the Atma flow: a broad "animus" sub needle would match Jalzahn's
    // sibling main-menu line "Relic Weapon Animus Enhancement" (the Animus -> Novus branch) before
    // the turn-in weapon pick is tried. The weapon pick (by the held Atma name) covers the turn-in
    // submenu, so no sub needle is needed.
    protected override string[] SubMenuNeedles => System.Array.Empty<string>();

    // Jalzahn's main menu -> the Atma enhancement branch (CustomTalk 721062). Full phrase so it
    // cannot substring-match the "Zenith/Animus/Novus Enhancement" siblings (none of them contain
    // "atma enhancement").
    protected override string[] MainMenuNeedles => new[] { "atma enhancement" };

    protected override string FlowLabel => "Animus upgrade (Jalzahn)";

    protected override string RegisterFailGuidance =>
        "Jalzahn's dialogue ended but no Animus weapon appeared. Make sure the Atma weapon is " +
        "UNEQUIPPED, all 9 Trials of the Braves books are complete, then choose 'Relic Weapon Atma " +
        "Enhancement' at Jalzahn (Fallgourd Float, North Shroud) and select the weapon to turn in. " +
        "Do it manually if this persists (see the logged menu entries above).";

    // The held Atma weapon's display name, captured at Start; the turn-in menu lists it (the weapon
    // is unequipped, so the equipped-hand name would be wrong/empty).
    private string _menuWeaponName = string.Empty;
    // True when this executor unequipped the Atma weapon (so Stop can restore it on an abort).
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

    // Completion: an Animus-tier weapon is EQUIPPED, so the engine's stage read advances to Animus
    // and the run proceeds to Novus. OnInteractTick / TryFinishEarly equip the freshly-traded weapon.
    protected override bool IsUpgraded(ExecutionContext ctx)
        => GameState.EquippedRelicStage() >= RelicStage.Animus;

    // Already have the Animus weapon (the trade happened, or a re-select mid-flow): equip it if it is
    // sitting unequipped and finish, WITHOUT travelling. Runs before BlockedReason.
    protected override bool TryFinishEarly(ExecutionContext ctx) => EquipHeldAnimusWeapon();

    protected override string? BlockedReason(ExecutionContext ctx)
    {
        if (!GameState.IsQuestComplete(TrialsOfTheBravesQuestId))
            return "the 'Trials of the Braves' quest is not complete, so the Animus stage is not " +
                   "unlocked and Jalzahn does not offer the Atma enhancement. Finish it (it introduces " +
                   "G'Jusana in Mor Dhona), then /relic start.";
        if (!GameState.TryFindHeldRelic(id => RelicWeaponStages.StageOf(id) == RelicStage.Atma,
                includeEquipped: true, out _, out _, out _))
            return "no Atma weapon found in your hands, armoury chest, or bags. The Atma -> Animus " +
                   "enhancement turns in the Atma weapon, so obtain it first (the Zenith enhancement " +
                   "at Jalzahn with 12 atmas).";
        return null;
    }

    // Before the trip: the weapon must be UNEQUIPPED to appear in Jalzahn's turn-in list. Capture
    // the held Atma weapon's name for the menu pick; if it is equipped, unequip it (this keeps the
    // same job -- the soul crystal, not the weapon, determines the class).
    protected override void OnBeforeTrip(ExecutionContext ctx)
    {
        if (GameState.TryFindHeldRelic(id => RelicWeaponStages.StageOf(id) == RelicStage.Atma,
                includeEquipped: true, out var container, out var slot, out var id))
        {
            _menuWeaponName = GameState.ItemName(id);
            if (container == InventoryType.EquippedItems)
            {
                // Remember the tier before it leaves the hands: stage is read off the EQUIPPED
                // weapon, so for the length of this trip the live read is None -- which would widen
                // Auto selection back to stages this character finished long ago. See RelicStageMemo.
                RelicStageMemo.Note(RelicStage.Atma);
                if (GameState.TryUnequipWeapon(slot))
                    _unequipped = true;
                else
                    RelicStageMemo.Clear(); // nothing moved; the live read is still authoritative
            }
        }
    }

    // Each Interact tick: if the trade produced an Animus weapon sitting unequipped, equip it so the
    // completion (Animus tier equipped) fires. Returns true when it did the equip.
    protected override bool OnInteractTick(ExecutionContext ctx) => EquipHeldAnimusWeapon();

    protected override void OnStop(ExecutionContext ctx)
    {
        // If we unequipped the Atma weapon and the trade did not happen (aborted mid-flow), the
        // character is left bare-handed; re-equip a held relic so the next Start reads the correct stage.
        if (_unequipped && GameState.EquippedRelicItemId() == 0
            && GameState.TryFindRelicInBags(out var c, out var s))
            GameState.TryEquipFromBag(c, s);
    }

    // Equip an Animus-tier relic weapon that is held but not currently in the main hand. True when it
    // did an equip (there was an unequipped Animus weapon to move in); false otherwise.
    private static bool EquipHeldAnimusWeapon()
    {
        if (GameState.EquippedRelicStage() >= RelicStage.Animus)
            return false;
        if (GameState.TryFindHeldRelic(id => RelicWeaponStages.StageOf(id) == RelicStage.Animus,
                includeEquipped: false, out var c, out var s, out _))
        {
            GameState.TryEquipFromBag(c, s);
            return true;
        }
        return false;
    }
}
