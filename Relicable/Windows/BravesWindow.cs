using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Relicable.Braves;
using Relicable.Data;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Windows;

// The Zodiac Braves (il125) shopping panel. The Braves stage is a quest + materials
// grind (not combat content), so rather than automate it this window prices every
// required material on the market board via Universalis, shows a grand total to buy
// what is listed, and reports the Grand Company seal / Poetics / dungeon-farm
// requirements that gil cannot satisfy. The eight HQ crafted items can be bought HQ on
// the board or, if Artisan is installed, crafted from here. Mirrors NovusWindow.
public sealed class BravesWindow : Window
{
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Grey = new(0.70f, 0.70f, 0.70f, 1f);

    private readonly Configuration _config;
    private readonly BravesPlanner _planner;
    private readonly ArtisanIpc _artisan;
    private readonly RetainerFetchRunner _fetch;
    private readonly Action _saveConfig;

    // The plan is memoized (NovusWindow's MaybeRecompute pattern): rebuilding all ~38
    // lines with inventory counts every frame -- even while the window sat collapsed --
    // was pure per-frame churn. Recomputed when prices land or on a short throttle
    // (inventory can change any time, so it still refreshes near-live).
    private BravesPlan? _plan;
    private long _lastComputeTicks;
    private DateTime _planPriceStamp = DateTime.MinValue;
    private const long RecomputeMs = 500;

    public BravesWindow(Configuration config, BravesPlanner planner, ArtisanIpc artisan,
        RetainerFetchRunner fetch, Action saveConfig)
        : base("Relicable Braves")
    {
        Size = new Vector2(820, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        _config = config;
        _planner = planner;
        _artisan = artisan;
        _fetch = fetch;
        _saveConfig = saveConfig;
    }

    public override void Draw()
    {
        _planner.EnsurePrices();
        var plan = MaybeRecompute();

        ImGui.TextDisabled("Zodiac Braves (il125) shopping list");
        ImGui.TextWrapped(
            "The Braves stage is a quest and materials grind rather than combat content, so it is " +
            "priced instead of automated: every required material is listed with its market price " +
            "and a total to buy everything in one go. Grand Company seals, Poetics, and dungeon " +
            "drops cannot be bought with gil and are listed separately. The eight crafted items " +
            "can be bought HQ on the board, or crafted via Artisan below.");

        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        DrawRetainerActions(plan);
        ImGui.Separator();
        DrawPriceStatus();
        ImGui.Separator();
        DrawTotals(plan);
        ImGui.Separator();
        DrawCraftables(plan);
        DrawGroup(plan, BravesSource.VendorGil, "Gil vendor items (100,000 gil each)", "the gil-vendor items");
        DrawGroup(plan, BravesSource.VendorSeals, "Grand Company seals (Bombard Core)", "the seal items");
        DrawGroup(plan, BravesSource.VendorPoetics, "Poetics (Sacred Spring Water)", "the Poetics items");
        DrawGroup(plan, BravesSource.DesynthSource, "Desynthesis sources (only if crafting; 3,000 gil each)", "the desynthesis sources");
        DrawGroup(plan, BravesSource.DungeonDrop, "Dungeon drops (must be farmed)", string.Empty);
    }

    // "Fetch from retainers" for the whole shopping list, plus the shared auto-pull mode and the
    // engine's status line. Individual rows and each group have their own buttons further down --
    // one retainer trip for everything, or a targeted pull for one item.
    private void DrawRetainerActions(BravesPlan plan)
    {
        ImGui.TextDisabled("Retainers");

        var fetchAll = plan.Lines.Where(l => l.ItemId != 0 && l.Need > 0).ToList();
        if (fetchAll.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("Fetch all from retainers"))
            StartFetch(fetchAll, "the Braves materials");
        if (fetchAll.Count == 0)
            ImGui.EndDisabled();
        Ui.Tooltip(fetchAll.Count == 0
            ? "Nothing outstanding: every market/vendor material on this list is already in your bags."
            : "Open a summoning bell, then click: Relicable visits each retainer in turn and retrieves every " +
              "still-needed material it holds.\n\n" +
              $"{fetchAll.Count} material(s) outstanding; the last retainer scan saw {plan.FetchableLines} of them " +
              "on a retainer. The dungeon drops are key items and can never be on a retainer.",
            whenDisabled: true);

        ImGui.SameLine();
        if (ImGui.Button("Stop##fetch"))
            _fetch.Stop();
        Ui.Tooltip("Stop the retainer fetch. It leaves the retainer UI where it is; close it yourself.");

        ImGui.SameLine();
        var autoWithdraw = _config.AutoWithdrawFromRetainers;
        if (ImGui.Checkbox("Pull items automatically", ref autoWithdraw))
        {
            _config.AutoWithdrawFromRetainers = autoWithdraw;
            _saveConfig();
        }
        Ui.Tooltip("On: the fetch drives the bell and retrieves the stacks itself.\n" +
            "Off: nothing is moved -- open a retainer and the status line lists what to drag out.\n\n" +
            "Shared with the Novus planner's fetch.");

        Ui.Wrapped(_fetch.Busy ? Yellow : Grey, "Status: " + _fetch.Status);
    }

    // Hand a set of rows to the shared retainer engine. Needs are the TOTAL quantity each material
    // wants held (the engine subtracts what is already in the bags live), so a fetch stops the moment
    // the bags are stocked. Key-item rows (dungeon drops) carry no Item id and are skipped.
    private void StartFetch(IEnumerable<BravesLine> lines, string what)
    {
        var needs = new Dictionary<uint, int>();
        foreach (var l in lines)
        {
            if (l.ItemId == 0 || l.Need <= 0)
                continue;
            // Have + Need is the plan's DELIVERED-ADJUSTED total (see BravesPlanner.ComputePlan),
            // not the authored Quantity: a stage-wide row (Bombard Core / Sacred Spring Water)
            // shrinks as quests are delivered, and pulling to the authored 4 would withdraw the
            // very items the finished turn-ins already consumed their share of.
            needs[l.ItemId] = needs.GetValueOrDefault(l.ItemId) + l.Have + l.Need;
        }
        _fetch.Start(needs, what, BravesData.GameName,
            () => _config.RetainerBravesItems.Retainers.Values
                .Select(r => new RetainerFetchRunner.RetainerStock(r.RetainerName, r.Items)));
    }

    // Per-row fetch: pulls just this material out of whichever retainer holds it. Shown for every
    // Item-sheet row; key items (dungeon drops) never reach here.
    private void DrawFetchButton(BravesLine line)
    {
        if (line.ItemId == 0)
        {
            ImGui.TextDisabled("-");
            Ui.Tooltip("A key item -- key items cannot be entrusted to a retainer.");
            return;
        }
        if (line.Need <= 0)
        {
            ImGui.TextColored(Green, "done");
            return;
        }
        if (ImGui.SmallButton($"Fetch##fetch{line.ItemId}"))
            StartFetch(new[] { line }, $"{line.Need}x {DisplayName(line)}");
        Ui.Tooltip(line.OnRetainers > 0
            ? $"Open a summoning bell, then click: retrieve {line.Need}x {DisplayName(line)} " +
              $"(the last scan saw {line.OnRetainers} on your retainers).\n\n" +
              "The game retrieves whole stacks, so you may get more than the quantity needed."
            : "Open a summoning bell, then click: Relicable checks every retainer for this item.\n\n" +
              "No retainer scan has seen it yet -- open each retainer once at a bell (or let AutoRetainer " +
              "run) so the planner can show retainer stock without a trip.");
    }

    private void DrawControls()
    {
        ImGui.TextDisabled("Market");

        var scope = (int)_config.MarketScope;
        if (ImGui.Combo("Market scope", ref scope, "World\0Data Center\0Region\0"))
        {
            _config.MarketScope = (UniversalisScope)scope;
            _planner.EnsurePrices(force: true);
            _saveConfig();
        }

        // Fetch + save when the edit finishes, not per keystroke (a forced fetch per
        // partial name hammered Universalis with 404s; a save per keystroke hit disk).
        var overrideName = _config.MarketNameOverride;
        if (ImGui.InputText("Market override", ref overrideName, 64))
            _config.MarketNameOverride = overrideName;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _planner.EnsurePrices(force: true);
            _saveConfig();
        }
        Ui.Tooltip("Leave blank to use your home world, or enter a world, data centre, or region name. " +
            "Shared with the Novus planner.");

        if (ImGui.Button("Refresh prices"))
            _planner.EnsurePrices(force: true);
    }

    private void DrawPriceStatus()
    {
        var u = _planner.Universalis;
        var market = _planner.ResolveMarketName();
        ImGui.TextUnformatted("Universalis:");
        ImGui.SameLine();
        switch (u.State)
        {
            case UniversalisClient.FetchState.Loaded:
                ImGui.TextColored(Green, $"loaded for {Market(market)} ({u.LastUpdatedUtc.ToLocalTime():HH:mm})");
                break;
            case UniversalisClient.FetchState.Loading:
                ImGui.TextColored(Yellow, $"loading {Market(market)}...");
                break;
            case UniversalisClient.FetchState.Error:
                ImGui.TextColored(Red, "price lookup failed");
                Ui.Tooltip($"Check your connection and press 'Refresh prices'.\n\nDetail: {u.LastError}");
                break;
            default:
                ImGui.TextColored(Grey, string.IsNullOrEmpty(market) ? "waiting for login / market" : "idle");
                break;
        }
    }

    private void DrawTotals(BravesPlan plan)
    {
        if (!ImGui.CollapsingHeader("Totals (per remaining quantity)##totals", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var headlineColor = plan.Unpriced == 0 ? Green : Yellow;
        ImGui.TextColored(headlineColor, $"Buy what the market lists + gil vendors: {Gil(plan.GrandGilBuyable)}");
        Ui.Tooltip("Market listings (HQ for the crafted items) plus the four gil-vendor items. " +
            "Seals, Poetics, and dungeon drops are not gil-buyable and are shown below.");

        ImGui.BulletText($"Market board (listed items): {Gil(plan.MarketGilListed)}");
        ImGui.BulletText($"Gil vendors (4 zone items): {Gil(plan.VendorGil)}");
        ImGui.BulletText($"Grand Company seals: {N(plan.Seals)}  (4x Bombard Core; or buy on the board)");
        ImGui.BulletText($"Allagan Tomestones of Poetics: {N(plan.Poetics)}  (4x Sacred Spring Water)");
        ImGui.BulletText($"Dungeon drops to farm: {plan.DungeonDropsToFarm}");
        ImGui.BulletText($"Crafted items (buy HQ or craft): {plan.Craftables}   (if crafting, desynth sources add {Gil(plan.DesynthSourceGil)})");

        // The totals count only what is in your BAGS as held, because that is what the quests
        // consume -- so anything sitting on a retainer is still being counted as "buy this".
        // Say so, rather than quietly folding retainer stock into a number you would then have
        // to fetch anyway.
        if (plan.FetchableLines > 0)
            ImGui.TextColored(Yellow,
                $"{plan.FetchableLines} still-needed material(s) are on your retainers -- fetch them instead of buying.");

        if (plan.Unpriced > 0)
            ImGui.TextColored(Yellow, $"{plan.Unpriced} tradable item(s) have no current listing, so the market total understates.");
        if (!plan.PricesReady)
            ImGui.TextColored(Grey, "Prices still loading; totals will fill in.");
    }

    private void DrawCraftables(BravesPlan plan)
    {
        if (!ImGui.CollapsingHeader("Crafted items (HQ, level 50 3-star)##craft", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button("Copy craft list"))
        {
            var sb = new StringBuilder();
            foreach (var l in plan.Lines.Where(l => l.Material.Source == BravesSource.Craft))
                sb.AppendLine($"{l.Material.ItemName} (HQ) - {l.Material.CraftJob}");
            ImGui.SetClipboardText(sb.ToString());
        }
        Ui.Tooltip("Copy the crafted items and their crafters to the clipboard.");
        ImGui.SameLine();
        ImGui.TextColored(_artisan.Available ? Green : Grey,
            _artisan.Available ? (_artisan.IsBusy() ? "Artisan: busy" : "Artisan: ready") : "Artisan: not installed");

        var craftLines = plan.Lines.Where(l => l.Material.Source == BravesSource.Craft).ToList();
        ImGui.SameLine();
        DrawGroupFetchButton(craftLines, "the crafted items", "craft");

        var craftSize = new Vector2(0f, (craftLines.Count + 2.5f) * ImGui.GetTextLineHeightWithSpacing());
        if (!ImGui.BeginTable("braves_craft", 9,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX, craftSize))
            return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("HQ unit", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("HQ line", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Crafter", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Craft", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Fetch", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableHeadersRow();

        foreach (var line in craftLines)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawItemName(line, string.Empty,
                $"For '{line.Material.Quest}'. Craft (desynth {line.Material.DesynthFrom} for the ingredient) or buy HQ on the board. Click to copy the name.");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Need.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(line.Have > 0 ? Green : Grey, line.Have.ToString());

            ImGui.TableNextColumn();
            DrawRetainerCount(line);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.UnitMarket.HasValue ? Gil(line.UnitMarket.Value) : "-");

            ImGui.TableNextColumn();
            if (line.LineMarket is { } lc)
                ImGui.TextUnformatted(Gil(lc));
            else
                ImGui.TextColored(Red, "no HQ listing");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Material.CraftJob);

            ImGui.TableNextColumn();
            DrawCraftButton(line);

            ImGui.TableNextColumn();
            DrawFetchButton(line);
        }

        ImGui.EndTable();
    }

    // "Fetch group from retainers" for one section of the list. Disabled (with the reason in the
    // tooltip) when the group has nothing outstanding that could live on a retainer.
    private void DrawGroupFetchButton(IReadOnlyList<BravesLine> lines, string what, string id)
    {
        var outstanding = lines.Where(l => l.ItemId != 0 && l.Need > 0).ToList();
        if (outstanding.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"Fetch group##fetchgrp{id}"))
            StartFetch(outstanding, what);
        if (outstanding.Count == 0)
            ImGui.EndDisabled();
        Ui.Tooltip(outstanding.Count == 0
            ? "Nothing outstanding in this group."
            : $"Open a summoning bell, then click: retrieve this group's {outstanding.Count} outstanding " +
              "material(s) from whichever retainers hold them.",
            whenDisabled: true);
    }

    // The "Retainer" cell: what the last bell scan saw on retainers. A snapshot, so 0 means
    // "not seen", not "definitely none" -- the fetch still checks every retainer.
    private void DrawRetainerCount(BravesLine line)
    {
        if (line.ItemId == 0)
        {
            ImGui.TextDisabled("-");
            return;
        }
        ImGui.TextColored(line.OnRetainers > 0 ? Green : Grey, line.OnRetainers.ToString());
        Ui.Tooltip(line.OnRetainers > 0
            ? $"{line.OnRetainers} on your retainers at the last scan. Use Fetch to pull them into your bags."
            : "No retainer scan has seen this item. Open each retainer at a summoning bell once (or let " +
              "AutoRetainer run) to record what they hold.");
    }

    private void DrawCraftButton(BravesLine line)
    {
        if (line.Need <= 0)
        {
            ImGui.TextColored(Green, "done");
            return;
        }
        if (!_artisan.Available)
        {
            ImGui.TextDisabled("-");
            return;
        }
        var recipe = BravesData.RecipeId(line.ItemId);
        if (recipe == 0)
        {
            ImGui.TextColored(Grey, "no recipe");
            return;
        }
        if (ImGui.Button($"Craft##{line.ItemId}"))
            _artisan.CraftItem(recipe, Math.Max(1, line.Need));
        Ui.Tooltip($"Queue Artisan to craft {line.Need}x {line.Material.ItemName} ({line.Material.CraftJob}). " +
            "Requires the materials and a levelled crafter.");
    }

    // fetchWhat names this group in the fetch status line; empty means the group can never be on
    // a retainer (the key-item dungeon drops), which also drops its Retainer and Fetch columns.
    private void DrawGroup(BravesPlan plan, BravesSource source, string title, string fetchWhat)
    {
        var lines = plan.Lines.Where(l => l.Material.Source == source).ToList();
        if (lines.Count == 0)
            return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"{title}##grp{source}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Untradable groups (dungeon drops) have no market price, so drop the Market and Native
        // cost columns and show just what is needed and where to get it. They are key items, so
        // they get no retainer columns either.
        var tradable = source != BravesSource.DungeonDrop;
        var fetchable = fetchWhat.Length > 0;
        if (fetchable)
            DrawGroupFetchButton(lines, fetchWhat, source.ToString());

        var columns = 4 + (tradable ? 2 : 0) + (fetchable ? 2 : 0);
        var size = new Vector2(0f, (lines.Count + 2.5f) * ImGui.GetTextLineHeightWithSpacing());
        if (!ImGui.BeginTable($"braves_{source}", columns,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX, size))
            return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 44);
        if (fetchable)
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 62);
        if (tradable)
        {
            ImGui.TableSetupColumn("Market", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Native cost", ImGuiTableColumnFlags.WidthFixed, 120);
        }
        ImGui.TableSetupColumn("Where");
        if (fetchable)
            ImGui.TableSetupColumn("Fetch", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableHeadersRow();

        foreach (var line in lines)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawItemName(line, line.Material.Quantity > 1 ? $" x{line.Material.Quantity}" : string.Empty,
                "Click to copy the item name to the clipboard.");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Need.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(line.Have > 0 ? Green : Grey, line.Have.ToString());

            if (fetchable)
            {
                ImGui.TableNextColumn();
                DrawRetainerCount(line);
            }

            if (tradable)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.LineMarket is { } lc ? Gil(lc) : (line.UnitMarket.HasValue ? Gil(line.UnitMarket.Value) : "-"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(NativeCost(line.Material));
            }

            ImGui.TableNextColumn();
            DrawWhere(line);

            if (fetchable)
            {
                ImGui.TableNextColumn();
                DrawFetchButton(line);
            }
        }

        ImGui.EndTable();
    }

    // The "Where" cell: a clickable vendor location (map flag + teleport) or dungeon (opens the
    // Duty Finder, does not queue), falling back to plain text when neither applies.
    private void DrawWhere(BravesLine line)
    {
        var m = line.Material;
        if (m.Source == BravesSource.DungeonDrop)
        {
            var cfc = BravesData.DungeonCfcId(m.Where);
            if (cfc != 0)
            {
                if (ImGui.Selectable($"{m.Where}##df{m.ItemName}"))
                    GameActions.OpenDutyFinder(cfc);
                Ui.Tooltip("Open the Duty Finder for this dungeon (does not queue).");
                return;
            }
        }
        else if (m.Territory != 0)
        {
            if (ImGui.Selectable($"{m.Where}##loc{m.ItemName}"))
                LocationNavigator.Go(m.Territory, m.MapX, m.MapY);
            Ui.Tooltip("Flag this vendor on the map, teleport to the zone, and fly to the flag. " +
                "Requires vnavmesh, with flight unlocked in the zone.");
            return;
        }
        ImGui.TextUnformatted(m.Where);
    }

    // The Item cell: shows the exact in-game item name (canonical case) and copies it to the
    // clipboard on click (handy for the market board search). Quantity suffix and tooltip vary.
    private static void DrawItemName(BravesLine line, string quantitySuffix, string tooltip)
    {
        var name = DisplayName(line);
        if (ImGui.Selectable($"{name}{quantitySuffix}##copy{line.Material.ItemName}"))
            ImGui.SetClipboardText(name);
        if (tooltip.Length > 0)
            Ui.Tooltip(tooltip);
    }

    // The canonical in-game name for a row, falling back to the catalog's own spelling for
    // anything that did not resolve (and for the key-item drops, which carry no Item id).
    private static string DisplayName(BravesLine line)
    {
        var name = BravesData.GameName(line.ItemId);
        return string.IsNullOrEmpty(name) ? line.Material.ItemName : name;
    }

    private static string NativeCost(BravesMaterial m) => m.Source switch
    {
        BravesSource.VendorGil => Gil(m.FixedCost),
        BravesSource.DesynthSource => Gil(m.FixedCost),
        BravesSource.VendorSeals => $"{N(m.FixedCost)} seals each",
        BravesSource.VendorPoetics => $"{N(m.FixedCost)} Poetics each",
        BravesSource.DungeonDrop => "dungeon drop",
        _ => "-",
    };

    private static string Market(string m) => string.IsNullOrEmpty(m) ? "(unknown market)" : m;

    private BravesPlan MaybeRecompute()
    {
        var now = Environment.TickCount64;
        var priceStamp = _planner.Universalis.LastUpdatedUtc;
        var priceChanged = priceStamp != _planPriceStamp;

        if (_plan == null || priceChanged || now - _lastComputeTicks >= RecomputeMs)
        {
            _plan = _planner.ComputePlan();
            _planPriceStamp = priceStamp;
            _lastComputeTicks = now;
        }
        return _plan;
    }

    // ASCII number formatting (see NovusWindow.Gil): invariant culture so "N0" never
    // emits non-ASCII group separators on non-English clients.
    private static string N(long value)
        => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Gil(long value) => N(value) + " gil";
}
