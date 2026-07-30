using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Relicable.Data;
using Relicable.External;
using Relicable.Model;
using Relicable.Novus;

namespace Relicable.Windows;

// The Novus materia panel. Shows what materia the player holds (bags + cached
// retainers), the cheapest valid melding route in order with a per-material-level
// (per-grade) price breakdown and totals from Universalis, and the controls that
// drive it (weapon profile, market scope, max stats, auto-withdraw, refresh).
public sealed class NovusWindow : Window
{
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Grey = new(0.70f, 0.70f, 0.70f, 1f);

    private readonly Configuration _config;
    private readonly MateriaPlanner _planner;
    private readonly NovusActionRunner _runner;
    private readonly Action _saveConfig;

    private MateriaRoute? _route;
    private long _lastComputeTicks;
    private DateTime _routePriceStamp = DateTime.MinValue;
    private bool _needsRecompute = true;
    // Route-relevant config last used for a compute, so edits made elsewhere (the
    // Config window shares NovusWeapon / MaxMateriaStats) also refresh the route.
    private (int Weapon, int MaxStats) _lastCfgSig = (-1, -1);

    public NovusWindow(Configuration config, MateriaPlanner planner, NovusActionRunner runner, Action saveConfig)
        : base("Relicable Novus")
    {
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        _config = config;
        _planner = planner;
        _runner = runner;
        _saveConfig = saveConfig;
    }

    public override void Draw()
    {
        // Keep prices warm and the route current while the window is open.
        _planner.EnsurePrices();
        MaybeRecompute();

        DrawControls();
        ImGui.Separator();
        DrawActions();
        ImGui.Separator();
        DrawPriceStatus();
        ImGui.Separator();
        DrawRoute();
        ImGui.Separator();
        DrawRetainers();
    }

    private void DrawControls()
    {
        ImGui.TextDisabled("Plan");

        var weapon = (int)_config.NovusWeapon;
        if (ImGui.Combo("Weapon", ref weapon, "Standard\0Healer\0Paladin (Curtana + Holy Shield)\0"))
        {
            _config.NovusWeapon = (NovusWeaponProfile)weapon;
            // A different weapon is a different scroll (or two) with different caps; drop all
            // per-scroll progress so it does not carry across (it is persisted otherwise).
            _config.ScrollProgressByScroll.Clear();
            Changed();
        }

        var scope = (int)_config.MarketScope;
        if (ImGui.Combo("Market scope", ref scope, "World\0Data Center\0Region\0"))
        {
            _config.MarketScope = (UniversalisScope)scope;
            _planner.EnsurePrices(force: true);
            Changed();
        }

        // Continuous widgets (slider, int/text inputs) fire their change every frame of
        // a drag and every keystroke; mutate + recompute live, but hit the disk (a
        // synchronous JSON write) only once, when the edit finishes.
        var maxStats = _config.MaxMateriaStats;
        if (ImGui.SliderInt("Max stats", ref maxStats, 2, 7))
        {
            _config.MaxMateriaStats = maxStats;
            _needsRecompute = true;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            _saveConfig();
        Ui.Tooltip("The most stats the route may spread melds across.\n\n" +
            "More stats stay in the cheap low grades; fewer force the expensive high grades. " +
            "Only affects stats beyond those already infused (see below).");

        // Why 'Max stats' can look inert: the route always CONTINUES stats that already have infused
        // points (below), and Max stats only bounds how many NEW stats it may add on top. So if the
        // persisted 'Already infused' values occupy >= Max stats (commonly stale ones left from a
        // previous scroll/job -- that section is collapsed, so they are easy to miss), every Max-stats
        // value in that range yields the identical plan. Surface it and offer a one-click reset.
        var (infusedSum, infusedCount) = InfusedProgress();
        if (infusedSum > 0)
        {
            ImGui.TextColored(Yellow, $"Route continues {infusedSum} pt across {infusedCount} already-infused stat(s); " +
                                      "'Max stats' only adds NEW stats beyond those.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##infusedinline"))
                ResetInfused();
            Ui.Tooltip("Clear the per-stat 'Already infused' values. A fresh scroll should start at 0 — " +
                "stale values from a previous scroll pin the plan and make 'Max stats' look inert.");
        }

        var autoWithdraw = _config.AutoWithdrawFromRetainers;
        if (ImGui.Checkbox("Pull items from retainers", ref autoWithdraw))
        {
            _config.AutoWithdrawFromRetainers = autoWithdraw;
            _saveConfig();
        }
        Ui.Tooltip("'Fetch from Retainer' drives the summoning bell itself, cycling through every retainer " +
            "and pulling the route's materia into your bags.\n\n" +
            "Turn off to only list what to withdraw. Either way the route shows retainer stock, and " +
            "melding always sources from your bags. Shared with the Braves planner's fetch buttons.");

        var alexTarget = _config.AlexandriteTarget;
        if (ImGui.InputInt("Alexandrite target", ref alexTarget))
        {
            _config.AlexandriteTarget = alexTarget < 0 ? 0 : alexTarget;
            _needsRecompute = true;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            _saveConfig();
        Ui.Tooltip("How many Alexandrite to hold before the treasure-map farm stops (one per meld; 75 fills " +
            "a scroll). Raising it re-arms the farm. 0 disables the target.");

        // '###infused' fixes the header's identity so its open/closed state survives the label
        // changing with the sum; the sum shows even while collapsed, so stale progress is visible.
        var infusedHeader = infusedSum > 0
            ? $"Already infused (per stat): {infusedSum} pt across {infusedCount} stat(s)###infused"
            : "Already infused (per stat)###infused";
        if (ImGui.CollapsingHeader(infusedHeader))
        {
            ImGui.TextWrapped("Set how many points each stat already has on the scroll. The route continues " +
                "each stat from its current grade and skips maxed stats. Keep these updated as you infuse.");
            if (ImGui.Button("Reset all to 0 (fresh scroll)##resetinfused"))
                ResetInfused();
            Ui.Tooltip("Zero every stat. Do this when starting a fresh Sphere Scroll so the plan starts from scratch.");

            // Per scroll: Paladin has TWO (Curtana + Holy Shield), each tracked separately; every other
            // job has one. Each stat's cap is per-scroll, and edits bind to that scroll's own progress.
            var scrolls = MateriaCatalog.GetScrolls(_config.NovusWeapon);
            foreach (var scroll in scrolls)
            {
                if (scrolls.Count > 1)
                    ImGui.TextColored(Yellow, scroll.Name);
                var prog = ScrollProgress(scroll.Name);
                foreach (var t in MateriaCatalog.AllTypes)
                {
                    var cap = scroll.Cap(t);
                    if (cap <= 0)
                        continue;
                    var v = prog.GetValueOrDefault(t);
                    if (ImGui.InputInt($"{MateriaCatalog.Stat(t)} (max {cap})##prog{scroll.Name}{t}", ref v))
                    {
                        prog[t] = Math.Clamp(v, 0, cap);
                        _needsRecompute = true;
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        _saveConfig();
                }
            }
        }

        var autoMeld = _config.EnableAutoMeld;
        if (ImGui.Checkbox("Auto-meld the route (experimental)", ref autoMeld))
        {
            _config.EnableAutoMeld = autoMeld;
            _saveConfig();
        }
        Ui.Tooltip("Experimental: drives the Materia Melding window to infuse the route automatically. " +
            "Open your Sphere Scroll's melding window first, and test with cheap materia — a wrong confirm " +
            "can destroy materia.\n\nWhen off, the route is computed and sourced but you infuse it yourself.");

        // The forced Universalis fetch fires when the edit FINISHES, not per keystroke:
        // per-keystroke forcing launched an HTTP request per partial name ("Aet",
        // "Aeth", ...), producing 404 error-flicker and needless API traffic.
        var overrideName = _config.MarketNameOverride;
        if (ImGui.InputText("Market override", ref overrideName, 64))
            _config.MarketNameOverride = overrideName;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _planner.EnsurePrices(force: true);
            Changed();
        }
        Ui.Tooltip("Leave blank to use your home world, or enter a world, data centre, or region name.");

        if (ImGui.Button("Refresh prices"))
        {
            _planner.EnsurePrices(force: true);
            _needsRecompute = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Recompute route"))
            _needsRecompute = true;
        ImGui.SameLine();
        if (ImGui.Button("Save settings"))
            _saveConfig();
    }

    // Infuse / Fetch run via the NovusActionRunner, ticked by the plugin independently
    // of the main controller -- so these work without pressing Start on the main window.
    private void DrawActions()
    {
        ImGui.TextDisabled("Actions (run without starting the main automation)");

        // All three buttons are always shown so an action is always one click away
        // (previously Infuse/Fetch were hidden while busy, so clicks landed on nothing).
        if (ImGui.Button("Infuse Sphere Scroll"))
            _runner.StartInfuse();
        Ui.Tooltip("Open the Materia Melding window on your Sphere Scroll first, then click. " +
            "Progress shows in the Status line below.");
        ImGui.SameLine();
        if (ImGui.Button("Fetch from Retainer"))
            _runner.StartFetch();
        Ui.Tooltip("Open a retainer at the bell ('Entrust or withdraw items'), then click.\n\n" +
            "With 'Pull materia from retainers' on, the route's materia is retrieved into your bags " +
            "one stack at a time; with it off, this lists what to withdraw. With no retainer open it " +
            "reports which retainers hold what you still need.");
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            _runner.Stop();

        Ui.Wrapped(_runner.Busy ? Yellow : Grey, "Status: " + _runner.Status);
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

    private static string Market(string m) => string.IsNullOrEmpty(m) ? "(unknown market)" : m;

    private void DrawRoute()
    {
        ImGui.TextDisabled("Route: use your materia first, then cheapest (in order)");

        if (_route == null || _route.TotalMelds == 0)
        {
            ImGui.TextColored(Grey, "No route yet. Prices may still be loading.");
            return;
        }

        foreach (var scroll in _route.Scrolls)
            DrawScroll(scroll);

        ImGui.Spacing();
        ImGui.TextUnformatted("Totals by material level:");
        for (var g = 1; g <= MateriaCatalog.MaxGrade; g++)
            if (_route.CostByGrade.TryGetValue(g, out var c) && c > 0)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($" {Roman(g)}: {Gil(c)}");
            }

        ImGui.Spacing();
        var costLabel = _route.FullyPriced ? Gil(_route.KnownCost) : $"{Gil(_route.KnownCost)} (some prices missing)";
        ImGui.TextColored(_route.FullyPriced ? Green : Yellow,
            $"Grand total to buy: {costLabel}   |   {_route.TotalMelds} melds, {_route.TotalAlexandrite} Alexandrite");
        Ui.Tooltip("What you still need to buy at the cheapest current listings, after the materia you " +
            "already own (bags + retainers).\n\n" +
            "Failed melds destroy materia, so the counts are expected values — stock a few extra of the " +
            "lossy higher grades. '(some prices missing)' means a needed grade has no listing.");
    }

    private void DrawScroll(ScrollRoute scroll)
    {
        ImGui.Spacing();
        ImGui.TextColored(Yellow, $"{scroll.ScrollName} - {scroll.TotalPoints} points, {Gil(scroll.KnownCost)}");

        if (!ImGui.BeginTable($"route_{scroll.ScrollName}", 8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableSetupColumn("Materia");
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 32);
        ImGui.TableSetupColumn("Melds", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Line", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableHeadersRow();

        var order = 1;
        foreach (var line in scroll.Lines)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(order.ToString());

            ImGui.TableNextColumn();
            DrawMateriaNameCell(line);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Roman(line.Grade));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.SuccessfulMelds.ToString());

            ImGui.TableNextColumn();
            var need = _planner.StillNeeded(line);
            ImGui.TextUnformatted(need.ToString());

            ImGui.TableNextColumn();
            // Materia this line draws from your own stock (bags + retainers). Held + Buy == the line's
            // total, so a line you fully own shows Buy 0. line.Held (not HeldTotal) so Paladin's two
            // scrolls each show only their share of a stack they split.
            var have = line.Held;
            ImGui.TextColored(have > 0 ? Green : Grey, have.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.UnitPrice.HasValue ? Gil(line.UnitPrice.Value) : "-");

            ImGui.TableNextColumn();
            if (line.LineCost.HasValue)
                ImGui.TextUnformatted(Gil(line.LineCost.Value));
            else
                ImGui.TextColored(Red, "no listing");

            order++;
        }

        ImGui.EndTable();
    }

    // The Materia cell: click to search the market board for the exact grade this line
    // needs (like HaselTweaks' "Search the Markets"). If no market board window is open
    // the search is unavailable, so the full item name is copied to the clipboard
    // instead -- paste that into the market board search yourself.
    private static void DrawMateriaNameCell(RouteLine line)
    {
        var baseName = MateriaCatalog.MateriaBaseName(line.Type);
        var fullName = MateriaCatalog.MateriaName(line.Type, line.Grade);
        if (ImGui.Selectable($"{baseName}##novusmat{line.Type}{line.Grade}"))
        {
            if (!Steps.GameState.TrySearchMarketBoard(fullName))
                ImGui.SetClipboardText(fullName);
        }
        Ui.Tooltip($"Raises {MateriaCatalog.Stat(line.Type)}.\n" +
            $"Click to search an open market board for {fullName}; with none open, the name is copied to your clipboard.");
    }

    private void DrawRetainers()
    {
        ImGui.TextDisabled("Retainer materia (scanned at the bell)");

        var cache = _config.RetainerMateria;
        if (cache.Retainers.Count == 0)
        {
            ImGui.TextColored(Grey, "No retainers scanned yet. Open each retainer at a summoning bell (or let AutoRetainer run) to record what they hold.");
            return;
        }

        foreach (var r in cache.Retainers.Values)
        {
            var total = 0;
            foreach (var v in r.Materia.Values)
                total += v;
            var when = DateTimeOffset.FromUnixTimeSeconds(r.ScannedAtUnix).ToLocalTime();
            ImGui.BulletText($"{r.RetainerName}: {total} catalog materia ({r.Materia.Count} stacks), scanned {when:MM-dd HH:mm}");
        }
    }

    private void MaybeRecompute()
    {
        var now = Environment.TickCount64;
        var priceStamp = _planner.Universalis.LastUpdatedUtc;
        var priceChanged = priceStamp != _routePriceStamp;

        // Catch route-relevant settings edited outside this window (Config window).
        var cfgSig = ((int)_config.NovusWeapon, _config.MaxMateriaStats);
        if (cfgSig != _lastCfgSig)
        {
            _lastCfgSig = cfgSig;
            _needsRecompute = true;
        }

        if (!_needsRecompute && !priceChanged)
            return;
        // Throttle so control spam does not recompute every frame.
        if (now - _lastComputeTicks < 400 && !priceChanged)
            return;

        _route = _planner.ComputeRoute();
        _routePriceStamp = priceStamp;
        _lastComputeTicks = now;
        _needsRecompute = false;
    }

    private void Changed()
    {
        _needsRecompute = true;
        _saveConfig();
    }

    // Total infused points and how many stats carry any, summed across ALL of the profile's scrolls
    // (Paladin has two). Drives the "route continues N stats" note and the header sum.
    private (int Sum, int Count) InfusedProgress()
    {
        var sum = 0;
        var count = 0;
        foreach (var scroll in _config.ScrollProgressByScroll.Values)
            foreach (var v in scroll.Values)
                if (v > 0) { sum += v; count++; }
        return (sum, count);
    }

    // Get (creating if needed) the mutable per-stat progress dict for one scroll (by spec name).
    private System.Collections.Generic.Dictionary<Model.MateriaType, int> ScrollProgress(string scrollName)
    {
        if (!_config.ScrollProgressByScroll.TryGetValue(scrollName, out var d))
        {
            d = new System.Collections.Generic.Dictionary<Model.MateriaType, int>();
            _config.ScrollProgressByScroll[scrollName] = d;
        }
        return d;
    }

    // Clear ALL per-scroll "Already infused" progress (a fresh scroll), then recompute + persist. This
    // un-pins a plan that stale progress had frozen (the "Max stats does nothing" report).
    private void ResetInfused()
    {
        _config.ScrollProgressByScroll.Clear();
        _needsRecompute = true;
        _saveConfig();
    }

    // Per-stat cap for the configured weapon (for clamping the per-stat progress
    // inputs): standard 44, Piety 31, Paladin Curtana 31, etc.
    private int StatCap(MateriaType t)
    {
        var scrolls = MateriaCatalog.GetScrolls(_config.NovusWeapon);
        return scrolls.Count > 0 ? scrolls[0].Cap(t) : 44;
    }

    private static string Roman(int grade) => grade switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => grade.ToString(),
    };

    // ASCII gil formatting with thousands separators, e.g. "1,234,567 gil".
    // Invariant culture: "N0" under some client cultures inserts U+00A0/U+202F group
    // separators, which are not ASCII and can render as tofu in the ImGui font.
    private static string Gil(long value)
        => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " gil";
}
