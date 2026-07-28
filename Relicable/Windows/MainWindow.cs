using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;          // FontAwesomeIcon (settings cog)
using ECommons.ImGuiMethods;      // ImGuiEx.IconButton
using Relicable.BaseRelic;
using Relicable.Controllers;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Windows;

// Progress and control window: active stage/objective/step, a progress bar for the
// current objective (read from game state), and start/stop with a clear message
// when a required dependency is missing.
public sealed class MainWindow : Window
{
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Grey = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.35f, 1f);

    private readonly RelicController _controller;
    private readonly Configuration _config;
    private readonly NavmeshIpc _navmesh;
    private readonly ArtisanCraftingList _artisanLists;
    private readonly Licensing.AlphaGate _alphaGate;
    private readonly Action _openNovus;
    private readonly Action _openBraves;
    private readonly Action _openQuestmap;
    private readonly Action _saveConfig;
    private readonly Action _openConfig;
    private string _startError = string.Empty;

    public MainWindow(
        RelicController controller, Configuration config, NavmeshIpc navmesh,
        ArtisanCraftingList artisanLists, Licensing.AlphaGate alphaGate,
        Action openNovus, Action openBraves, Action openQuestmap, Action saveConfig, Action openConfig)
        : base("Relicable")
    {
        _controller = controller;
        _config = config;
        _navmesh = navmesh;
        _artisanLists = artisanLists;
        _alphaGate = alphaGate;
        _openNovus = openNovus;
        _openBraves = openBraves;
        _openQuestmap = openQuestmap;
        _saveConfig = saveConfig;
        _openConfig = openConfig;
    }

    // Human-readable run status: the controller's state machine names (RunStep,
    // SelectObjective) are internal, not something to show a user.
    private static string StatusLabel(RelicController.State state) => state switch
    {
        RelicController.State.Idle => "Idle",
        RelicController.State.SelectStage or RelicController.State.SelectObjective => "Planning",
        RelicController.State.RunStep => "Running",
        RelicController.State.Stopped => "Stopped",
        _ => state.ToString(),
    };

    // First authored world coordinate among an objective's steps -- the spawn / anchor the run
    // itself travels to. Used to flag and travel when the objective name is clicked. Null when
    // the objective has no authored position (nothing to flag).
    private static Vector3? FirstAuthoredSpot(RelicObjective obj)
    {
        foreach (var s in obj.Steps)
            if (s.Position is { } p)
                return p;
        return null;
    }

    public override void Draw()
    {
        ImGui.Text($"Status: {StatusLabel(_controller.Current)}");

        // Settings cog, right-aligned on the header line: opens the config window (same as
        // /relic config). Right-align by pushing the cursor to the far edge of the content region.
        var cog = ImGui.GetFrameHeight();
        ImGui.SameLine();
        var pad = ImGui.GetContentRegionAvail().X - cog;
        if (pad > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
        if (ImGuiEx.IconButton(FontAwesomeIcon.Cog, "relicSettings", new Vector2(cog, cog)))
            _openConfig();
        Ui.Tooltip("Open Relicable settings.");

        // The relic stage: the equipped weapon's tier (base/Unfinished/Zenith -> Relic, then
        // Atma..Zeta and Braves) when a relic weapon is on, otherwise resolved to the first
        // stage (Relic) from the active A Relic Reborn quest id, so a player mid base-relic with
        // nothing equipped to identify it is not shown as "none detected". Shown even when idle.
        var stage = BaseRelicState.EffectiveStage();
        if (stage == RelicStage.None)
            ImGui.Text("Relic Stage: none detected");
        else if (BaseRelicState.StageResolvedFromQuest())
            ImGui.Text("Relic Stage: Relic (from A Relic Reborn quest)");
        else
            ImGui.Text($"Relic Stage: {stage}");

        // Quest-aware "where are you on the whole line" line: reads the accepted Zodiac
        // quest across every stage (Up in Arms, Trials of the Braves, ... Rise and Shine),
        // so a player past the base relic sees their real position -- the weapon-tier line
        // above cannot distinguish a Zenith held mid-Atma from the base Relic stage. Full
        // per-stage breakdown is /relic quests. See ZodiacQuestState.
        ImGui.Text($"Position: {ZodiacQuestState.CurrentPositionLine()}");

        // Atma stage delegated to CBT's Fate Tool Kit (Configuration.AtmaBackend): show the
        // driver's live status/guidance (farming progress; at 12/12 the delegation ends and the
        // engine's own atma-upgrade objective takes over the Jalzahn enhancement).
        var atmaStatus = _controller.AtmaDelegationStatus;
        if (!string.IsNullOrEmpty(atmaStatus))
            ImGui.TextWrapped(atmaStatus);

        // Built-in Atma farm: how many of the CURRENT zone's atma are held against the per-zone
        // target (config "Atmas per zone, then move on"), so the zone change is not a surprise.
        var atmaZone = _controller.AtmaZoneProgress;
        if (!string.IsNullOrEmpty(atmaZone))
            ImGui.TextWrapped(atmaZone);

        // Co-run conflict: CBT's Fate Tool Kit is grinding FATEs at the same time as Relicable, so
        // Relicable has stepped aside to stop them fighting over movement. Tell the user how to
        // reclaim control or coordinate the two.
        if (_controller.CbtFateToolKitConflict)
        {
            Ui.Wrapped(Red, "CBT's Fate Tool Kit is running, so Relicable has stepped aside to avoid fighting it for movement.");
            Ui.Wrapped(Grey, "Disable the Fate Tool Kit, or set the Atma backend to it in Settings so the two coordinate.");
        }

        // When any finished base relic is held -- equipped, in the armoury chest, or in a
        // bag; it does not need to be on -- the NEXT step for it is the Zenith item gate
        // (3x Thavnairian Mist at the Furnace). It is a pure item gate with no quest, so it
        // is surfaced here as guidance rather than driven by objective selection.
        if (BaseRelicState.NeedsZenith())
            DrawZenithNextStep();

        // Atma weapon equipped but no book yet -> the Animus stage's first step is buying the
        // first Trials of the Braves book from G'Jusana. The controller auto-buys it on Start.
        if (BaseRelicState.NeedsFirstBook())
            DrawFirstBookNextStep();

        // A Relic Reborn part 2 (the melded class weapon): the one base-relic step that cannot be
        // automated, so while the quest sits on it the annotated step is shown here with its
        // market-board search links, travel button, and Artisan crafting list.
        DrawClassWeaponNextStep();

        DrawLightTracker();
        DrawMahatmaTracker();

        var obj = _controller.ActiveObjective;
        if (obj != null)
        {
            ImGui.Text($"Stage: {obj.Stage}");

            // Objective name is clickable. A book-stage (Trials of the Braves / Animus) objective
            // lives in the in-game relic note book, so clicking it OPENS that book -- where each
            // enemy/dungeon/FATE/leve entry is itself click-to-flag (RelicNoteBookHook). Other
            // objectives flag their authored spot on the map and travel there instead.
            var isBookObjective = obj.Completion.Kind is CompletionKind.MonsterSlot
                or CompletionKind.DungeonSlot or CompletionKind.FateSlot or CompletionKind.LeveSlot;
            // The three Jalzahn enhancements (Zenith->Atma, Atma->Animus, Novus->Nexus) are done at
            // one NPC; clicking flags him, teleports to Fallgourd Float, and flies there.
            var isJalzahnUpgrade = obj.Completion.Kind is CompletionKind.AtmaUpgraded
                or CompletionKind.AnimusUpgraded or CompletionKind.NexusUpgraded;
            // Any other objective that carries a zone AND an authored world spot (today only the
            // base-relic beastmen hunt): clicking drops the in-game map flag there and travels to
            // it -- the same flag + teleport + fly the Jalzahn line uses. Null (no zone or no
            // authored coordinate) leaves the name inert, with no tooltip promising otherwise.
            var travelSpot = !isBookObjective && !isJalzahnUpgrade && obj.Territory != 0
                ? FirstAuthoredSpot(obj)
                : null;
            ImGui.TextUnformatted("Objective:");
            ImGui.SameLine();
            if (ImGui.Selectable(obj.DisplayName, false))
            {
                if (isBookObjective)
                    GameActions.OpenRelicNoteBook();
                else if (isJalzahnUpgrade)
                    LocationNavigator.GoWorld(Data.NexusData.JalzahnTerritory, Data.NexusData.JalzahnPosition);
                else if (travelSpot is { } spot)
                    LocationNavigator.GoWorld(obj.Territory, spot);
            }
            if (isBookObjective)
                Ui.Tooltip("Opens the Trials of the Braves book.\nClick any entry inside it to flag the target on the map and travel there.");
            else if (isJalzahnUpgrade)
                Ui.Tooltip("Flags Jalzahn (Hyrstmill, North Shroud) and teleports you to him.\nPick the enhancement from his menu, or press Start to automate it.");
            else if (travelSpot != null)
                Ui.Tooltip(string.IsNullOrEmpty(obj.TargetName)
                    ? "Flags this objective's location on the map and teleports you there."
                    : $"Flags {obj.TargetName} on the map and teleports you there.");

            ImGui.Text($"Step: {_controller.ActiveStepIndex + 1} / {obj.Steps.Count}");
            DrawObjectiveProgress(obj);
        }
        else
        {
            ImGui.TextDisabled("No active objective.");
        }

        ImGui.Separator();

        if (ImGui.Button("Start"))
        {
            if (_controller.Start())
            {
                _startError = string.Empty;
            }
            else
            {
                var missing = _controller.MissingRequiredDependencies();
                _startError = "Missing required plugins: " + string.Join(", ", missing);
                // The usual "it still wants RSR" cause: the combat backend is set to a plugin
                // you do not have. Point at the setting rather than just naming the plugin.
                foreach (var m in missing)
                    if (m.Contains("Rotation Solver") || m.Contains("BossMod") || m.Contains("Wrath"))
                    {
                        _startError += "\nCombat backend needs it: switch it in Config (Combat backend) to BossMod Reborn, Rotation Solver Reborn or Wrath Combo to match what you have installed.";
                        break;
                    }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            _controller.Stop();
            _startError = string.Empty;
        }

        if (_startError.Length > 0)
            ImGui.TextColored(Red, _startError);

        ImGui.Separator();
        DrawStageSelection();

        ImGui.Separator();
        DrawNavmeshControls();

        // Atma tracker: a collapsible 4x3 grid of the twelve atmas at the bottom, shown only
        // while working the Atma stage. Each cell references both your bags and your retainers.
        if (ShouldShowAtmaTracker())
        {
            ImGui.Separator();
            DrawAtmaTracker();
        }

        DrawAlphaFooter();
    }

    // Early Alpha attribution footer.
    //
    // This is the anti-sharing mechanism, not decoration: the name the running code was
    // issued to is displayed to whoever is using it. A code passed on to someone else
    // keeps showing the name of the person it was issued to, on their screen, in every
    // screenshot and stream. Do not make this hideable.
    private void DrawAlphaFooter()
    {
        if (!_alphaGate.Unlocked)
            return;

        ImGui.Separator();

        var license = _alphaGate.License;
        ImGui.TextColored(Grey, $"Early Alpha — access: {license.Owner}");

        var days = license.DaysRemaining(DateTime.UtcNow);
        if (_alphaGate.ExpiringSoon)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, $"({days} day{(days == 1 ? "" : "s")} left)");
            Ui.Tooltip($"This access code expires on {license.Expires:yyyy-MM-dd}.\nAsk the developer for a renewal before then.");
        }
        else
        {
            Ui.Tooltip($"Issued to {license.Owner}. Expires {license.Expires:yyyy-MM-dd} ({days} days left).");
        }
    }

    // True while the player is working the Atma stage (or has Manual pinned to Atma), so the
    // Atma tracker is offered at the bottom of the window.
    private bool ShouldShowAtmaTracker()
        => ZodiacQuestState.CurrentStage() == RelicStage.Atma
           || (_config.StageMode == StageSelectionMode.Manual && _config.ManualStage == RelicStage.Atma);

    // Collapsible Atma tracker: a 4-column x 3-row grid of the twelve atmas, each showing whether
    // you hold it in your own bags and/or on a retainer. Retainer counts come from the cached
    // summoning-bell scan (AutoRetainer's IPC cannot report retainer inventory), so they reflect
    // the last time a retainer was open. Collapsed by default.
    private void DrawAtmaTracker()
    {
        var atmas = GameState.AtmaItemIds;
        // Per-zone target (config "Atmas per zone, then move on"): a zone is done once you hold this
        // many of its atma. At the default 1 this is exactly the old "do you have it" tracker.
        var target = Math.Max(1, _config.AtmaPerZone);
        var have = 0;
        foreach (var atmaId in atmas)
            if (GameState.InventoryCount(atmaId) >= target)
                have++;

        // Fixed ### id so the live count in the label cannot reset the open/closed state.
        var targetLabel = target > 1 ? $" at {target}x" : string.Empty;
        if (!ImGui.CollapsingHeader($"Atma Tracker ({have}/{atmas.Count}{targetLabel})###atmaTracker"))
            return;

        ImGui.TextColored(Grey, target > 1
            ? $"Green = zone done ({target} held), yellow = partial or on a retainer, grey = none."
            : "Green = in your bags, yellow = on a retainer, grey = missing.");
        ImGui.TextColored(Grey, "Click an atma to teleport to its farm zone.");
        Ui.Tooltip("Retainer counts come from the summoning bell and reflect the last time each retainer was opened.");

        ImGui.Columns(4, "atmaTrackerGrid", false);
        for (var i = 0; i < atmas.Count; i++)
        {
            var id = atmas[i];
            var local = GameState.InventoryCount(id);
            var retainer = _config.RetainerAtmas.TotalFor(id);

            var full = GameState.ItemName(id);
            var sign = StripAtmaPrefix(full);
            if (string.IsNullOrEmpty(sign))
                sign = $"Atma {i + 1}";

            // Green once the per-zone target is met (identical to the old "held" rule at target 1),
            // yellow while partially held or sitting on a retainer, grey when none.
            var color = local >= target ? Green : local > 0 || retainer > 0 ? Yellow : Grey;
            ImGui.BeginGroup();
            ImGui.TextColored(color, sign);
            ImGui.TextColored(Grey, target > 1 ? $"bags {local}/{target} | ret {retainer}" : $"bags {local} | ret {retainer}");
            ImGui.EndGroup();

            // Click-to-teleport to this atma's farm zone. The destination is that zone's own farm
            // objective aetheryte (RelicController.AetheryteForAtma), so it lands exactly where the
            // built-in farm would start. CLICK, not hover: the grid is twelve cells wide and a mouse
            // simply crossing it would fire a run of teleports (and burn the gil for them).
            var aetheryte = _controller.AetheryteForAtma(id);
            var where = Data.Locations.AetheryteLabel(aetheryte);
            if (ImGui.IsItemHovered())
            {
                var status = local >= target ? "This zone is done."
                    : local > 0 ? $"{local} of {target} in your bags."
                    : retainer > 0 ? "Held on a retainer." : "Not obtained yet.";
                var travel = aetheryte == 0
                    ? "No farm zone is loaded for this atma."
                    : $"Click to teleport to {(string.IsNullOrEmpty(where) ? "its farm zone" : where)}.";
                Ui.Tooltip($"{(string.IsNullOrEmpty(full) ? sign : full)}\nBags: {local}   Retainers: {retainer}\n{status}\n{travel}");
            }
            if (aetheryte != 0 && ImGui.IsItemClicked())
            {
                GameActions.TeleportToAetheryte(aetheryte);
                Diagnostics.DebugLog.Info($"Atma tracker: teleporting to {(string.IsNullOrEmpty(where) ? $"aetheryte {aetheryte}" : where)} " +
                              $"for {(string.IsNullOrEmpty(full) ? sign : full)}.");
            }
            ImGui.NextColumn();
        }
        ImGui.Columns(1);
    }

    // "Atma of the Maiden" -> "Maiden"; returns the name unchanged when the prefix is absent.
    private static string StripAtmaPrefix(string name)
    {
        const string prefix = "Atma of the ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name.Substring(prefix.Length) : name;
    }

    // Book work: which KINDS of Trials of the Braves slot the engine may work, plus a list of the
    // active book's remaining slots so one can be pushed to the front.
    //
    // Only drawn while a book is actually in hand (a live RelicNote): outside the Animus stage
    // there are no slots and nothing here would mean anything. Auto is unchanged from before this
    // existed, so the section is purely additive for anyone who never opens it.
    private void DrawBookWorkSelection()
    {
        var slots = _controller.IncompleteBookSlots();
        if (slots.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("Book work");

        var mode = (int)_config.BookWorkMode;
        if (ImGui.RadioButton("Auto##bookwork", ref mode, (int)BookWorkSelectionMode.Auto))
        {
            _config.BookWorkMode = BookWorkSelectionMode.Auto;
            _saveConfig();
            _controller.Replan();
        }
        Ui.Tooltip("Work every kind of book entry, in the usual order: enemies, then leves, then dungeons, then FATEs.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Manual##bookwork", ref mode, (int)BookWorkSelectionMode.Manual))
        {
            _config.BookWorkMode = BookWorkSelectionMode.Manual;
            _saveConfig();
            _controller.Replan();
        }
        Ui.Tooltip("Only work the kinds you tick below. Everything else in the book is left alone.");

        if (_config.BookWorkMode == BookWorkSelectionMode.Manual)
        {
            DrawKindToggle("Enemies", BookWorkKinds.Enemies, "Hunt the book's enemy entries.");
            ImGui.SameLine();
            DrawKindToggle("Leves", BookWorkKinds.Leves, "Run the book's levequests. These spend leve allowances.");
            DrawKindToggle("Dungeons", BookWorkKinds.Dungeons, "Queue and clear the book's dungeons (needs AutoDuty).");
            ImGui.SameLine();
            DrawKindToggle("FATEs", BookWorkKinds.Fates, "Wait for and clear the book's FATEs.");

            if (_config.BookWorkKinds == BookWorkKinds.None)
                Ui.Wrapped(Yellow, "Nothing is ticked, so there is no book work to do. The engine will ignore this " +
                                   "filter rather than stop, until you tick something or switch back to Auto.");
        }

        // The remaining slots of the book in hand. Clicking one runs it next, ahead of the normal
        // order -- including a kind that is currently unticked, which is the point of the list.
        if (ImGui.TreeNode($"Remaining in this book ({slots.Count})###bookslots"))
        {
            foreach (var slot in slots)
            {
                var kind = KindLabel(slot.Completion.Kind);
                var enabled = _config.BookWorkMode != BookWorkSelectionMode.Manual
                              || KindAllowed(slot.Completion.Kind);

                if (ImGui.SmallButton($"Run next##{slot.Id}"))
                    _controller.RunObjectiveNow(slot.Id);
                ImGui.SameLine();
                ImGui.TextColored(enabled ? Grey : Yellow, $"[{kind}]");
                ImGui.SameLine();
                Ui.Wrapped(enabled ? Grey : Yellow, slot.DisplayName);
                if (!enabled)
                    Ui.Tooltip($"{kind} is unticked, so the engine will not pick this on its own. " +
                               "\"Run next\" still runs it once.");
            }

            // Replan() is a no-op while stopped, so a pick made before pressing Start is queued
            // rather than lost. Say so, otherwise the click looks like it did nothing.
            if (_controller.HasForcedObjective && _controller.Current is RelicController.State.Idle or RelicController.State.Stopped)
            {
                Ui.Wrapped(Yellow, "Picked - it will run when you press Start.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel##forced"))
                    _controller.ClearForcedObjective();
            }
            ImGui.TreePop();
        }
    }

    private void DrawKindToggle(string label, BookWorkKinds kind, string tooltip)
    {
        var on = (_config.BookWorkKinds & kind) != 0;
        if (ImGui.Checkbox(label, ref on))
        {
            _config.BookWorkKinds = on
                ? _config.BookWorkKinds | kind
                : _config.BookWorkKinds & ~kind;
            _saveConfig();
            _controller.Replan();
        }
        Ui.Tooltip(tooltip);
    }

    private bool KindAllowed(CompletionKind kind) => kind switch
    {
        CompletionKind.MonsterSlot => (_config.BookWorkKinds & BookWorkKinds.Enemies) != 0,
        CompletionKind.LeveSlot => (_config.BookWorkKinds & BookWorkKinds.Leves) != 0,
        CompletionKind.DungeonSlot => (_config.BookWorkKinds & BookWorkKinds.Dungeons) != 0,
        CompletionKind.FateSlot => (_config.BookWorkKinds & BookWorkKinds.Fates) != 0,
        _ => true,
    };

    private static string KindLabel(CompletionKind kind) => kind switch
    {
        CompletionKind.MonsterSlot => "Enemy",
        CompletionKind.LeveSlot => "Leve",
        CompletionKind.DungeonSlot => "Dungeon",
        CompletionKind.FateSlot => "FATE",
        _ => kind.ToString(),
    };

    // Stage selection: Auto works the lowest incomplete stage (original behaviour);
    // Manual pins work to a user-inserted stage so a passed/farmable stage (Atma,
    // Novus, Nexus, Zeta) can be revisited. Changes apply immediately via Replan.
    private void DrawStageSelection()
    {
        ImGui.TextDisabled("Stage selection");

        var mode = (int)_config.StageMode;
        if (ImGui.RadioButton("Auto", ref mode, (int)StageSelectionMode.Auto))
        {
            _config.StageMode = StageSelectionMode.Auto;
            _saveConfig();
            _controller.Replan();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Manual", ref mode, (int)StageSelectionMode.Manual))
        {
            _config.StageMode = StageSelectionMode.Manual;
            _saveConfig();
            _controller.Replan();
        }

        if (_config.StageMode == StageSelectionMode.Manual)
        {
            // Index maps directly to the RelicStage enum (None=0, Relic=1 .. Complete=8).
            var stage = (int)_config.ManualStage;
            if (ImGui.Combo("Stage", ref stage, "None\0Relic\0Atma\0Animus\0Novus\0Nexus\0Braves\0Zeta\0Complete\0"))
            {
                _config.ManualStage = (RelicStage)stage;
                _saveConfig();
                _controller.Replan();
            }
            ImGui.TextColored(Grey, "Pins work to this stage; lets you go back to a farmable stage.");
        }

        DrawBookWorkSelection();

        // Heads-up when Auto has nothing to do because the base weapon is not done yet.
        if (_config.StageMode == StageSelectionMode.Auto && BaseRelicState.ShouldWorkBaseRelic())
            Ui.Wrapped(Grey, "Currently on the base relic: work 'A Relic Reborn'. The questmap below shows the prerequisites, parts, and materials.");

        // The base-relic questmap (A Relic Reborn): prerequisites, the ten parts, and materials,
        // all live-verified. Relevant before/while doing the base relic (equipped stage None or
        // Relic), the same one-stage-ahead pattern the Novus/Braves buttons use.
        var relicStage = GameState.EquippedRelicStage();
        if (relicStage is RelicStage.None or RelicStage.Relic || BaseRelicState.ShouldWorkBaseRelic())
        {
            if (ImGui.Button("Open A Relic Reborn questmap"))
                _openQuestmap();
            Ui.Tooltip("Prerequisites, the ten quest parts, and materials for A Relic Reborn, all checked live against your character.");
        }

        // The Novus planner (materia melding route) is relevant from the moment you hold the ANIMUS
        // weapon: Novus is the next stage, so the meld route is worth planning through the WHOLE Animus
        // stage, and it stays relevant while actually melding the Novus. Surface it for both: Manual
        // pinned to Animus/Novus, or an Animus/Novus relic equipped in Auto. (Mirrors the Braves
        // planner below, which shows one stage ahead, on Nexus.) Hidden for base-relic / Atma / Nexus+.
        var planStage = _config.StageMode == StageSelectionMode.Manual
            ? _config.ManualStage
            : GameState.EquippedRelicStage();
        var onNovusStage = planStage is RelicStage.Animus or RelicStage.Novus;
        if (onNovusStage)
        {
            if (ImGui.Button("Open Novus planner"))
                _openNovus();
            Ui.Tooltip("Your materia across bags and retainers, plus the cheapest melding route in order.");
        }

        // The Braves planner (il125 shopping list) is relevant to a player working the
        // Braves stage: Manual pinned to Braves, or a Nexus weapon equipped in Auto (Nexus
        // complete, Braves is the next stage). It prices every Braves material on the board.
        // Keep the reopen button available across the whole Braves stage: from Nexus-complete
        // (Braves is next) through holding the il125 Braves weapon (EquippedRelicStage()==Braves).
        // The prior version showed it only on Nexus, so once the Braves weapon was equipped the
        // button vanished and a closed planner could not be reopened. Mirrors the Novus button,
        // which includes its own stage; Manual mirrors the same, keyed on the pinned stage.
        var bravesPlanStage = _config.StageMode == StageSelectionMode.Manual
            ? _config.ManualStage
            : GameState.EquippedRelicStage();
        var onBravesStage = bravesPlanStage is RelicStage.Nexus or RelicStage.Braves;
        if (onBravesStage)
        {
            if (ImGui.Button("Open Braves planner"))
                _openBraves();
            Ui.Tooltip("The full Zodiac Braves shopping list with market prices, a buy-it-all total, and Artisan crafting.");
        }
    }

    // vnavmesh status and recovery controls. Shows the build progress when the mesh
    // is (re)building, and offers a manual stop and reload for when navigation gets
    // stuck mid-path.
    private void DrawNavmeshControls()
    {
        var progress = _navmesh.BuildProgress();
        ImGui.TextUnformatted("Navmesh:");
        ImGui.SameLine();
        if (progress >= 0f)
            ImGui.ProgressBar(System.Math.Clamp(progress, 0f, 1f), new Vector2(-1, 0), $"building {progress * 100:0}%");
        else if (_navmesh.IsReady())
            ImGui.TextColored(Green, "ready");
        else
            ImGui.TextColored(Red, "not loaded");

        if (ImGui.Button("Stop pathfinding"))
            _navmesh.Stop();
        Ui.Tooltip("Halt movement immediately. Use if the character is stuck mid-path.");

        ImGui.SameLine();
        if (ImGui.Button("Reload navmesh"))
            _navmesh.Reload();
        Ui.Tooltip("Cancel pathfinding and reload this zone's navmesh. Fixes most stuck states.");
    }

    // Persistent Nexus Light readout: shown whenever a Novus relic is equipped, so the
    // 0/2000 gauge is visible while farming regardless of the active objective. This is
    // the in-game Light gauge value, read live from the equipped relic.
    private static void DrawLightTracker()
    {
        if (!GameState.TryGetNexusLight(out var light))
            return;
        var max = GameState.NexusLightMax;
        ImGui.TextUnformatted("Nexus Light:");
        ImGui.SameLine();
        ImGui.ProgressBar(System.Math.Clamp((float)light / max, 0f, 1f), new Vector2(-1, 0), $"{light} / {max}");
    }

    // Persistent Zeta Mahatma readout: shown whenever a Zodiac Braves weapon is
    // equipped. Overall Mahatma awakened (X/12) plus the current Mahatma's fill (Y/40),
    // read live from the relic. At 12/12 it prompts the one-time Jalzahn finish (which
    // needs the relic unequipped, so it is left to the player).
    private static void DrawMahatmaTracker()
    {
        if (!GameState.TryGetMahatma(out var completed, out var points, out _))
            return;
        var max = GameState.MahatmaCount;
        ImGui.TextUnformatted("Mahatma:");
        ImGui.SameLine();
        // "completed" tops out at 11 (the 12th has no next to bank into), so the done state is the
        // last Mahatma sitting full -- use IsZetaFarmComplete and show 12/12 for it.
        var charged = GameState.IsZetaFarmComplete();
        var shown = charged ? max : completed;
        ImGui.ProgressBar(System.Math.Clamp((float)shown / max, 0f, 1f), new Vector2(-1, 0), $"{shown} / {max}");
        if (charged)
        {
            Ui.Wrapped(Green, "All 12 charged: unequip the relic and pick 'Zodiac Weapon Awakening' at Jalzahn (Hyrstmill, North Shroud).");
        }
        else
        {
            ImGui.TextUnformatted("Current:");
            ImGui.SameLine();
            ImGui.ProgressBar(System.Math.Clamp((float)points / GameState.MahatmaPointsMax, 0f, 1f),
                new Vector2(-1, 0), $"{points} / {GameState.MahatmaPointsMax}");
        }
    }

    // The Zenith next-step prompt shown while any finished base relic still awaits the trade:
    // Zenith is the il90 upgrade right after the base 2-star relic and is a pure item gate (no
    // quest to drive selection), so HOLDING the weapon anywhere -- equipped, armoury chest, or
    // bags -- keeps this step shown until it is traded at the Furnace beside Gerolt. Several
    // weapons at the stage are each listed (x2/x3 when duplicated) with the total mist need;
    // "3x each" is only claimed when every pending weapon really costs 3 (the Paladin pair is
    // its own two trades at 2 + 1 mists, see RelicWeaponStages.ZenithMistCost).
    private static void DrawZenithNextStep()
    {
        var pending = BaseRelicState.ZenithPendingWeapons();
        var weapons = BaseRelicState.CountZenithPending(pending);
        var need = BaseRelicState.ZenithMistNeeded(pending);
        var mistId = BaseRelicCatalog.ItemId("Thavnairian Mist");
        var have = mistId != 0 ? GameState.InventoryCount(mistId) : 0;
        ImGui.TextColored(Yellow, weapons > 1
            ? $"Next step (Zenith): {weapons} weapons ready to trade ({have} / {need} Thavnairian Mist)"
            : $"Next step (Zenith): trade {(need > 0 ? need : 3)}x Thavnairian Mist ({have} held)");
        if (pending.Count > 0)
            Ui.Tooltip($"Waiting on the Furnace: {BaseRelicState.DescribeZenithPending(pending)}.\nEquipped or in the armoury chest both count.");
        ImGui.TextColored(Grey, have >= need && need > 0
            ? "Start goes straight to the Furnace beside Gerolt (Hyrstmill, North Shroud) and trades it."
            : "Start buys the mist from Auriana (Revenant's Toll, 20 poetics each), then trades at the Furnace beside Gerolt (Hyrstmill).");
    }

    // "A Relic Reborn" part 2: the class weapon plus its two Grade III materia. Shown while the
    // live relic-quest sequence is still short of the hand-over (see ClassWeaponSteps.IsWindow),
    // so it appears as soon as the line is underway and disappears once Gerolt has the melded
    // weapon and the Chimera becomes the active step.
    private void DrawClassWeaponNextStep()
    {
        var job = BaseRelicState.ActiveRelicJob();
        if (job == RelicJob.None)
            return;
        if (!Data.ClassWeaponSteps.IsWindow(BaseRelicState.RelicQuestSequenceFor(job)))
            return;

        ClassWeaponPanel.Draw(job, _artisanLists,
            "Next step (A Relic Reborn): the class weapon, melded", "main");
        ImGui.TextColored(Grey, "Hand it to Gerolt (Hyrstmill) when melded; Start drives that turn-in and the Chimera after it.");
    }

    // The Animus next-step prompt shown when the Atma weapon is equipped with no book yet: buy the
    // first Trials of the Braves book from G'Jusana. Relicable auto-buys it on Start (this is the
    // guidance shown while idle).
    private static void DrawFirstBookNextStep()
    {
        ImGui.TextColored(Yellow, "Next step (Animus): buy your first Trials of the Braves book");
        ImGui.TextColored(Grey, "From G'Jusana (Rowena's House of Splendors, Mor Dhona; 100 poetics). Start does this automatically.");
    }

    private void DrawObjectiveProgress(RelicObjective obj)
    {
        var c = obj.Completion;
        switch (c.Kind)
        {
            case CompletionKind.MonsterSlot:
                Bar(GameState.MonsterProgress(c.Slot), c.Threshold, "kills");
                break;
            case CompletionKind.ItemCount:
                Bar(GameState.InventoryCount(c.ItemId), c.Threshold, "items");
                break;
            case CompletionKind.KeyItemCount:
                Bar(GameState.KeyItemCount(c.ItemId), c.Threshold, "items");
                break;
            case CompletionKind.AlexandriteCount:
                Bar(GameState.InventoryCount(Data.NovusData.AlexandriteItemId),
                    _config.AlexandriteTarget, "Alexandrite");
                break;
            case CompletionKind.DungeonSlot:
                Done(GameState.IsDungeonComplete(c.Slot));
                break;
            case CompletionKind.FateSlot:
                Done(GameState.IsFateComplete(c.Slot));
                break;
            case CompletionKind.LeveSlot:
                Done(GameState.IsLeveComplete(c.Slot));
                break;
            case CompletionKind.RelicItem:
                Done(GameState.EquippedRelicItemId() == c.ExpectedRelicItemId);
                break;
            case CompletionKind.LightGauge:
                if (GameState.TryGetNexusLight(out _))
                    ImGui.TextColored(Grey, $"Farming Light to {GameState.NexusLightMax} (auto-stops when full).");
                else
                    ImGui.TextColored(Grey, "Equip your Novus relic to track and farm Light.");
                break;
            case CompletionKind.AtmaUpgraded:
                ImGui.TextColored(Grey,
                    $"Zenith enhancement at Jalzahn (Hyrstmill): {GameState.AtmaCollectedCount()}/12 atmas + the equipped Zenith weapon.");
                Done(GameState.EquippedRelicStage() >= RelicStage.Atma);
                break;
            case CompletionKind.NexusUpgraded:
                Done(GameState.EquippedRelicStage() >= RelicStage.Nexus);
                break;
            case CompletionKind.MahatmaGauge:
                if (GameState.IsZetaFarmComplete())
                    ImGui.TextColored(Grey, "All 12 Mahatma charged; finish at Jalzahn yourself (relic unequipped).");
                else if (GameState.TryGetMahatma(out var doneMahatma, out _, out _))
                    ImGui.TextColored(Grey, $"Charging Mahatma ({doneMahatma}/{GameState.MahatmaCount}); attaches at Remon, farms via AutoDuty.");
                else
                    ImGui.TextColored(Grey, "Equip your Zodiac Braves weapon to charge Mahatma.");
                break;
            case CompletionKind.AllStepsDone:
                ImGui.TextDisabled("Runs step by step (no single progress gauge).");
                break;
            default:
                break;
        }
    }

    private static void Bar(int current, int threshold, string unit)
    {
        var max = threshold <= 0 ? 1 : threshold;
        ImGui.ProgressBar(System.Math.Clamp((float)current / max, 0f, 1f),
            new Vector2(-1, 0), $"{current} / {max} {unit}");
    }

    private static void Done(bool done)
        => ImGui.ProgressBar(done ? 1f : 0f, new Vector2(-1, 0), done ? "complete" : "in progress");
}
