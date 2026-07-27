using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Relicable.Data;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Novus;

// Ties the Novus melding inputs together: what materia the player holds (bags and
// cached retainers), live Universalis prices, and the cheapest-route optimizer. It
// is the single source the executor and the UI both read, so the displayed plan and
// the executed plan never diverge.
//
// Implements IMateriaPriceSource so the optimizer prices straight from the cached
// Universalis data, and IMateriaStockSource so it treats materia you already own as free
// ("use your stock first, then cheapest").
public sealed class MateriaPlanner : IMateriaPriceSource, IMateriaStockSource
{
    private readonly Configuration _config;
    private readonly UniversalisClient _universalis;

    // Region group row id (WorldDCGroupType.Region) -> Universalis region name.
    private static readonly Dictionary<uint, string> RegionNames = new()
    {
        [1] = "Japan", [2] = "North-America", [3] = "Europe", [4] = "Oceania",
    };

    public MateriaPlanner(Configuration config, UniversalisClient universalis)
    {
        _config = config;
        _universalis = universalis;
    }

    public UniversalisClient Universalis => _universalis;

    // ---- IMateriaPriceSource ----
    public long? UnitPrice(MateriaType type, int grade)
    {
        var id = MateriaCatalog.ItemId(type, grade);
        return id == 0 ? null : _universalis.UnitPrice(id);
    }

    // ---- Held materia ----
    public int HeldInInventory(MateriaType type, int grade)
        => GameState.InventoryCount(MateriaCatalog.ItemId(type, grade));

    public int HeldInRetainers(MateriaType type, int grade)
        => _config.RetainerMateria.TotalFor(MateriaCatalog.ItemId(type, grade));

    public int HeldTotal(MateriaType type, int grade)
        => HeldInInventory(type, grade) + HeldInRetainers(type, grade);

    // ---- IMateriaStockSource ----
    // What the optimizer treats as free: everything you already own (bags + cached retainers).
    public int Held(MateriaType type, int grade) => HeldTotal(type, grade);

    // ---- Pricing ----
    // Start/refresh the Universalis fetch for every catalog materia at the resolved
    // market. Cheap to call each frame; the client self-throttles.
    public void EnsurePrices(bool force = false)
    {
        var market = ResolveMarketName();
        if (!string.IsNullOrEmpty(market))
            _universalis.EnsurePrices(MateriaCatalog.AllMateriaItemIds().ToList(), market, _config.MarketScope, force);
    }

    // ---- Route ----
    // Plans only the remaining melds, continuing each stat from its infused progress, tracked
    // PER SCROLL (ScrollProgressByScroll) so Paladin's two scrolls (Curtana + Holy Shield) do not
    // share one dict. When an infusion window is open, the OPEN scroll is identified by its max
    // points (AtkValue 11: Curtana 53, Holy Shield 22, others 75) and its stored progress is REPLACED
    // (not merged) with the live per-stat read -- but only when that read's sum matches the game's
    // authoritative infused counter (AtkValue 10, TryReadInfuseTotal). When the window is closed the
    // manually-entered per-scroll progress is used unchanged.
    public MateriaRoute ComputeRoute()
    {
        var scrolls = MateriaCatalog.GetScrolls(_config.NovusWeapon);

        // One-time migration of the legacy single-scroll progress into the per-scroll store (under the
        // profile's first scroll -- the only one the old model tracked), then retire the legacy field.
        if (_config.ScrollProgressByScroll.Count == 0 && _config.ScrollProgressByStat.Count > 0 && scrolls.Count > 0)
        {
            _config.ScrollProgressByScroll[scrolls[0].Name] =
                new Dictionary<MateriaType, int>(_config.ScrollProgressByStat);
            _config.ScrollProgressByStat.Clear();
        }

        // Reconcile the OPEN scroll's live progress into ITS OWN per-scroll entry. Paladin has two
        // scrolls open at different times (Curtana + Holy Shield); the scroll is identified by its max
        // points (AtkValue 11 = TotalPoints: Curtana 53, Holy Shield 22, others 75), so the Holy Shield
        // window can no longer overwrite the Curtana plan. The infused counter (AtkValue 10) is
        // authoritative; the per-stat block is trusted only when its sum matches it.
        if (Steps.RelicMeld.TryReadInfuseTotal(out var infused, out var max))
        {
            Steps.RelicMeld.TryReadProgress(out var live);
            var sum = 0;
            foreach (var v in live.Values)
                sum += v;

            var spec = scrolls.FirstOrDefault(s => s.TotalPoints == max);
            if (spec != null)
            {
                if (sum == infused)
                {
                    // Trustworthy read: replace THIS scroll's stored progress with the live values.
                    // For a fresh scroll (infused == 0) this correctly resets it to empty.
                    _config.ScrollProgressByScroll[spec.Name] = new Dictionary<MateriaType, int>(live);
                }
                else
                {
                    // The per-stat block did not reconcile (layout off, or >5 stats): do NOT overwrite
                    // the config with a misread. Keep manual values and warn.
                    Plugin.Log.Warning(
                        $"Relicable Novus: per-stat read ({sum}) disagrees with {spec.Name}'s infused count " +
                        $"({infused}); keeping the manually-entered progress. Zero the 'Already infused' stats " +
                        "in the Novus window if the plan looks too short.");
                }
            }
        }

        return MateriaRouteOptimizer.Compute(_config.NovusWeapon, _config.MaxMateriaStats, this,
            _config.ScrollProgressByScroll, this);
    }

    // How many materia of a route line you must still BUY: the line's total stock minus the
    // amount the optimizer already allocated from what you own (line.Held). Never negative.
    // Held is per-line (Paladin's two scrolls split a shared stack), so this reads it off the
    // line rather than re-summing HeldTotal, which would double-count across the two scrolls.
    public int StillNeeded(RouteLine line)
    {
        var need = line.StockToBuy - line.Held;
        return need < 0 ? 0 : need;
    }

    // The world / data-centre / region name to query Universalis with: an explicit
    // override if set, otherwise derived from the logged-in character's home world at
    // the configured scope.
    public string ResolveMarketName()
    {
        if (!string.IsNullOrWhiteSpace(_config.MarketNameOverride))
            return _config.MarketNameOverride.Trim();

        // The local player lives on IObjectTable in current Dalamud (IClientState no longer
        // exposes it); CurrentWorld is a RowRef<World> and follows the DC/world you are on
        // (including after data-centre travel), unlike HomeWorld.
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.CurrentWorld.ValueNullable is not { } world)
            return string.Empty;

        switch (_config.MarketScope)
        {
            case UniversalisScope.World:
                return world.Name.ExtractText();

            case UniversalisScope.Region:
            {
                var dc = world.DataCenter.ValueNullable;
                if (dc != null && RegionNames.TryGetValue(dc.Value.Region.RowId, out var region))
                    return region;
                // Fall back to the DC name if the region is unknown.
                return dc?.Name.ExtractText() ?? string.Empty;
            }

            case UniversalisScope.DataCenter:
            default:
            {
                var dc = world.DataCenter.ValueNullable;
                return dc?.Name.ExtractText() ?? string.Empty;
            }
        }
    }
}
