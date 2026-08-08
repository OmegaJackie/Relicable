using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Relicable.Data;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Braves;

// One priced row of the Braves shopping list: a material, how many are still needed
// (after what the player already holds), and its current market unit/line price (HQ for
// craftables, NQ otherwise). UnitMarket is null when the item has no current listing.
public sealed class BravesLine
{
    public BravesMaterial Material { get; init; } = new();
    public uint ItemId { get; init; }
    public int Have { get; init; }
    public int Need { get; init; }

    // How many sit on retainers, from the cached bell scan (Configuration.RetainerBravesItems).
    // A snapshot, not a live read: it is what the last visit to each retainer saw. Always 0 for
    // the key-item dungeon drops, which cannot be entrusted to a retainer.
    public int OnRetainers { get; init; }

    public long? UnitMarket { get; init; }
    public long? LineMarket => UnitMarket.HasValue ? UnitMarket.Value * Need : null;

    // This row can be pulled out of a retainer: a real Item-sheet id, still short, and the
    // cache last saw at least one on a retainer.
    public bool Fetchable => ItemId != 0 && Need > 0 && OnRetainers > 0;
}

// The fully costed Braves plan. Mirrors the Novus route's "grand total" idea: a single
// gil figure for buying everything the market lists, plus the native-currency and
// dungeon-farm requirements that gil cannot satisfy, reported alongside.
public sealed class BravesPlan
{
    public IReadOnlyList<BravesLine> Lines { get; init; } = new List<BravesLine>();

    // Sum of every listed line's market cost (the "buy whatever is on the board" figure).
    public long MarketGilListed { get; init; }

    // The four 100,000-gil zone-vendor items, needed regardless of crafting choice.
    public long VendorGil { get; init; }

    // The eight 3,000-gil desynthesis source items, needed only on the craft path.
    public long DesynthSourceGil { get; init; }

    public long Seals { get; init; }     // Bombard Core (Grand Company seals)
    public long Poetics { get; init; }   // Sacred Spring Water (Allagan Tomestones of Poetics)

    public int DungeonDropsToFarm { get; init; }
    public int Craftables { get; init; }

    // Distinct still-needed materials the retainer cache last saw on a retainer, i.e. how many
    // rows "Fetch all from retainers" would go after.
    public int FetchableLines { get; init; }

    // Tradable-looking lines with no current listing (so the market total understates).
    public int Unpriced { get; init; }
    public bool PricesReady { get; init; }

    // Headline "buy it all with gil where possible" figure: everything the market lists
    // plus the gil-vendor items. Seals/Poetics/dungeon drops are reported separately.
    public long GrandGilBuyable => MarketGilListed + VendorGil;
}

// Ties the Braves shopping list to live Universalis prices and the player's current
// holdings, the same way MateriaPlanner does for Novus melding. Owns no UI; the window
// reads ComputePlan(). Uses its own UniversalisClient instance so its item set does not
// fight the Novus planner's cache (the client caches one item set per market/scope).
public sealed class BravesPlanner
{
    private readonly Configuration _config;
    private readonly UniversalisClient _universalis;

    // Region group row id (WorldDCGroupType.Region) -> Universalis region name. Mirrors
    // MateriaPlanner.RegionNames (kept local so Braves does not depend on the Novus side).
    private static readonly Dictionary<uint, string> RegionNames = new()
    {
        [1] = "Japan", [2] = "North-America", [3] = "Europe", [4] = "Oceania",
    };

    public BravesPlanner(Configuration config, UniversalisClient universalis)
    {
        _config = config;
        _universalis = universalis;
    }

    public UniversalisClient Universalis => _universalis;

    // Start/refresh the Universalis fetch for every Braves material at the resolved
    // market. Cheap to call each frame; the client self-throttles.
    public void EnsurePrices(bool force = false)
    {
        var market = ResolveMarketName();
        if (!string.IsNullOrEmpty(market))
            // Only tradable items: untradable dungeon drops have no listings, so fetching them
            // just produces Universalis errors for items the market can never price.
            _universalis.EnsurePrices(BravesData.TradableItemIds(), market, _config.MarketScope, force);
    }

    public BravesPlan ComputePlan()
    {
        var lines = new List<BravesLine>();
        long marketListed = 0, vendorGil = 0, desynthGil = 0, seals = 0, poetics = 0;
        int dungeon = 0, craft = 0, unpriced = 0, fetchable = 0;
        var ready = _universalis.State == UniversalisClient.FetchState.Loaded;

        // A quest already DELIVERED for the weapon in progress (its reward item is banked -- see
        // BravesData.QuestDelivered) consumes no more materials, so its rows must stop counting:
        // otherwise the list (and the retainer auto-fetch that reads it) re-demands the very items
        // the final turn-in just ate. Per-quest rows zero out when their quest is delivered; the
        // stage-wide rows (Bombard Core / Sacred Spring Water, authored quantity 4 = one per quest)
        // shrink by the delivered count instead.
        var deliveredQuests = BravesData.DeliveredQuestCount();

        foreach (var m in BravesData.Materials)
        {
            var id = BravesData.ItemId(m.ItemName);
            // Dungeon drops are Key Items (EventItem sheet / KeyItems container): the normal Item
            // id + GetInventoryItemCount never see them, so resolve + count them via the key path.
            var keyId = id == 0 ? BravesData.KeyItemId(m.ItemName) : 0u;
            var have = id != 0 ? GameState.InventoryCount(id)
                     : keyId != 0 ? GameState.KeyItemCount(keyId)
                     : 0;
            var quantity = BravesData.MaterialQuests.Contains(m.Quest)
                ? BravesData.QuestDelivered(m.Quest) ? 0 : m.Quantity
                : Math.Max(0, m.Quantity - deliveredQuests);
            var need = quantity - have;
            if (need < 0)
                need = 0;

            // Craftables must be HQ; everything else is priced NQ. Fall back HQ->NQ so a
            // craftable with only NQ listings still shows a (lower-bound) number.
            long? unit = null;
            if (id != 0)
                unit = m.Source == BravesSource.Craft
                    ? _universalis.UnitPriceHq(id) ?? _universalis.UnitPrice(id)
                    : _universalis.UnitPrice(id);

            // Retainer stock comes from the cached bell scan. Key items (the dungeon drops)
            // resolve to no Item id and can never be on a retainer, so they stay at 0.
            var onRetainers = id != 0 ? _config.RetainerBravesItems.TotalFor(id) : 0;

            var line = new BravesLine
            {
                Material = m, ItemId = id, Have = have, Need = need,
                OnRetainers = onRetainers, UnitMarket = unit,
            };
            lines.Add(line);

            if (need <= 0)
                continue;
            if (line.Fetchable)
                fetchable++;

            // Each item contributes to the headline gil total exactly once, via the
            // intended gil path: the 100,000-gil zone items at vendor price; the
            // craftables and the currency/farm items at their market listing when one
            // exists. Desynthesis sources are craft-path-only and never join the buy
            // total (buying the crafted item HQ skips them).
            switch (m.Source)
            {
                case BravesSource.VendorGil:
                    vendorGil += m.FixedCost * need;
                    break;
                case BravesSource.DesynthSource:
                    desynthGil += m.FixedCost * need;
                    break;
                case BravesSource.VendorSeals:
                    seals += m.FixedCost * need;
                    if (line.LineMarket is { } sm)
                        marketListed += sm;
                    else
                        unpriced++; // tradable (Bombard Core) but unlisted -> total understates
                    break;
                case BravesSource.VendorPoetics:
                    poetics += m.FixedCost * need;
                    if (line.LineMarket is { } pm)
                        marketListed += pm;
                    else
                        unpriced++; // tradable (Sacred Spring Water) but unlisted
                    break;
                case BravesSource.DungeonDrop:
                    dungeon += need;
                    if (line.LineMarket is { } dm)
                        marketListed += dm; // tradable drops are rare, but price them if listed
                    else if (BravesData.IsTradable(id))
                        unpriced++; // only a TRADABLE drop can understate the buy total
                    break;
                case BravesSource.Craft:
                    craft += need;
                    if (line.LineMarket is { } cm)
                        marketListed += cm;
                    else
                        unpriced++; // a craftable with no HQ listing makes the buy total understate
                    break;
            }
        }

        return new BravesPlan
        {
            Lines = lines,
            MarketGilListed = marketListed,
            VendorGil = vendorGil,
            DesynthSourceGil = desynthGil,
            Seals = seals,
            Poetics = poetics,
            DungeonDropsToFarm = dungeon,
            Craftables = craft,
            FetchableLines = fetchable,
            Unpriced = unpriced,
            PricesReady = ready,
        };
    }

    // The world / data-centre / region name to query Universalis with. Mirrors
    // MateriaPlanner.ResolveMarketName so the Braves planner honours the same market
    // scope / override settings the user already configured for Novus.
    public string ResolveMarketName()
    {
        if (!string.IsNullOrWhiteSpace(_config.MarketNameOverride))
            return _config.MarketNameOverride.Trim();

        // CurrentWorld (not HomeWorld) so the market follows the DC/world you are actually on,
        // including after data-centre travel.
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
