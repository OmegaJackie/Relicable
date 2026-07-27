using System;
using System.Linq;
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Steps;

// Base-relic FINAL step (quest sequence 19): buy one Radz-at-Han Quenching Oil from Auriana at
// Revenant's Toll (15 Allagan Tomestones of Poetics), which the subsequent InteractNpc turns in to
// Gerolt to finish the il80 relic. Reuses the Mysterious-Map restock's Auriana navigation
// (NovusData.AurianaDataId / AurianaApproachPosition) and drives her Poetics tomestone exchange via
// ECommons' AddonMaster.ShopExchangeCurrency (the same grid the map restock leaves alone). Completion
// is authoritative: the oil is in the bag (GameState.InventoryCount(RadzOilItemId) >= 1).
//
// SEAM (offline-unverifiable): the wording of Auriana's option that opens the Poetics exchange, and
// whether selecting the oil pops a quantity dialog, are not in any offline data source, so the option
// is needle-matched ("poetics" / "tomestone" / "purchase") and each open menu is logged once
// (LogOpenMenus) so the real wording is visible if nothing matches. The step FAILS (never
// false-completes) if the oil is not obtained, so a wrong needle stalls safely for the backoff.
public sealed class BuyRadzOilExecutor : ITaskExecutor
{
    private const long MenuActionCooldownMs = 500;
    // Fail if a single Auriana menu stays open this long without the oil appearing (our pick or the
    // shop drive did not work) -- so a wrong needle halts the backoff with the logged menu, not a hang.
    private const long MenuStuckMs = 15000;

    public StepType Handles => StepType.BuyRadzOil;

    private readonly NpcInteractor _npc = new();
    private long _lastMenuAction;
    private long _menuSince;
    private long _closeSince;
    private string _lastMenuSig = string.Empty;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _npc.Reset();
        _lastMenuAction = 0;
        _menuSince = 0;
        _closeSince = 0;
        _lastMenuSig = string.Empty;
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        var oil = NovusData.RadzOilItemId;
        if (oil == 0)
        {
            DebugLog.Warn("Buy oil: could not resolve 'Radz-at-Han Quenching Oil' in the Item sheet; " +
                          "buy it from Auriana (Revenant's Toll, 15 Poetics) and turn in to Gerolt manually.");
            return ExecutorStatus.Failed;
        }

        // Authoritative completion: the oil is in the bag (just bought, or a leftover from a prior run).
        // Close Auriana's shop/menu first so the next step's teleport is not blocked by an open window.
        if (GameState.InventoryCount(oil) >= 1)
        {
            if (CloseAurianaUi())
                return ExecutorStatus.InProgress;
            DebugLog.Info("Buy oil: quenching oil obtained; heading to Gerolt to finish the relic.");
            return ExecutorStatus.Complete;
        }

        if (NovusData.AurianaDataId == 0)
        {
            DebugLog.Warn("Buy oil: Auriana's NPC id did not resolve; buy the oil manually, then /relic start.");
            return ExecutorStatus.Failed;
        }

        // The Poetics tomestone grid is open -> buy the oil (and confirm any quantity dialog).
        if (TryDriveShop(oil))
            return ExecutorStatus.InProgress;

        // Auriana's option list is open -> pick the Poetics exchange that opens the grid.
        if (DialogueMenu.AnyOpen())
        {
            var sig = DialogueMenu.OpenSignature();
            if (sig.Length > 0 && sig != _lastMenuSig)
            {
                DialogueMenu.LogOpenMenus("Buy oil (Auriana)");
                _lastMenuSig = sig;
                _menuSince = Environment.TickCount64; // menu advanced; restart the stuck timer
            }
            if (_menuSince != 0 && Environment.TickCount64 - _menuSince > MenuStuckMs)
            {
                DebugLog.Warn($"Buy oil: stuck on the same Auriana menu for {MenuStuckMs / 1000}s without the oil " +
                              "appearing -- the option that opens the Poetics exchange likely did not match. The open " +
                              "menu was logged above; tell me its exact wording to wire it.");
                return ExecutorStatus.Failed;
            }
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                TrySelectExchange();
                DialogueMenu.ConfirmYes(); // in case selecting the oil pops a Yes/No buy prompt
            }
            return ExecutorStatus.InProgress;
        }

        // Nothing open: walk to Auriana (she stands behind a stall, so approach the authored front spot)
        // and interact to open her menu.
        if (_npc.Tick(NovusData.AurianaDataId, NovusData.AurianaApproachPosition, ctx, approachFromPlayerSide: true)
            == InteractionPhase.Failed)
        {
            DebugLog.Warn("Buy oil: could not reach Auriana at Revenant's Toll to buy the quenching oil.");
            return ExecutorStatus.Failed;
        }
        return ExecutorStatus.InProgress;
    }

    // Drive Auriana's Poetics tomestone exchange (ShopExchangeCurrency) to buy one oil, confirming the
    // quantity dialog if it appears. Returns true while either shop window is open (caller stays
    // InProgress). Throttled so a list addon is not re-fired every frame.
    private bool TryDriveShop(uint oil)
    {
        if (TryGetAddonMaster<AddonMaster.ShopExchangeCurrencyDialog>("ShopExchangeCurrencyDialog", out var dlg)
            && dlg.IsAddonReady)
        {
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                dlg.Exchange();
                DebugLog.Info("Buy oil: confirming the purchase quantity");
            }
            return true;
        }

        if (TryGetAddonMaster<AddonMaster.ShopExchangeCurrency>("ShopExchangeCurrency", out var shop)
            && shop.IsAddonReady)
        {
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                var entry = shop.BasicShopItems.FirstOrDefault(x => x.ItemId == oil);
                if (entry != null)
                {
                    entry.Select(1);
                    DebugLog.Info("Buy oil: selecting Radz-at-Han Quenching Oil in Auriana's Poetics exchange");
                }
                else
                {
                    DebugLog.Warn($"Buy oil: the Poetics exchange is open but the oil (item {oil}) is not listed. " +
                                  "This may be the wrong exchange, or you are short on Poetics. Buy it manually if it persists.");
                }
            }
            return true;
        }

        return false;
    }

    // Pick Auriana's option that opens the Poetics tomestone grid. The wording is unknown offline, so
    // needle in preference order, avoiding the map / disposal / leave entries.
    private static void TrySelectExchange()
    {
        foreach (var addon in new[] { "SelectIconString", "SelectString" })
        {
            if (!DialogueMenu.IsOpen(addon))
                continue;
            foreach (var needle in new[] { "poetics", "tomestone", "purchase" })
                if (DialogueMenu.SelectByText(addon, needle))
                    return;
        }
    }

    // After the oil is in the bag, close whatever Auriana window is still open so the next step's
    // teleport is not blocked. Returns true while something is still open (caller stays InProgress),
    // bounded by a grace so a stubborn window cannot hang the step (the teleport retries if occupied).
    private bool CloseAurianaUi()
    {
        var dialogOpen = DialogueMenu.IsOpen("ShopExchangeCurrencyDialog");
        var shopOpen = DialogueMenu.IsOpen("ShopExchangeCurrency");
        if (!dialogOpen && !shopOpen && !DialogueMenu.AnyOpen())
        {
            _closeSince = 0;
            return false;
        }
        if (_closeSince == 0)
            _closeSince = Environment.TickCount64;
        if (Environment.TickCount64 - _closeSince > 3000)
            return false; // gave up closing; let the step complete (teleport handles an occupied state)
        if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
        {
            _lastMenuAction = Environment.TickCount64;
            if (dialogOpen
                && TryGetAddonMaster<AddonMaster.ShopExchangeCurrencyDialog>("ShopExchangeCurrencyDialog", out var d)
                && d.IsAddonReady)
                d.Cancel();
            else if (shopOpen)
                DialogueMenu.FireClose("ShopExchangeCurrency");
            else
            {
                DialogueMenu.FireClose("SelectString");
                DialogueMenu.FireClose("SelectIconString");
            }
        }
        return true;
    }

    public void Stop(ExecutionContext ctx)
    {
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
