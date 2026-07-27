using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Steps;

// Animus stage: buy the NEXT Trials of the Braves book from G'Jusana in Mor Dhona once the
// current book is finished, so the engine advances through the books instead of stopping.
// The book is granted -- and becomes the active Relic Note -- on purchase, so completion is
// simply that RelicNote.RelicNoteId advanced past the finished book.
//
// Flow: (wait to leave a duty, then teleport to Revenant's Toll if needed) -> approach
// G'Jusana -> interact -> drive the purchase menu (select the target book, confirm) -> the
// RelicNoteId advances and the step completes.
//
// SEAM: G'Jusana's exact purchase addon and option wording are not in any offline data
// source, so the target book is matched by its EventItem name (then purchase-intent needles)
// across the list addons, and each open menu is logged once (LogOpenMenus) so the real
// wording is visible if nothing matches. The step FAILS (never false-completes) if the
// purchase does not register, so a wrong needle stalls safely rather than lying -- the
// controller's failure backoff then halts with the logged menu for refinement.
public sealed class BuyRelicBookExecutor : ITaskExecutor
{
    // Grace after the G'Jusana dialogue ends for the purchase (RelicNote advance) to register.
    private const long PurchaseGraceMs = 4000;
    // Min gap between menu picks. A list addon re-fired every frame can double-select into the next
    // menu or close it before it settles (the same discipline as UpgradeRelic's menu cooldown).
    private const long MenuActionCooldownMs = 500;
    // Fail if a single G'Jusana menu stays open this long without advancing (our pick did not match).
    private const long MenuStuckMs = 10000;

    public StepType Handles => StepType.BuyRelicBook;

    private enum Phase { WaitExit, Teleport, Interact, Blocked }

    private readonly AetheryteTeleportExecutor _teleport = new();
    private readonly NpcInteractor _npc = new();

    private Phase _phase;
    private byte _completedBook;
    private uint _targetBook;
    private string _targetName = string.Empty;
    private StepData? _teleStep;
    private string _lastMenuSig = string.Empty;
    private long _doneDeadline;
    private long _lastMenuAction;
    private long _menuSince;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _completedBook = GameState.ActiveRelicNoteId();
        (_targetBook, _targetName) = AnimusBookData.NextBook(_completedBook);
        // Repeat-relic restart: the finished note is the LAST book (no next row), but the Animus
        // umbrella quest ("Trials of the Braves", once-ever) is already complete, so this is a fresh
        // Atma weapon carrying a previous relic's stale note -- start its own run at book 1. The
        // controller only creates this objective when it has decided to wrap (guarded per-weapon), so
        // mirroring the target here is safe; the executor re-derives it because it reads the live note.
        if (_targetBook == 0 && _completedBook != 0
            && ZodiacQuestRegistry.MainFor(RelicStage.Animus) is { } animusQuest
            && GameState.IsQuestComplete(animusQuest.QuestId))
            (_targetBook, _targetName) = AnimusBookData.NextBook(0);
        _teleStep = null;
        _lastMenuSig = string.Empty;
        _doneDeadline = 0;
        _lastMenuAction = 0;
        _menuSince = 0;

        if (AnimusBookData.GJusanaNpcId == 0 || _targetBook == 0)
        {
            // No vendor resolved or no next book row -> nothing to do; Update fails with guidance.
            _phase = Phase.Blocked;
            return;
        }

        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        DebugLog.Info($"Buy relic book: finished book {_completedBook}; buying next book {_targetBook} " +
                      $"('{(_targetName.Length > 0 ? _targetName : "?")}') from G'Jusana in Mor Dhona.");

        // Coming straight off a book dungeon's last boss, the player can still be bound by the
        // instance (the duty-complete / eject window). Teleport is blocked there, so wait for the
        // overworld before starting the trip rather than firing teleports from inside.
        if (BoundByDuty())
        {
            _phase = Phase.WaitExit;
            return;
        }

        StartTrip(ctx);
    }

    private void StartTrip(ExecutionContext ctx)
    {
        if (AnimusBookData.MorDhonaAetheryte != 0)
        {
            _teleStep = new StepData { Type = StepType.AetheryteTeleport, AetheryteId = AnimusBookData.MorDhonaAetheryte };
            _teleport.Start(_teleStep, ctx);
            _phase = Phase.Teleport;
        }
        else
        {
            // No aetheryte resolved; rely on navigation from wherever we are.
            _npc.Reset();
            _phase = Phase.Interact;
        }
    }

    // Still inside a duty instance (any bound-by-duty flag) -> teleport is blocked.
    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        // Authoritative completion: the active Relic Note is no longer the finished book -- the
        // purchase granted the target book and made it current. "Different", not "greater": a repeat
        // relic wraps from the last book back to book 1 (a DECREASE); the != 0 guard ignores a
        // transient no-note read.
        if (GameState.ActiveRelicNoteId() != _completedBook && GameState.ActiveRelicNoteId() != 0)
        {
            DebugLog.Info($"Relic Note advanced to {GameState.ActiveRelicNoteId()}; book purchase complete.");
            return ExecutorStatus.Complete;
        }

        switch (_phase)
        {
            case Phase.WaitExit:
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                StartTrip(ctx);
                return ExecutorStatus.InProgress;

            case Phase.Teleport:
                var t = _teleport.Update(_teleStep!, ctx);
                if (t == ExecutorStatus.Failed)
                    return ExecutorStatus.Failed;
                if (t == ExecutorStatus.Complete)
                {
                    _teleport.Stop(ctx);
                    _npc.Reset();
                    _phase = Phase.Interact;
                }
                return ExecutorStatus.InProgress;

            case Phase.Interact:
                var p = _npc.Tick(AnimusBookData.GJusanaNpcId, AnimusBookData.GJusanaPosition, ctx);
                if (p == InteractionPhase.Failed)
                    return ExecutorStatus.Failed;

                // Drive the purchase menu whenever a list menu is open, even if the interactor
                // reports the conversation "done" (a shop/exchange picker can linger as a
                // SelectString after the NPC event ends, as Remon's sign picker does).
                if (DialogueMenu.AnyOpen())
                {
                    var sig = DialogueMenu.OpenSignature();
                    if (sig.Length > 0 && sig != _lastMenuSig)
                    {
                        DialogueMenu.LogOpenMenus("Buy relic book (G'Jusana)");
                        DebugLog.Info($"Buy relic book: target='{_targetName}' (book {_targetBook})");
                        _lastMenuSig = sig;
                        _menuSince = Environment.TickCount64; // the menu advanced; restart the stuck timer
                    }

                    // Stuck detector: if the SAME menu stays open (sig unchanged) for this long while
                    // we keep trying to pick, our option did not advance it -- the wording did not
                    // match, or "Exchange" is not the right entry. Without this the step would loop
                    // on that menu forever (AnyOpen stays true, so the Done-timeout below never runs)
                    // with no failure. Fail so the logged menu can be wired and the backoff halts.
                    if (_menuSince != 0 && Environment.TickCount64 - _menuSince > MenuStuckMs)
                    {
                        DebugLog.Warn(
                            $"Buy relic book: stuck on the same G'Jusana menu for {MenuStuckMs / 1000}s without it " +
                            $"advancing (target '{_targetName}'). The pick is not opening the next menu -- the option " +
                            $"wording likely does not match. Open menu: {DialogueMenu.OpenSignature()}");
                        return ExecutorStatus.Failed;
                    }

                    // Throttle the picks so a list addon is not re-fired every frame (which can
                    // double-select into the next menu or close it before it settles).
                    if (Environment.TickCount64 - _lastMenuAction >= MenuActionCooldownMs)
                    {
                        _lastMenuAction = Environment.TickCount64;
                        TrySelectBook();
                        DialogueMenu.ConfirmYes();
                    }
                    _doneDeadline = 0; // a menu is still up; the purchase is not finished
                    return ExecutorStatus.InProgress;
                }

                if (p == InteractionPhase.Done)
                {
                    // No menu open and the conversation ended; allow a moment for the purchase to
                    // register (the top-of-Update RelicNote check completes the step the instant it does).
                    if (_doneDeadline == 0)
                        _doneDeadline = Environment.TickCount64 + PurchaseGraceMs;
                    else if (Environment.TickCount64 > _doneDeadline)
                    {
                        DebugLog.Warn(
                            $"Buy relic book: G'Jusana's dialogue ended but the Relic Note did not advance " +
                            $"(still book {_completedBook}). The purchase option for '{_targetName}' was not " +
                            "matched, it costs a currency you are short on, or G'Jusana is not the vendor. The " +
                            "open menu was logged above -- tell me its exact wording to wire the option.");
                        return ExecutorStatus.Failed;
                    }
                }

                return ExecutorStatus.InProgress;

            default: // Blocked
                DebugLog.Warn(AnimusBookData.GJusanaNpcId == 0
                    ? "Buy relic book: could not resolve G'Jusana's NPC id, so the next book cannot be auto-bought. " +
                      "Buy it from G'Jusana in Mor Dhona and equip it, then /relic start."
                    : "Buy relic book: no next book row after the finished book -- the final Animus weapon upgrade " +
                      "is a separate step that is not yet automated. Finish it manually, then /relic start.");
                return ExecutorStatus.Failed;
        }
    }

    // Drive G'Jusana's book-purchase menus. The tree is confirmed from the game's own event script
    // (CustomTalk CmnDefRelicWeapon025GetNote), not just from observed logs:
    //   1) SelectIconString: "Trials of the Braves Exchange" (buy) / "... Disposal" / "..." / etc.
    //   2) SelectString "What are you interested in?":
    //      - Most jobs: the four element groups -- "The Books of Fire", "The Books of Fall",
    //        "The Books of Wind", "The Books of Earth" -- each serving its next numbered book.
    //      - Paladin only: first a weapon split "Books pertaining to swords." (Curtana: the Sky*
    //        books, reached via the element groups) / "Books pertaining to shields." (Holy Shield),
    //        and the shield branch then lists its two books by SINGULAR name, "The Book of
    //        Netherfire" / "The Book of Netherfall" -- no roman numeral, no "The Books of" group.
    //   3) Yes-No confirm.
    // So we match, in order: the swords/shields split (Sky -> sword, Nether -> shield), then the
    // element group whose element the target name contains ("Book of Skyfall I" -> "The Books of
    // Fall"), then a specific book by core name + numeral -- the last is what reaches the Paladin
    // shield sub-menu, whose "The Book of Netherfall" matches neither a group nor the item name.
    private void TrySelectBook()
    {
        // First menu: the "Trials of the Braves Exchange" (buy) icon option. Use ECommons'
        // AddonMaster.SelectIconString, which reads the real PopupMenu entries and fires the correct
        // ENTRY INDEX -- a raw AtkValue-string-ordinal FireCallback does NOT reliably map to a
        // SelectIconString's callback index, so the Exchange pick misfired and closed the menu
        // instead of opening the book list (the reported "not collecting the book"). Prefer the
        // Braves "exchange" entry so "Disposal" / "Nothing" is never chosen.
        if (TryGetAddonMaster<AddonMaster.SelectIconString>("SelectIconString", out var icon) && icon.IsAddonReady)
        {
            foreach (var e in icon.Entries)
            {
                var text = e.Text ?? string.Empty;
                if (text.Contains("exchange", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("braves", StringComparison.OrdinalIgnoreCase))
                {
                    e.Select();
                    return;
                }
            }
            foreach (var e in icon.Entries)
                if ((e.Text ?? string.Empty).Contains("exchange", StringComparison.OrdinalIgnoreCase))
                {
                    e.Select();
                    return;
                }
        }

        // PLD only: G'Jusana's book menu has an extra level for the Paladin, whose two relics use
        // different book sets -- after the "Trials of the Braves Exchange" pick it splits into
        // "Books pertaining to swords" (Curtana: Sky* books) and "Books pertaining to shields"
        // (Holy Shield: Nether* books) BEFORE the element groups. Pick the branch matching the
        // target book's family (Nether -> shields, Sky -> swords). Inert for every non-PLD relic
        // (no such menu), and safe-fail: a wrong/absent pick is caught by the stuck detector.
        // SEAM (offline-unverifiable): the exact branch wording and whether the RelicNote sheet's
        // book name carries the Sky/Nether family for the off-hand relic are unconfirmed in-game.
        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var split) && split.IsAddonReady)
        {
            var wantShield = _targetName.Contains("nether", StringComparison.OrdinalIgnoreCase);
            var wantSword = _targetName.Contains("sky", StringComparison.OrdinalIgnoreCase);
            if (wantShield || wantSword)
                foreach (var e in split.Entries)
                {
                    var t = e.Text ?? string.Empty;
                    // Only a swords/shields BRANCH line, never an element group ("The Books of Fire"),
                    // which also contains "book" but neither "sword"/"shield" nor "pertaining".
                    var isBranch = t.Contains("pertaining", StringComparison.OrdinalIgnoreCase)
                        || (t.Contains("book", StringComparison.OrdinalIgnoreCase)
                            && (t.Contains("sword", StringComparison.OrdinalIgnoreCase)
                                || t.Contains("shield", StringComparison.OrdinalIgnoreCase)));
                    if (!isBranch)
                        continue;
                    if ((wantShield && t.Contains("shield", StringComparison.OrdinalIgnoreCase))
                        || (wantSword && t.Contains("sword", StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Select();
                        return;
                    }
                }
        }

        // The element-group / specific-book SelectString carries a leading prompt line, so the
        // callback index is NOT the string ordinal; use ECommons' AddonMaster.SelectString (as
        // LeveBoard does), which selects the real option. The open menu is either the element-group
        // list or a concrete-book list (the Paladin shield sub-menu); each matcher below is inert on
        // the other -- the group matcher needs the plural "The Books of", the book matcher needs the
        // singular "Book of <name>" -- so running both in order handles whichever one is up.
        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var m) && m.IsAddonReady)
        {
            // The element group the target belongs to: "Book of Skyfall I" -> "The Books of Fall".
            // Match the group's element word against the target book name.
            foreach (var e in m.Entries)
            {
                var element = ExtractGroupElement(e.Text ?? string.Empty);
                if (element.Length > 0 && _targetName.Contains(element, StringComparison.OrdinalIgnoreCase))
                {
                    e.Select();
                    return;
                }
            }

            // A specific book entry, matched by core name + roman numeral. This is what reaches the
            // Paladin shield sub-menu, whose entries are the SINGULAR, numeral-less "The Book of
            // Netherfire" / "The Book of Netherfall": those match neither an element group nor a
            // verbatim compare with the item name ("Book of Netherfall I"), which is why the shield
            // books stalled. Also robust to any decorated per-book picker ("Book of Skyfall II
            // (Raises Vitality). Completed: ...").
            var (targetCore, targetNum) = ParseBookName(_targetName);
            if (targetCore.Length > 0)
                foreach (var e in m.Entries)
                {
                    var (entryCore, entryNum) = ParseBookName(e.Text ?? string.Empty);
                    if (entryCore.Length == 0
                        || !entryCore.Equals(targetCore, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // The numeral must agree only when BOTH sides carry one; a numeral-less entry
                    // ("The Book of Netherfall") matches the numbered target book.
                    if (targetNum.Length > 0 && entryNum.Length > 0
                        && !entryNum.Equals(targetNum, StringComparison.OrdinalIgnoreCase))
                        continue;
                    e.Select();
                    return;
                }
        }
    }

    // "The Books of Fall (Raises Vitality). Completed: 0 of 3" -> "Fall". Empty when the line is not
    // a "The Books of <Element>" group entry (e.g. the prompt or "Nothing").
    private static string ExtractGroupElement(string label)
    {
        const string prefix = "The Books of ";
        var i = label.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return string.Empty;
        var rest = label[(i + prefix.Length)..];
        var end = rest.IndexOfAny(new[] { '(', '.' });
        return (end > 0 ? rest[..end] : rest).Trim();
    }

    // Split a book line into its core name and trailing roman numeral. Handles the item-name forms
    // ("Book of Netherfall I", "copy of the Book of Netherfall I" -> ("Netherfall", "I")) and the
    // menu forms ("The Book of Netherfall (Raises Vitality). Completed: 0 of 2" -> ("Netherfall",
    // ""), "Book of Skyfall II. Completed: ..." -> ("Skyfall", "II")). Empty core when the line is
    // not a single-book line: an element group "The Books of Fall" has the PLURAL "Books of", which
    // never contains the singular "Book of ", so a group header is skipped here (and vice versa).
    private static (string Core, string Numeral) ParseBookName(string text)
    {
        var t = text ?? string.Empty;
        const string marker = "Book of ";
        var i = t.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return (string.Empty, string.Empty);
        var rest = t[(i + marker.Length)..];
        var end = rest.IndexOfAny(new[] { '(', '.', ',' });
        rest = (end >= 0 ? rest[..end] : rest).Trim();
        var numeral = string.Empty;
        var sp = rest.LastIndexOf(' ');
        if (sp > 0 && IsRomanNumeral(rest[(sp + 1)..]))
        {
            numeral = rest[(sp + 1)..].ToUpperInvariant();
            rest = rest[..sp].Trim();
        }
        return (rest, numeral);
    }

    // A short run of I/V/X (the book numerals only go up to III), so a trailing "I"/"II"/"III" is
    // split off as the numeral instead of being folded into the core name.
    private static bool IsRomanNumeral(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (var c in s)
            if ("IVXivx".IndexOf(c) < 0)
                return false;
        return true;
    }

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
