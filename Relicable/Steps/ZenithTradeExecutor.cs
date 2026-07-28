using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Steps;

// Zenith step 2: the Furnace beside Gerolt in Hyrstmill (North Shroud). Trades the finished bare
// base relic plus Thavnairian Mist for its il90 "<weapon> Zenith" form -- the last thing standing
// between a finished base relic and the Atma stage.
//
// ONE TRADE PER WEAPON. SpecialShop 1769484 gives every solo main hand its own 3-mist entry, and
// splits the Paladin's set into two (Curtana + 2, Holy Shield + 1). So this walks the weapons it
// found at Start and trades them one at a time, rather than assuming a single exchange.
//
// UNEQUIP FIRST, RE-EQUIP AFTER, exactly as the Atma turn-in at Jalzahn does: a trade lists what
// is in your bags, not what is in your hands, and the result is handed back unequipped. While the
// weapon is out of the hands the engine's stage read would collapse to None and widen Auto
// selection back to finished stages, so the tier is pinned through RelicStageMemo for the trip.
//
// SEAM -- THE FURNACE'S UI. Which shape the Furnace presents (a SelectString weapon list, or a
// shop grid) is not derivable from anything this plugin reads offline, so BOTH are driven, and
// both by POSITIVE IDENTIFICATION rather than a guessed row:
//   * a list menu is picked by the weapon's own NAME (text match), and
//   * a shop grid is picked by the ITEM ID of the Zenith weapon the row yields
//     (RelicWeaponStages.ZenithFormFor).
// Nothing is ever fired at a row that could not be identified. Each distinct menu is logged once,
// and if nothing matches, the step FAILS with that log rather than clicking something arbitrary --
// the same discipline JalzahnUpgradeExecutorBase and the Auriana purchase already use.
public sealed class ZenithTradeExecutor : ITaskExecutor
{
    private const long MenuActionCooldownMs = 500;
    private const long MenuStuckMs = 15000;
    private const long InteractCooldownMs = 600;
    private const long TradeRegisterGraceMs = 4000;
    private const long EquipGraceMs = 5000;
    private const float SearchRadius = 100f;
    private const float ArriveHorizontal = 2.0f;
    private const float ApproachStop = 1.0f;
    private const float LandHorizontal = 8.0f;
    private const float FlyMinDistance = 30.0f;
    private const long OverallTimeoutMs = 180_000;
    private const long DiagMs = 5000;

    public StepType Handles => StepType.ZenithTrade;

    private enum Phase { WaitExit, Teleport, Approach, Trade, Equipping, Done, Failed }

    private readonly AetheryteTeleportExecutor _teleport = new();

    private Phase _phase;
    private StepData? _teleStep;
    private long _startTicks;
    private long _lastMenuAction;
    private long _lastInteract;
    private long _lastDiag;
    private long _menuSince;
    private long _doneDeadline;
    private long _equipDeadline;
    private string _lastMenuSig = string.Empty;
    private bool _landing;
    private bool _unequipped;
    private Vector3? _resolvedAnchor;

    // The bare relics this run set out to trade, captured at Start. Completion is "none of THESE
    // are held any more", so an alt job's parked base relic sitting in the armoury can neither
    // block the step nor make it run forever.
    private readonly List<uint> _targets = new();

    public void Start(StepData step, ExecutionContext ctx)
    {
        _teleStep = null;
        _startTicks = Environment.TickCount64;
        _lastMenuAction = 0;
        _lastInteract = 0;
        _lastDiag = 0;
        _menuSince = 0;
        _doneDeadline = 0;
        _equipDeadline = 0;
        _lastMenuSig = string.Empty;
        _landing = false;
        _unequipped = false;
        _resolvedAnchor = null;
        _targets.Clear();

        foreach (var (_, itemId) in ZenithData.PendingTrades())
            _targets.Add(itemId);
        // Nothing in the hands is awaiting a trade, but one may be sitting in the bags from an
        // interrupted run -- pick those up too so a resumed run finishes the job.
        if (_targets.Count == 0
            && GameState.TryFindHeldRelic(RelicWeaponStages.IsBareBaseRelic, includeEquipped: true,
                out _, out _, out var held))
            _targets.Add(held);

        if (_targets.Count == 0)
        {
            _phase = Phase.Done;
            return;
        }

        var need = 0;
        foreach (var id in _targets)
            need += RelicWeaponStages.ZenithMistCost(id);
        var mist = ZenithData.MistItemId;
        var have = mist == 0 ? 0 : GameState.InventoryCount(mist);
        if (have < need)
        {
            DebugLog.Warn($"Zenith trade: only {have}/{need} {ZenithData.MistItemName} held for " +
                          $"{string.Join(" + ", _targets.Select(GameState.ItemName))}. Buy the rest from " +
                          "Auriana (Revenant's Toll), then /relic start.");
            _phase = Phase.Failed;
            return;
        }

        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        // The trade lists what is in the BAGS, so anything still in the hands comes off first.
        // Pin the tier before it leaves: the stage read is taken off the equipped weapon, and for
        // the length of this trip it would otherwise read None (see RelicStageMemo).
        foreach (var (slot, _) in ZenithData.PendingTrades())
        {
            RelicStageMemo.Note(RelicStage.Relic);
            if (GameState.TryUnequipWeapon(slot))
                _unequipped = true;
            else
                RelicStageMemo.Clear(); // nothing moved; the live read is still authoritative
        }

        DebugLog.Info($"Zenith trade: {string.Join(" + ", _targets.Select(GameState.ItemName))} for " +
                      $"{need}x {ZenithData.MistItemName} at the Furnace (Hyrstmill, North Shroud).");

        _phase = BoundByDuty() ? Phase.WaitExit : StartTrip(ctx);
    }

    private Phase StartTrip(ExecutionContext ctx)
    {
        if (ZenithData.FurnaceAetheryte != 0)
        {
            _teleStep = new StepData
            {
                Type = StepType.AetheryteTeleport,
                AetheryteId = ZenithData.FurnaceAetheryte,
            };
            _teleport.Start(_teleStep, ctx);
            return Phase.Teleport;
        }
        return Phase.Approach;
    }

    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    // Every weapon this run set out to trade is gone from the bags/hands -> the trades landed.
    private bool AllTraded()
        => _targets.All(id => !GameState.TryFindHeldRelic(x => x == id, includeEquipped: true,
            out _, out _, out _));

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        switch (_phase)
        {
            case Phase.Done:
                return ExecutorStatus.Complete;
            case Phase.Failed:
                return ExecutorStatus.Failed;
        }

        var now = Environment.TickCount64;

        // Authoritative completion: the bare relics are gone AND their Zenith forms are back in
        // the hands. Both halves matter -- "gone" alone is also true one frame after the trade
        // while the result sits unequipped, and finishing there would leave the run bare-handed
        // with the stage read collapsed.
        if (AllTraded())
        {
            // The trade landed. The result comes back UNEQUIPPED, so completion is not "the bare
            // relic is gone" -- it is the Zenith form actually being in the hands, otherwise the
            // step would finish with the run bare-handed and the stage read collapsed to None.
            if (RelicWeaponStages.IsZenithWeapon(GameState.EquippedRelicItemId()))
            {
                CloseFurnaceUi();
                RelicStageMemo.Clear();
                DebugLog.Info("Zenith trade: the Zenith weapon is equipped; the Atma stage is next.");
                _phase = Phase.Done;
                return ExecutorStatus.Complete;
            }
            // Arm the grace ONCE. Re-arming it each tick would make a weapon that can never be
            // equipped hang the step InProgress forever, which no failure counter can catch (this
            // branch returns before the overall timeout below).
            if (_phase != Phase.Equipping)
            {
                _phase = Phase.Equipping;
                _equipDeadline = now + EquipGraceMs;
            }
            EquipHeldZenith();
            if (now > _equipDeadline)
            {
                DebugLog.Warn("Zenith trade: the trade went through but the Zenith weapon could not be equipped " +
                              "(wrong job for it, or the slot was blocked). Equip it yourself, then /relic start.");
                return ExecutorStatus.Failed;
            }
            return ExecutorStatus.InProgress;
        }

        if (now - _startTicks > OverallTimeoutMs)
        {
            DebugLog.Warn($"Zenith trade: timed out in phase {_phase}. The Furnace stands beside Gerolt " +
                          "(Hyrstmill, North Shroud); trade the relic + Thavnairian Mist there manually if " +
                          "this persists (see any menu logged above).");
            return ExecutorStatus.Failed;
        }

        switch (_phase)
        {
            case Phase.WaitExit:
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                _phase = StartTrip(ctx);
                return ExecutorStatus.InProgress;

            case Phase.Teleport:
                var t = _teleport.Update(_teleStep!, ctx);
                if (t == ExecutorStatus.Failed)
                    return ExecutorStatus.Failed;
                if (t == ExecutorStatus.Complete)
                {
                    _teleport.Stop(ctx);
                    _phase = Phase.Approach;
                }
                return ExecutorStatus.InProgress;

            default:
                return TickFurnace(ctx, now);
        }
    }

    // Walk to the Furnace and drive whatever it opens.
    private ExecutorStatus TickFurnace(ExecutionContext ctx, long now)
    {
        // A shop/menu is up -> drive it. Checked before the approach so an already-open window is
        // never abandoned to go stand somewhere.
        if (AnyFurnaceUiOpen())
        {
            _phase = Phase.Trade;
            ctx.Navmesh.Stop();
            return DriveTrade(now);
        }

        if (_phase == Phase.Trade)
        {
            // The window closed without the trade registering; give it a moment to land (the
            // completion check at the top of Update ends the step the instant it does).
            if (_doneDeadline == 0)
                _doneDeadline = now + TradeRegisterGraceMs;
            else if (now > _doneDeadline)
            {
                DebugLog.Warn("Zenith trade: the Furnace's window closed but no Zenith weapon appeared. " +
                              "Check that the base relic is UNEQUIPPED and the Thavnairian Mist is in your " +
                              "bags, then /relic start (the menu wording was logged above).");
                return ExecutorStatus.Failed;
            }
            return ExecutorStatus.InProgress;
        }

        if (!ctx.Navmesh.IsReady())
        {
            _startTicks = now; // mesh build must not count against the budget
            return ExecutorStatus.InProgress;
        }

        var furnace = WorldObject.FindNearest(ZenithData.FurnaceObjectName, 0, SearchRadius, out var targetable);
        if (furnace == null)
        {
            TravelToAnchor(ctx, now);
            return ExecutorStatus.InProgress;
        }

        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? furnace.Position;
        var horiz = Vector2.Distance(new(me.X, me.Z), new(furnace.Position.X, furnace.Position.Z));

        if (!Combat.Mount.IsGrounded() && (_landing || horiz <= LandHorizontal))
        {
            _landing = true;
            Combat.Mount.LandAndDismount(ctx, furnace.Position);
            return ExecutorStatus.InProgress;
        }
        _landing = false;

        if (horiz > ArriveHorizontal)
        {
            _phase = Phase.Approach;
            if (horiz > FlyMinDistance)
                Combat.Mount.EnsureMounted(ctx, horiz);
            ctx.Navmesh.MoveCloseTo(furnace.Position, Plugin.Condition[ConditionFlag.InFlight], ApproachStop);
            if (now - _lastDiag > DiagMs)
            {
                _lastDiag = now;
                DebugLog.Info($"Zenith trade: approaching the Furnace ({horiz:0.0}y, targetable {targetable}).");
            }
            return ExecutorStatus.InProgress;
        }

        ctx.Navmesh.Stop();
        if (!Combat.Mount.IsGrounded())
        {
            Combat.Mount.EnsureDismounted(); // interaction is a no-op while mounted or airborne
            return ExecutorStatus.InProgress;
        }
        if (now - _lastInteract >= InteractCooldownMs)
        {
            _lastInteract = now;
            DebugLog.Info($"Zenith trade: interacting with the Furnace (targetable {targetable}).");
            WorldObject.Interact(furnace);
        }
        return ExecutorStatus.InProgress;
    }

    private void TravelToAnchor(ExecutionContext ctx, long now)
    {
        var ap = ZenithData.FurnaceAnchor;
        if (ap.Y == 0f)
        {
            _resolvedAnchor ??= ctx.Navmesh.LandableFloorForMapPoint(ap) ?? ctx.Navmesh.FloorForMapPoint(ap);
            if (_resolvedAnchor is { } snapped)
                ap = snapped;
        }
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? ap;
        var d = Vector3.Distance(me, ap);
        Combat.Mount.EnsureMounted(ctx, d);
        ctx.Navmesh.MoveCloseTo(ap, Flight.Allowed(ctx), 2.0f);
        if (now - _lastDiag > DiagMs)
        {
            _lastDiag = now;
            DebugLog.Info($"Zenith trade: the Furnace has not streamed in; travelling to Gerolt's spot ({d:0.0}y).");
        }
    }

    private static bool AnyFurnaceUiOpen()
        => DialogueMenu.AnyOpen()
           || DialogueMenu.IsOpen("ShopExchangeCurrency")
           || DialogueMenu.IsOpen("ShopExchangeCurrencyDialog")
           || DialogueMenu.IsOpen("ShopExchangeItem")
           || DialogueMenu.IsOpen("ShopExchangeItemDialog");

    // Drive whatever the Furnace opened. Every pick is a positive identification: a list entry by
    // the weapon's NAME, a shop row by the ITEM ID it yields. Throttled so a list addon is not
    // re-fired every frame, with a stuck timer so an unmatched menu fails instead of looping.
    private ExecutorStatus DriveTrade(long now)
    {
        var sig = DialogueMenu.OpenSignature();
        if (sig.Length > 0 && sig != _lastMenuSig)
        {
            DialogueMenu.LogOpenMenus("Zenith trade (Furnace)");
            _lastMenuSig = sig;
            _menuSince = now;
        }
        if (_menuSince != 0 && now - _menuSince > MenuStuckMs)
        {
            DebugLog.Warn($"Zenith trade: stuck on the same Furnace window for {MenuStuckMs / 1000}s without the " +
                          "trade registering -- nothing in it matched the weapon by name or the Zenith weapon by " +
                          "item id. The window was logged above; tell me its exact wording to wire it.");
            return ExecutorStatus.Failed;
        }

        if (now - _lastMenuAction < MenuActionCooldownMs)
            return ExecutorStatus.InProgress;
        _lastMenuAction = now;

        // A confirmation ALWAYS follows picking a trade, and it opens on top of a window that stays
        // open underneath it -- so it is answered FIRST. Re-firing the selection underneath while
        // the prompt is up is what would double-trade or cancel it.
        if (DialogueMenu.IsOpen("SelectYesno"))
        {
            if (DialogueMenu.ConfirmYes())
                DebugLog.Info("Zenith trade: confirming the trade prompt");
            return ExecutorStatus.InProgress;
        }

        // Quantity/confirm dialog of an item exchange.
        if (TryGetAddonMaster<AddonMaster.ShopExchangeItemDialog>("ShopExchangeItemDialog", out var itemDlg)
            && itemDlg.IsAddonReady)
        {
            itemDlg.Exchange();
            DebugLog.Info("Zenith trade: confirming the exchange");
            return ExecutorStatus.InProgress;
        }
        if (TryGetAddonMaster<AddonMaster.ShopExchangeCurrencyDialog>("ShopExchangeCurrencyDialog", out var curDlg)
            && curDlg.IsAddonReady)
        {
            curDlg.Exchange();
            DebugLog.Info("Zenith trade: confirming the exchange");
            return ExecutorStatus.InProgress;
        }

        // A shop grid: pick the row whose RESULT is the Zenith form of a weapon we are trading.
        // Matching on the yielded item id (never a row index) is what makes this safe to fire --
        // it cannot select a neighbouring job's trade or the wrong half of the Paladin pair.
        if (TryGetAddonMaster<AddonMaster.ShopExchangeCurrency>("ShopExchangeCurrency", out var shop)
            && shop.IsAddonReady)
        {
            foreach (var bare in _targets)
            {
                if (!GameState.TryFindHeldRelic(x => x == bare, includeEquipped: true, out _, out _, out _))
                    continue; // already traded
                var want = RelicWeaponStages.ZenithFormFor(bare);
                var entry = shop.BasicShopItems.FirstOrDefault(x => x.ItemId == want);
                if (want != 0 && entry != null)
                {
                    entry.Select(1);
                    DebugLog.Info($"Zenith trade: selecting {GameState.ItemName(want)} in the Furnace's list.");
                    return ExecutorStatus.InProgress;
                }
            }
        }

        // A list menu: pick the entry naming the weapon we are trading (or its Zenith result).
        foreach (var addon in new[] { "SelectString", "SelectIconString" })
        {
            if (!DialogueMenu.IsOpen(addon))
                continue;
            foreach (var bare in _targets)
            {
                if (!GameState.TryFindHeldRelic(x => x == bare, includeEquipped: true, out _, out _, out _))
                    continue; // already traded
                var bareName = GameState.ItemName(bare);
                if (bareName.Length > 0 && DialogueMenu.SelectByTextSafe(addon, bareName))
                {
                    DebugLog.Info($"Zenith trade: selecting '{bareName}' in the Furnace's menu.");
                    return ExecutorStatus.InProgress;
                }
                var zenName = GameState.ItemName(RelicWeaponStages.ZenithFormFor(bare));
                if (zenName.Length > 0 && DialogueMenu.SelectByTextSafe(addon, zenName))
                {
                    DebugLog.Info($"Zenith trade: selecting '{zenName}' in the Furnace's menu.");
                    return ExecutorStatus.InProgress;
                }
            }
        }

        return ExecutorStatus.InProgress;
    }

    // Equip a Zenith-form relic that is held but not in the hands. True when it did an equip.
    private static bool EquipHeldZenith()
    {
        if (RelicWeaponStages.IsZenithWeapon(GameState.EquippedRelicItemId()))
            return false;
        if (GameState.TryFindHeldRelic(RelicWeaponStages.IsZenithWeapon, includeEquipped: false,
                out var c, out var s, out _))
        {
            GameState.TryEquipFromBag(c, s);
            return true;
        }
        return false;
    }

    private void CloseFurnaceUi()
    {
        DialogueMenu.FireClose("ShopExchangeItem");
        DialogueMenu.FireClose("ShopExchangeCurrency");
        DialogueMenu.FireClose("SelectString");
        DialogueMenu.FireClose("SelectIconString");
    }

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
        // Aborted after unequipping and before the trade: the character is left bare-handed, which
        // would make the next Start read the wrong stage. Put a relic back in the hands.
        if (_unequipped && GameState.EquippedRelicItemId() == 0
            && GameState.TryFindRelicInBags(out var c, out var s))
            GameState.TryEquipFromBag(c, s);
    }
}
