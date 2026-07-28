using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Steps;

// Buying N of an item from Auriana's Allagan Tomestones of Poetics exchange at Revenant's Toll:
// walk to her, find the exchange that actually stocks the item, buy the shortfall, answer the
// confirmation, and close up behind us. Extracted verbatim from BuyRadzOilExecutor (which was
// the only currency purchase in the line) so the Zenith step can buy Thavnairian Mist from the
// same NPC and the same grid without a second copy of the flow drifting out of sync.
//
// PICKING THE RIGHT EXCHANGE. Auriana does not offer one "Poetics" option -- she offers SEVERAL,
// and every one of them is named "Allagan Tomestones of Poetics (...)". So a "poetics" needle
// matches them all and takes whichever is listed first, which is a gear category: reported live
// as opening the Disciple of War arms grid, where the relic materials of course are not sold. The
// relic materials live under the SPECIAL ARMS category.
//
// Rather than trust one guessed wording, this ranks her actual menu entries and walks them: the
// category whose name looks like the relic one is tried first, and if a grid opens WITHOUT the
// wanted item in it, that grid is closed and the next candidate is tried. So the correct entry is
// found even if the wording is not what we expect, and the only way to fail is genuinely running
// out of entries.
//
// This is a DRIVER, not an ITaskExecutor: it holds the per-purchase state and is ticked by an
// executor that owns the travel and the surrounding flow.
public sealed class AurianaPoeticsShop
{
    public enum Result
    {
        InProgress, // still walking / picking / buying -- caller stays InProgress
        Complete,   // the wanted count is held and her windows are closed
        Failed,     // ran out of exchanges, stuck, or the purchase is deliberately refused
    }

    private const long MenuActionCooldownMs = 500;
    // Fail if a single Auriana menu stays open this long without the item appearing (our pick or
    // the shop drive did not work) -- so a wrong needle halts with the logged menu, not a hang.
    private const long MenuStuckMs = 15000;

    // Ranking hints for her category list, best first. "Special arms" is the category that
    // actually stocks the Zodiac materials; the rest are fallbacks in case the wording differs by
    // patch or client language, and anything unmatched is still tried afterwards in menu order.
    private static readonly string[] PreferredCategories = { "special arms", "special", "arms" };

    // Entries that are certainly NOT the exchange we want, so they are never tried: her OTHER
    // relic exchange (the Novus treasure maps), and the leave/cancel lines.
    private static readonly string[] SkipCategories =
    {
        "mysterious map", "nothing", "cancel", "quit", "leave", "disposal",
    };

    private readonly NpcInteractor _npc = new();
    private long _lastMenuAction;
    private long _menuSince;
    private long _closeSince;
    private string _lastMenuSig = string.Empty;

    // Auriana's exchange entries in the order this purchase will try them, and how far through we
    // are. Built from the live menu the first time it is seen (see BuildCandidates).
    private readonly List<string> _candidates = new();
    private int _candidateIdx;
    // The candidate whose grid is currently open, so opening a grid that lacks the item retires
    // that exact entry rather than whichever index we happen to be on.
    private string _openedWith = string.Empty;

    // Clear all per-purchase state. MUST be called before each purchase: the owning executors are
    // reused singletons, so a stale candidate walk would otherwise resume mid-list on a later run.
    public void Reset()
    {
        _npc.Reset();
        _lastMenuAction = 0;
        _menuSince = 0;
        _closeSince = 0;
        _lastMenuSig = string.Empty;
        _candidates.Clear();
        _candidateIdx = 0;
        _openedWith = string.Empty;
    }

    // One tick of the purchase. itemId/itemName describe what to buy, want is the TOTAL that must
    // be held when this finishes, and label prefixes every log line ("Buy oil", "Zenith").
    public Result Tick(uint itemId, string itemName, int want, string label, ExecutionContext ctx)
    {
        if (itemId == 0)
        {
            DebugLog.Warn($"{label}: could not resolve '{itemName}' in the Item sheet; buy it from " +
                          "Auriana (Revenant's Toll) manually, then /relic start.");
            return Result.Failed;
        }

        // Authoritative completion: the item is in the bag (just bought, or held from a prior run).
        // Close Auriana's shop/menu first so the next step's teleport is not blocked by an open window.
        var have = GameState.InventoryCount(itemId);
        if (have >= want)
        {
            if (CloseAurianaUi())
                return Result.InProgress;
            DebugLog.Info($"{label}: {have}x {itemName} held (needed {want}).");
            return Result.Complete;
        }

        if (NovusData.AurianaDataId == 0)
        {
            DebugLog.Warn($"{label}: Auriana's NPC id did not resolve; buy {want}x {itemName} manually, then /relic start.");
            return Result.Failed;
        }

        // Do not buy what is already owned. The bag check above covers what is on the character;
        // this covers a RETAINER, whose contents cannot be read unless one is open, so it uses the
        // cache the plugin builds during its own retainer visits (see PurchaseGuard). Poetics are
        // farmed, so spending more on something sitting in a retainer's bag is real waste. Stops
        // instead of withdrawing: pulling the item needs a summoning bell and a retainer trip,
        // which is the player's call, not something to do silently mid-run.
        PurchaseGuard.FindHeld(ctx.Config, itemId, out _, out var onRetainers, out var where);
        if (onRetainers > 0)
        {
            DebugLog.Warn($"{label}: you already have {onRetainers}x {itemName} on " +
                          $"{(where.Length > 0 ? where : "a retainer")} -- not buying more. Withdraw it " +
                          "(or buy it yourself if you would rather), then /relic start.");
            return Result.Failed;
        }

        // A purchase confirmation ALWAYS follows picking an item, and it is checked FIRST because
        // it opens on top of a shop window that stays open underneath it: the grid and the quantity
        // dialog both still report as open, so driving them first means re-firing "Exchange" at a
        // window that is waiting on a Yes/No nobody is answering.
        //
        // Nothing else runs on a tick that answers a prompt: the selection underneath has already
        // been made, and re-firing it while the prompt is up is what would double-buy or cancel it.
        if (DialogueMenu.IsOpen("SelectYesno"))
        {
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                if (DialogueMenu.ConfirmYes())
                    DebugLog.Info($"{label}: confirming the purchase prompt");
            }
            return Result.InProgress;
        }

        // The Poetics tomestone grid is open -> buy the shortfall (and confirm any quantity dialog).
        if (TryDriveShop(itemId, itemName, want - have, label))
            return Result.InProgress;

        // Auriana's option list is open -> pick the Poetics exchange that opens the grid.
        if (DialogueMenu.AnyOpen())
        {
            var sig = DialogueMenu.OpenSignature();
            if (sig.Length > 0 && sig != _lastMenuSig)
            {
                DialogueMenu.LogOpenMenus($"{label} (Auriana)");
                _lastMenuSig = sig;
                _menuSince = Environment.TickCount64; // menu advanced; restart the stuck timer
            }
            if (_menuSince != 0 && Environment.TickCount64 - _menuSince > MenuStuckMs)
            {
                DebugLog.Warn($"{label}: stuck on the same Auriana menu for {MenuStuckMs / 1000}s without " +
                              $"{itemName} appearing -- the option that opens the Poetics exchange likely did not " +
                              "match. The open menu was logged above; tell me its exact wording to wire it.");
                return Result.Failed;
            }
            // Every one of her exchanges has been opened and none stocked the item.
            if (_candidates.Count > 0 && _candidateIdx >= _candidates.Count)
            {
                DebugLog.Warn($"{label}: tried every exchange Auriana offers and none listed {itemName} " +
                              $"([{string.Join(" | ", _candidates)}]). Either you are short on Poetics, or she no " +
                              "longer stocks it here -- buy it manually, then /relic start.");
                return Result.Failed;
            }

            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                TrySelectExchange(label);
            }
            return Result.InProgress;
        }

        // Nothing open: walk to Auriana (she stands behind a stall, so approach the authored front
        // spot) and interact to open her menu.
        if (_npc.Tick(NovusData.AurianaDataId, NovusData.AurianaApproachPosition, ctx, approachFromPlayerSide: true)
            == InteractionPhase.Failed)
        {
            DebugLog.Warn($"{label}: could not reach Auriana at Revenant's Toll to buy {itemName}.");
            return Result.Failed;
        }
        return Result.InProgress;
    }

    // Drive Auriana's Poetics tomestone exchange (ShopExchangeCurrency) to buy `amount` of the
    // item, confirming the quantity dialog if it appears. Returns true while either shop window is
    // open (caller stays InProgress). Throttled so a list addon is not re-fired every frame.
    private bool TryDriveShop(uint itemId, string itemName, int amount, string label)
    {
        if (TryGetAddonMaster<AddonMaster.ShopExchangeCurrencyDialog>("ShopExchangeCurrencyDialog", out var dlg)
            && dlg.IsAddonReady)
        {
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                dlg.Exchange();
                DebugLog.Info($"{label}: confirming the purchase quantity");
            }
            return true;
        }

        if (TryGetAddonMaster<AddonMaster.ShopExchangeCurrency>("ShopExchangeCurrency", out var shop)
            && shop.IsAddonReady)
        {
            if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
            {
                _lastMenuAction = Environment.TickCount64;
                var entry = shop.BasicShopItems.FirstOrDefault(x => x.ItemId == itemId);
                if (entry != null)
                {
                    // Buy the whole shortfall in one exchange. Never less than 1: a caller that is
                    // already stocked completes before reaching here.
                    var buy = Math.Max(1, amount);
                    entry.Select(buy);
                    DebugLog.Info($"{label}: selecting {buy}x {itemName} in Auriana's Poetics exchange");
                }
                else
                {
                    // Wrong category: this grid does not stock the item. Retire the entry that
                    // opened it, close it, and let the next tick re-open her menu and try the next
                    // candidate. Sitting here would just burn the stuck timer on a grid that can
                    // never have it.
                    RetireOpenCategory(shop.BasicShopItems.Count(), itemName, label);
                    DialogueMenu.FireClose("ShopExchangeCurrency");
                }
            }
            return true;
        }

        return false;
    }

    // Pick the entry in Auriana's menu that opens the exchange stocking the item. Her categories
    // are all called "Allagan Tomestones of Poetics (...)", so this works off the LIST rather than
    // a single word: rank her live entries once, then select the current candidate by its own text.
    private void TrySelectExchange(string label)
    {
        foreach (var addon in new[] { "SelectIconString", "SelectString" })
        {
            if (!DialogueMenu.IsOpen(addon))
                continue;

            if (_candidates.Count == 0)
                BuildCandidates(addon, label);
            if (_candidateIdx >= _candidates.Count)
                return; // exhausted; the caller reports it

            var choice = _candidates[_candidateIdx];
            if (DialogueMenu.SelectByTextSafe(addon, choice))
            {
                _openedWith = choice;
                DebugLog.Info($"{label}: opening Auriana's '{choice}'.");
            }
            return;
        }
    }

    // Rank Auriana's live menu entries into the order to try them: the category that looks like the
    // relic/special-arms one first, then everything else in menu order. Nothing is filtered except
    // the entries that certainly cannot be it (her map exchange, and leave/cancel), so an
    // unexpected wording still gets its turn instead of giving up on a guess.
    private void BuildCandidates(string addon, string label)
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
        DebugLog.Info($"{label}: Auriana offers {entries.Count} option(s); trying them in the order " +
                      $"[{string.Join(" | ", _candidates)}].");
    }

    // Mark the currently-open exchange as "does not stock the item" and advance to the next candidate.
    private void RetireOpenCategory(int listed, string itemName, string label)
    {
        var name = _openedWith.Length > 0 ? _openedWith : "(unknown option)";
        _openedWith = string.Empty;
        _candidateIdx++;
        // RESTART the stuck clock rather than clearing it: the next attempt deserves a fresh budget,
        // but re-opening the same menu leaves the signature unchanged, so clearing it would leave
        // the watchdog permanently disarmed and a selection that opens nothing would loop forever.
        _menuSince = Environment.TickCount64;
        _npc.Reset();     // let the interactor re-open her menu once this grid closes
        DebugLog.Info($"{label}: '{name}' lists {listed} item(s) but not {itemName}; " +
                      "closing it and trying the next exchange.");
    }

    // After the item is in the bag, close whatever Auriana window is still open so the next step's
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
            return false; // gave up closing; let the step continue (teleport handles an occupied state)
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
}
