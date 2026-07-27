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
// PICKING THE RIGHT EXCHANGE. Auriana does not offer one "Poetics" option -- she offers SEVERAL,
// and every one of them is named "Allagan Tomestones of Poetics (...)". So a "poetics" needle matches
// them all and takes whichever is listed first, which is a gear category: reported live as opening
// the Disciple of War arms grid, where the oil is of course not sold ("the Poetics exchange is open
// but the oil is not listed"). The relic materials live under the SPECIAL ARMS category.
//
// Rather than trust one guessed wording, the step ranks her actual menu entries and walks them: the
// category whose name looks like the relic one is tried first, and if a grid opens WITHOUT the oil in
// it, that grid is closed and the next candidate is tried. So the correct entry is found even if the
// wording is not what we expect, and the only way to fail is genuinely running out of entries.
public sealed class BuyRadzOilExecutor : ITaskExecutor
{
    private const long MenuActionCooldownMs = 500;
    // Fail if a single Auriana menu stays open this long without the oil appearing (our pick or the
    // shop drive did not work) -- so a wrong needle halts the backoff with the logged menu, not a hang.
    private const long MenuStuckMs = 15000;

    // Ranking hints for her category list, best first. "Special arms" is the category that actually
    // stocks the Zodiac materials; the rest are fallbacks in case the wording differs by patch or
    // client language, and anything unmatched is still tried afterwards in menu order.
    private static readonly string[] PreferredCategories = { "special arms", "special", "arms" };

    // Entries that are certainly NOT the exchange we want, so they are never tried: her OTHER relic
    // exchange (the Novus treasure maps), and the leave/cancel lines.
    private static readonly string[] SkipCategories =
    {
        "mysterious map", "nothing", "cancel", "quit", "leave", "disposal",
    };

    public StepType Handles => StepType.BuyRadzOil;

    private readonly NpcInteractor _npc = new();
    private long _lastMenuAction;
    private long _menuSince;
    private long _closeSince;
    private string _lastMenuSig = string.Empty;

    // Auriana's exchange entries in the order this step will try them, and how far through we are.
    // Built from the live menu the first time it is seen (see BuildCandidates).
    private readonly System.Collections.Generic.List<string> _candidates = new();
    private int _candidateIdx;
    // The candidate whose grid is currently open, so opening a grid that lacks the oil retires that
    // exact entry rather than whichever index we happen to be on.
    private string _openedWith = string.Empty;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _npc.Reset();
        _lastMenuAction = 0;
        _menuSince = 0;
        _closeSince = 0;
        _lastMenuSig = string.Empty;
        _candidates.Clear();
        _candidateIdx = 0;
        _openedWith = string.Empty;
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

        // A purchase confirmation ALWAYS follows picking an item, and it is checked FIRST because it
        // opens on top of a shop window that stays open underneath it: the grid and the quantity
        // dialog both still report as open, so driving them first means re-firing "Exchange" at a
        // window that is waiting on a Yes/No nobody is answering. That is the reported "it doesn't
        // click yes on the dialogue box after selecting the oil" -- the purchase never completed and
        // the step burned its stuck timer one tick at a time.
        //
        // Nothing else runs on a tick that answers a prompt: the selection underneath has already
        // been made, and re-firing it while the prompt is up is what would double-buy or cancel it.
        if (DialogueMenu.IsOpen("SelectYesno"))
        {
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                if (DialogueMenu.ConfirmYes())
                    DebugLog.Info("Buy oil: confirming the purchase prompt");
            }
            return ExecutorStatus.InProgress;
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
            // Every one of her exchanges has been opened and none stocked the oil.
            if (_candidates.Count > 0 && _candidateIdx >= _candidates.Count)
            {
                DebugLog.Warn("Buy oil: tried every exchange Auriana offers and none listed the quenching oil " +
                              $"([{string.Join(" | ", _candidates)}]). Either you are short on Poetics, or she no " +
                              "longer stocks it here -- buy it manually, then /relic start.");
                return ExecutorStatus.Failed;
            }

            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                TrySelectExchange();
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
                    // Wrong category: this grid does not stock the oil. Retire the entry that opened
                    // it, close it, and let the next tick re-open her menu and try the next candidate.
                    // Sitting here would just burn the stuck timer on a grid that can never have it.
                    RetireOpenCategory(shop.BasicShopItems.Count());
                    DialogueMenu.FireClose("ShopExchangeCurrency");
                }
            }
            return true;
        }

        return false;
    }

    // Pick the entry in Auriana's menu that opens the exchange stocking the oil. Her categories are
    // all called "Allagan Tomestones of Poetics (...)", so this works off the LIST rather than a
    // single word: rank her live entries once, then select the current candidate by its own text.
    private void TrySelectExchange()
    {
        foreach (var addon in new[] { "SelectIconString", "SelectString" })
        {
            if (!DialogueMenu.IsOpen(addon))
                continue;

            if (_candidates.Count == 0)
                BuildCandidates(addon);
            if (_candidateIdx >= _candidates.Count)
                return; // exhausted; Update reports it

            var choice = _candidates[_candidateIdx];
            if (DialogueMenu.SelectByTextSafe(addon, choice))
            {
                _openedWith = choice;
                DebugLog.Info($"Buy oil: opening Auriana's '{choice}'.");
            }
            return;
        }
    }

    // Rank Auriana's live menu entries into the order to try them: the category that looks like the
    // relic/special-arms one first, then everything else in menu order. Nothing is filtered except
    // the entries that certainly cannot be it (her map exchange, and leave/cancel), so an unexpected
    // wording still gets its turn instead of the step giving up on a guess.
    private void BuildCandidates(string addon)
    {
        var entries = DialogueMenu.EntryTexts(addon);
        if (entries.Count == 0)
            return; // addon not ready yet; retry next tick

        var usable = entries
            .Where(e => !SkipCategories.Any(s => e.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _candidates.AddRange(usable.OrderBy(e =>
        {
            for (var i = 0; i < PreferredCategories.Length; i++)
                if (e.Contains(PreferredCategories[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            return PreferredCategories.Length; // unranked: keep menu order behind the preferred ones
        }));

        _candidateIdx = 0;
        DebugLog.Info($"Buy oil: Auriana offers {entries.Count} option(s); trying them in the order " +
                      $"[{string.Join(" | ", _candidates)}].");
    }

    // Mark the currently-open exchange as "does not stock the oil" and advance to the next candidate.
    private void RetireOpenCategory(int listed)
    {
        var name = _openedWith.Length > 0 ? _openedWith : "(unknown option)";
        _openedWith = string.Empty;
        _candidateIdx++;
        // RESTART the stuck clock rather than clearing it: the next attempt deserves a fresh budget,
        // but re-opening the same menu leaves the signature unchanged, so clearing it would leave the
        // watchdog permanently disarmed and a selection that opens nothing would loop forever.
        _menuSince = Environment.TickCount64;
        _npc.Reset();     // let the interactor re-open her menu once this grid closes
        DebugLog.Info($"Buy oil: '{name}' lists {listed} item(s) but not the quenching oil; " +
                      "closing it and trying the next exchange.");
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
