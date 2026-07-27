using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.External;

// Minimal client for the Universalis market-board API (https://universalis.app).
//
// Threading note (deliberate exception to the "no wrapper spawns a thread" rule in
// DESIGN.md Appendix C.4): an HTTP call cannot run on the framework tick, so the
// fetch runs on a background task. Only plain value reads (UnitPrice, State) happen
// on the framework thread, against a concurrent dictionary of immutable longs, so
// the threading invariant for game-memory/IPC access is never violated -- this class
// touches neither.
//
// It uses the "aggregated" endpoint, which returns a compact cheapest-listing record
// per item at world / data-centre / region granularity:
//   GET https://universalis.app/api/v2/aggregated/{worldDcRegion}/{itemIds}
// and reads results[].nq.minListing.{scope}.price (materia is always NQ).
public sealed class UniversalisClient : IDisposable
{
    public enum FetchState { Idle, Loading, Loaded, Error }

    private const string BaseUrl = "https://universalis.app/api/v2/aggregated/";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    // After a failed fetch, hold off automatic retries for a while. EnsurePrices is
    // called every frame by the planner windows, so without this a persistent fast
    // failure (DNS down, 4xx) turned into an HTTP request per round-trip, forever.
    // A forced refresh (the explicit button) still bypasses the backoff.
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<uint, long> _unitPrice = new();
    private readonly ConcurrentDictionary<uint, long> _unitPriceHq = new();

    private string _key = string.Empty;     // marketName|scope the cached PRICES belong to
    private volatile FetchState _state = FetchState.Idle;
    private volatile string _lastError = string.Empty;
    private DateTime _lastUpdatedUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private int _inFlight; // 0/1 guard so only one fetch runs at a time

    public UniversalisClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Relicable/1.0 (FFXIV Dalamud plugin)");
    }

    public FetchState State => _state;
    public string LastError => _lastError;
    public DateTime LastUpdatedUtc => _lastUpdatedUtc;

    // Cached cheapest NQ unit price for an item at the active scope, or null if unknown.
    public long? UnitPrice(uint itemId)
        => _unitPrice.TryGetValue(itemId, out var p) ? p : null;

    // Cached cheapest HQ unit price for an item at the active scope, or null if unknown.
    // Used by the Braves planner, whose crafted items must be HQ; falls back to NQ at the
    // call site when no HQ listing exists.
    public long? UnitPriceHq(uint itemId)
        => _unitPriceHq.TryGetValue(itemId, out var p) ? p : null;

    public bool HasAnyData => !_unitPrice.IsEmpty || !_unitPriceHq.IsEmpty;

    // Ensure prices for the given items at the given market are loaded. Starts a
    // background fetch when there is no fresh data for this market/scope, or when the
    // market/scope changed. Cheap to call every frame; it self-throttles.
    public void EnsurePrices(IReadOnlyCollection<uint> itemIds, string marketName, UniversalisScope scope, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(marketName) || itemIds.Count == 0)
            return;

        var key = $"{marketName}|{scope}";
        var fresh = DateTime.UtcNow - _lastUpdatedUtc < StaleAfter;
        var keyMatches = key == _key;
        if (!force && keyMatches && fresh && _state == FetchState.Loaded)
            return;
        if (_state == FetchState.Loading)
            return;
        // Error backoff: do not auto-retry a failing endpoint every frame.
        if (!force && _state == FetchState.Error &&
            DateTime.UtcNow - _lastAttemptUtc < ErrorBackoff)
            return;

        // Single-fetch guard.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        _lastAttemptUtc = DateTime.UtcNow;
        _state = FetchState.Loading;
        var ids = itemIds.Where(i => i != 0).Distinct().ToArray();
        var scopeNode = scope switch
        {
            UniversalisScope.World => "world",
            UniversalisScope.Region => "region",
            _ => "dc",
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var url = BaseUrl + Uri.EscapeDataString(marketName) + "/" +
                          string.Join(",", ids);
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var parsed = Parse(json, scopeNode, "nq");
                var parsedHq = Parse(json, scopeNode, "hq");

                _unitPrice.Clear();
                foreach (var kv in parsed)
                    _unitPrice[kv.Key] = kv.Value;

                _unitPriceHq.Clear();
                foreach (var kv in parsedHq)
                    _unitPriceHq[kv.Key] = kv.Value;

                // Commit the key only on success, so the freshness check above always
                // describes the prices actually held; a failed market switch keeps the
                // old key and retries (after the backoff) instead of masquerading as
                // fresh data for the new market.
                _key = key;
                _lastUpdatedUtc = DateTime.UtcNow;
                _state = FetchState.Loaded;
                DebugLog.Info($"Universalis: loaded {parsed.Count} NQ / {parsedHq.Count} HQ of {ids.Length} for {marketName} ({scopeNode})");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _state = FetchState.Error;
                DebugLog.Warn($"Universalis fetch failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        });
    }

    // Parse results[].{quality}.minListing.{scope}.price, falling back through broader
    // scopes and then to average sale price so a partial response still yields a number.
    // quality is "nq" or "hq".
    private static Dictionary<uint, long> Parse(string json, string scopeNode, string quality)
    {
        var result = new Dictionary<uint, long>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("itemId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;
            var id = idEl.GetUInt32();
            if (!item.TryGetProperty(quality, out var node))
                continue;

            var price = ReadScopedPrice(node, scopeNode);
            if (price is { } p && p > 0)
                result[id] = p;
        }

        return result;
    }

    private static long? ReadScopedPrice(JsonElement nq, string scopeNode)
    {
        if (nq.TryGetProperty("minListing", out var minListing))
        {
            // Preferred scope, then broaden, so a DC query without a world node still
            // resolves and a sparse market falls back to the region.
            foreach (var node in PreferenceOrder(scopeNode))
                if (TryPrice(minListing, node, out var v))
                    return v;
        }

        // Last resort: average sale price (keeps the optimizer from treating an item
        // with sales but no current listings as unavailable).
        if (nq.TryGetProperty("averageSalePrice", out var avg))
            foreach (var node in PreferenceOrder(scopeNode))
                if (TryPrice(avg, node, out var v))
                    return v;

        return null;
    }

    private static IEnumerable<string> PreferenceOrder(string scopeNode) => scopeNode switch
    {
        "world" => new[] { "world", "dc", "region" },
        "region" => new[] { "region", "dc", "world" },
        _ => new[] { "dc", "region", "world" },
    };

    private static bool TryPrice(JsonElement parent, string node, out long price)
    {
        price = 0;
        if (parent.TryGetProperty(node, out var n) &&
            n.ValueKind == JsonValueKind.Object &&
            n.TryGetProperty("price", out var pe) &&
            pe.ValueKind == JsonValueKind.Number)
        {
            price = (long)Math.Round(pe.GetDouble());
            return price > 0;
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
