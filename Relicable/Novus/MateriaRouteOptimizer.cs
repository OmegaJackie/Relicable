using System;
using System.Collections.Generic;
using Relicable.Data;
using Relicable.Model;

namespace Relicable.Novus;

// Supplies a unit market price (gil) for one materia type and grade, or null when no
// listing is known. Implemented by the planner over cached Universalis data.
public interface IMateriaPriceSource
{
    long? UnitPrice(MateriaType type, int grade);
}

// Supplies how many (type, grade) materia are already held (bags + retainers). Implemented
// by the planner. Lets the optimizer treat owned materia as free, so the route uses what you
// already have first and only prices the remainder.
public interface IMateriaStockSource
{
    int Held(MateriaType type, int grade);
}

// Computes the best valid melding route for a Novus Sphere Scroll, accounting for what is
// already infused PER STAT and what materia you already own.
//
// Objective: minimise gil you must SPEND to finish the scroll ("use your materia first,
// then cheapest; any stats"). Materia you already hold costs 0, so a stat you own materia
// for is preferred, and among the rest the cheapest listing wins. This also makes the plan
// robust when Universalis has no price for a grade you already hold -- it is simply free.
// Every Sphere Scroll rule is respected: at most N distinct stats; each stat capped (44 /
// Piety 31, etc.); within a stat, grades fill in ascending tier order; failed melds waste
// materia (expected materia per tier from the success curve).
//
// Per-stat progress: each stat may already hold some points (the user sets these as
// they infuse, since the in-game bar is not readable). The solver treats those as a
// floor -- a stat continues from its current grade, and a maxed stat yields no lines
// -- so the route never asks for, say, Grade I of a stat that is already full.
//
// The solver is an exact dynamic program over (new stats used, additional points); it
// is pure and deterministic so it can be unit-tested away from the game.
public static class MateriaRouteOptimizer
{
    private const double UnpricedPenalty = 1_000_000_000.0;

    public static MateriaRoute Compute(NovusWeaponProfile profile, int maxStats, IMateriaPriceSource prices,
        IReadOnlyDictionary<string, Dictionary<MateriaType, int>>? existingByScroll = null,
        IMateriaStockSource? stock = null)
    {
        var effectiveMax = Math.Clamp(maxStats, 1, MateriaCatalog.AllTypes.Count);

        // Held materia (bags + retainers) is a single pool shared across a profile's scrolls. It is
        // SNAPSHOT here and DECREMENTED as each scroll is solved, so Paladin's Curtana and Holy Shield
        // never both count the same stack as free (each scroll gets what the earlier one did not use).
        var pool = new Dictionary<(MateriaType, int), int>();
        foreach (var t in MateriaCatalog.AllTypes)
            for (var g = 1; g <= MateriaCatalog.MaxGrade; g++)
                pool[(t, g)] = stock?.Held(t, g) ?? 0;

        var scrolls = new List<ScrollRoute>();
        foreach (var spec in MateriaCatalog.GetScrolls(profile))
        {
            // Each scroll continues from its OWN infused progress (keyed by spec name), so Paladin's
            // Curtana and Holy Shield scrolls are tracked independently instead of sharing one dict.
            IReadOnlyDictionary<MateriaType, int>? existing =
                existingByScroll != null && existingByScroll.TryGetValue(spec.Name, out var e) ? e : null;
            scrolls.Add(SolveScroll(spec, effectiveMax, prices, existing, pool));
        }

        var costByGrade = new Dictionary<int, long>();
        long known = 0;
        var fully = true;
        var melds = 0;
        foreach (var s in scrolls)
        {
            known += s.KnownCost;
            fully &= s.FullyPriced;
            foreach (var line in s.Lines)
            {
                melds += line.SuccessfulMelds;
                if (line.LineCost is { } lc)
                    costByGrade[line.Grade] = costByGrade.GetValueOrDefault(line.Grade) + lc;
            }
        }

        return new MateriaRoute
        {
            Scrolls = scrolls,
            KnownCost = known,
            FullyPriced = fully,
            TotalMelds = melds,
            TotalAlexandrite = melds,
            CostByGrade = costByGrade,
        };
    }

    private static ScrollRoute SolveScroll(ScrollSpec spec, int maxStats, IMateriaPriceSource prices,
        IReadOnlyDictionary<MateriaType, int>? existingByStat, Dictionary<(MateriaType, int), int> pool)
    {
        var types = MateriaCatalog.AllTypes;
        var n = types.Count;
        var total = spec.TotalPoints;

        var caps = new int[n];
        var ex = new int[n];                 // points already infused per stat (the floor)
        var statCost = new double[n][];      // cost of placing 1..p points in a stat
        var exUsed = 0;
        var exTotal = 0;
        for (var i = 0; i < n; i++)
        {
            caps[i] = spec.Cap(types[i]);
            var e = 0;
            if (existingByStat != null && existingByStat.TryGetValue(types[i], out var ev))
                e = Math.Clamp(ev, 0, caps[i]);
            ex[i] = e;
            if (e > 0) { exUsed++; exTotal += e; }
            statCost[i] = new double[caps[i] + 1];
            for (var p = 0; p <= caps[i]; p++)
                statCost[i][p] = StatCost(spec, types[i], e, p, prices, pool);
        }

        var existingByType = new Dictionary<MateriaType, int>();
        for (var i = 0; i < n; i++)
            if (ex[i] > 0) existingByType[types[i]] = ex[i];

        var remaining = total - exTotal;
        if (remaining <= 0)
            return new ScrollRoute { ScrollName = spec.Name, TotalPoints = 0, FullyPriced = true, Allocation = existingByType };

        // Stats already in use are pre-counted; only NEW stats consume the remaining
        // stat budget.
        var maxNewUsed = Math.Max(0, maxStats - exUsed);

        // dp[newUsed, addPts] = min additional cost; choice[i, newUsed, addPts] = the
        // additional melds chosen for stat i to reach that state.
        var dp = Fill(maxNewUsed + 1, remaining + 1, double.PositiveInfinity);
        dp[0, 0] = 0.0;
        var choice = new int[n, maxNewUsed + 1, remaining + 1];

        for (var i = 0; i < n; i++)
        {
            var maxAdd = caps[i] - ex[i];
            var baseStatCost = statCost[i][ex[i]];
            var ndp = Fill(maxNewUsed + 1, remaining + 1, double.PositiveInfinity);
            for (var nu = 0; nu <= maxNewUsed; nu++)
            for (var pts = 0; pts <= remaining; pts++)
            {
                var baseCost = dp[nu, pts];
                if (double.IsPositiveInfinity(baseCost))
                    continue;

                var maxA = Math.Min(maxAdd, remaining - pts);
                for (var a = 0; a <= maxA; a++)
                {
                    // Only a previously-empty stat consumes a new stat slot.
                    var nnu = nu + (ex[i] == 0 && a > 0 ? 1 : 0);
                    if (nnu > maxNewUsed)
                        break;
                    var c = baseCost + (statCost[i][ex[i] + a] - baseStatCost);
                    if (c < ndp[nnu, pts + a])
                    {
                        ndp[nnu, pts + a] = c;
                        choice[i, nnu, pts + a] = a;
                    }
                }
            }
            dp = ndp;
        }

        var bestNu = -1;
        var best = double.PositiveInfinity;
        for (var nu = 0; nu <= maxNewUsed; nu++)
            if (dp[nu, remaining] < best) { best = dp[nu, remaining]; bestNu = nu; }

        var finalPoints = new Dictionary<MateriaType, int>(existingByType);
        if (bestNu >= 0)
        {
            var u = bestNu;
            var pts = remaining;
            for (var i = n - 1; i >= 0; i--)
            {
                var a = choice[i, u, pts];
                if (a > 0 && ex[i] == 0)
                    u -= 1;
                var p = ex[i] + a;
                if (p > 0)
                    finalPoints[types[i]] = p;
                pts -= a;
            }
        }

        return BuildRoute(spec, finalPoints, existingByType, prices, remaining, pool);
    }

    // Cost to fill one stat from its infused 'floor' up to 'points': for each grade tier the
    // ADDED melds cross, the expected materia BEYOND what your held stock covers, times the
    // grade's unit price (a penalty when that grade is unpriced). Materia you already own is
    // free, so a stat you hold materia for costs less and is preferred; among the rest the
    // cheapest wins.
    //
    // Floor-relative on purpose: an already-infused stat is not re-priced, and (crucially) the
    // held stock is credited to the REMAINING melds, not the ones already done -- pricing from 0
    // would let the infused floor "use up" your stock and under-credit it for the melds still to do.
    private static double StatCost(ScrollSpec spec, MateriaType type, int floor, int points,
        IMateriaPriceSource prices, Dictionary<(MateriaType, int), int> pool)
    {
        if (points <= floor)
            return 0.0;
        var meldsAt = spec.GradeMelds(type, points);
        var meldsFloor = spec.GradeMelds(type, floor);
        var cost = 0.0;
        for (var g = 0; g < meldsAt.Length; g++)
        {
            var floorG = g < meldsFloor.Length ? meldsFloor[g] : 0;
            var eBeyond = spec.ExpectedMateriaForTier(meldsAt[g]) - spec.ExpectedMateriaForTier(floorG);
            if (eBeyond <= 0.0)
                continue;
            var held = pool.TryGetValue((type, g + 1), out var h) ? h : 0;
            var buy = eBeyond - held;
            if (buy <= 0.0)
                continue;                                  // your stock covers this tier -> free
            var unit = prices.UnitPrice(type, g + 1);
            cost += (unit ?? UnpricedPenalty) * buy;
        }
        return cost;
    }

    // Emits only the REMAINING melds per stat: the grades between what is already
    // infused (existingByType) and the planned final points, in ascending grade order.
    private static ScrollRoute BuildRoute(ScrollSpec spec, Dictionary<MateriaType, int> finalPoints,
        Dictionary<MateriaType, int> existingByType, IMateriaPriceSource prices, int remainingTotal,
        Dictionary<(MateriaType, int), int> pool)
    {
        var lines = new List<RouteLine>();
        long knownCost = 0;
        var fullyPriced = true;

        foreach (var type in MateriaCatalog.AllTypes)
        {
            if (!finalPoints.TryGetValue(type, out var points) || points <= 0)
                continue;

            var ex = existingByType.GetValueOrDefault(type);
            var full = spec.GradeMelds(type, points);
            var done = spec.GradeMelds(type, ex);

            for (var g = 0; g < full.Length; g++)
            {
                var doneG = g < done.Length ? done[g] : 0;
                var melds = full[g] - doneG;
                if (melds <= 0)
                    continue;

                // The remaining melds occupy positions doneG..full[g]-1 of the tier's
                // success curve, so their expected cost is the CURVE-TAIL difference --
                // NOT ExpectedMateriaForTier(melds), which would price them as if they
                // sat in the guaranteed early positions and understate the lossy tail
                // (the DP in SolveScroll already prices this correctly via the same
                // statCost difference; this keeps the displayed lines consistent).
                var expected = spec.ExpectedMateriaForTier(full[g]) - spec.ExpectedMateriaForTier(doneG);
                var stock = (int)Math.Ceiling(expected);

                // Held stock (bags + retainers) covers this line first and is free; only the remainder
                // is priced. Consume it from the shared pool so the profile's next scroll (Paladin) does
                // not spend the same stack again. Mirrors StatCost's buy = max(0, expected - held), so the
                // displayed cost matches the cost the DP optimised.
                var held = pool.TryGetValue((type, g + 1), out var h) ? h : 0;
                var heldUsed = Math.Min(held, stock);
                if (heldUsed > 0)
                    pool[(type, g + 1)] = held - heldUsed;
                var buyExpected = expected - heldUsed;
                if (buyExpected < 0.0)
                    buyExpected = 0.0;

                var unit = prices.UnitPrice(type, g + 1);
                long? lineCost;
                if (buyExpected <= 0.0)
                    lineCost = 0;                                   // your stock fully covers it -> free
                else if (unit.HasValue)
                    lineCost = (long)Math.Round(unit.Value * buyExpected);
                else
                    lineCost = null;                                // must buy some, but no listing
                if (lineCost.HasValue)
                    knownCost += lineCost.Value;
                else
                    fullyPriced = false;

                lines.Add(new RouteLine
                {
                    Type = type,
                    Grade = g + 1,
                    SuccessfulMelds = melds,
                    ExpectedMateria = expected,
                    StockToBuy = stock,
                    Held = heldUsed,
                    UnitPrice = unit,
                    LineCost = lineCost,
                });
            }
        }

        return new ScrollRoute
        {
            ScrollName = spec.Name,
            TotalPoints = remainingTotal,
            Lines = lines,
            Allocation = finalPoints,
            FullyPriced = fullyPriced,
            KnownCost = knownCost,
        };
    }

    private static double[,] Fill(int rows, int cols, double value)
    {
        var a = new double[rows, cols];
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            a[r, c] = value;
        return a;
    }
}
