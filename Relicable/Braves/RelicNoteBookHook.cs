using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Relicable.Diagnostics;
using LiveRelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace Relicable.Braves;

// Book click-to-travel helper: clicking a target row in the in-game Trials of the Braves book (the
// "RelicNoteBook" addon) flags that objective on the map and teleports to its zone. We listen for the
// addon's button click via IAddonLifecycle (PostReceiveEvent, so the game's own row selection runs
// first and we only add to it), identify the clicked enemy/dungeon/FATE/leve slot by matching the
// event's target node against the row check boxes -- the only reliable way to identify the row -- and
// hand off to BraveBookNavigator. Gated by Configuration.BookClickNavigate.
internal sealed unsafe class RelicNoteBookHook : IDisposable
{
    private const string AddonName = "RelicNoteBook";
    private readonly Configuration _config;

    public RelicNoteBookHook(Configuration config)
    {
        _config = config;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, AddonName, OnReceiveEvent);
    }

    public void Dispose()
        => Plugin.AddonLifecycle.UnregisterListener(OnReceiveEvent);

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!_config.BookClickNavigate)
            return;
        try
        {
            if (args is not AddonReceiveEventArgs r)
                return;
            if ((AtkEventType)r.AtkEventType != AtkEventType.ButtonClick)
                return;
            HandleClick((AddonRelicNoteBook*)args.Addon.Address, (AtkEvent*)r.AtkEvent);
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"RelicNoteBook hook: {ex.Message}");
        }
    }

    private void HandleClick(AddonRelicNoteBook* addon, AtkEvent* ev)
    {
        if (addon == null || ev == null || addon->CategoryList == null)
            return;
        var note = LiveRelicNote.Instance();
        if (note == null)
            return;

        var bookId = note->RelicNoteId;
        var tab = addon->CategoryList->SelectedItemIndex;
        var slot = MatchSlot(addon, tab, ev->Target);
        if (slot < 0)
            return;

        // Flag + teleport (dungeons open the Duty Finder). Teleport is the intent of the click;
        // the toggle above lets a user turn the whole helper off.
        BraveBookNavigator.Go(bookId, tab, slot, teleport: true);
    }

    // Which slot of the active tab a click landed on, or -1 for a non-row click (tab switch, close,
    // etc.). Matches the event target against each row's check box owner node.
    private static int MatchSlot(AddonRelicNoteBook* a, int tab, AtkEventTarget* t)
    {
        switch (tab)
        {
            case BraveBookNavigator.TabEnemies:
                if (Owns(t, a->Enemy0.CheckBox)) return 0;
                if (Owns(t, a->Enemy1.CheckBox)) return 1;
                if (Owns(t, a->Enemy2.CheckBox)) return 2;
                if (Owns(t, a->Enemy3.CheckBox)) return 3;
                if (Owns(t, a->Enemy4.CheckBox)) return 4;
                if (Owns(t, a->Enemy5.CheckBox)) return 5;
                if (Owns(t, a->Enemy6.CheckBox)) return 6;
                if (Owns(t, a->Enemy7.CheckBox)) return 7;
                if (Owns(t, a->Enemy8.CheckBox)) return 8;
                if (Owns(t, a->Enemy9.CheckBox)) return 9;
                break;
            case BraveBookNavigator.TabDungeons:
                if (Owns(t, a->Dungeon0.CheckBox)) return 0;
                if (Owns(t, a->Dungeon1.CheckBox)) return 1;
                if (Owns(t, a->Dungeon2.CheckBox)) return 2;
                break;
            case BraveBookNavigator.TabFates:
                if (Owns(t, a->Fate0.CheckBox)) return 0;
                if (Owns(t, a->Fate1.CheckBox)) return 1;
                if (Owns(t, a->Fate2.CheckBox)) return 2;
                break;
            case BraveBookNavigator.TabLeves:
                if (Owns(t, a->Leve0.CheckBox)) return 0;
                if (Owns(t, a->Leve1.CheckBox)) return 1;
                if (Owns(t, a->Leve2.CheckBox)) return 2;
                break;
        }
        return -1;
    }

    private static bool Owns(AtkEventTarget* target, AtkComponentCheckBox* checkbox)
        => checkbox != null && (nint)target == (nint)checkbox->AtkComponentButton.OwnerNode;
}
