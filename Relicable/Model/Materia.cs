using System.Collections.Generic;

namespace Relicable.Model;

// The seven secondary-stat materia types the ARR Novus Sphere Scroll accepts.
// Verified against the FFXIV ConsoleGamesWiki Novus / Sphere Scroll pages: the
// five main stats (Strength, Dexterity, Intelligence, Mind, Vitality) upgrade
// automatically and are NOT melded, so only these seven appear in a route.
//
// Each value carries its in-game stat in the comment. The wiki labels stats with
// their post-Stormblood names; the ARR-era stat is noted because the materia ITEM
// name (used to resolve the item id by text) never changed.
public enum MateriaType
{
    HeavensEye,   // Direct Hit Rate (ARR: Accuracy)   -- Heavens' Eye Materia
    Quickarm,     // Skill Speed                        -- Quickarm Materia
    SavageAim,    // Critical Hit                       -- Savage Aim Materia
    Piety,        // Piety                              -- Piety Materia
    SavageMight,  // Determination                      -- Savage Might Materia
    Quicktongue,  // Spell Speed                        -- Quicktongue Materia
    Battledance,  // Tenacity (ARR: Parry)              -- Battledance Materia
}

// A single line of a computed melding route: meld 'SuccessfulMelds' materia of one
// type and grade into one stat. Grades are 1-based (I..IV). The route is ordered;
// within a stat, lower grades must be completed before higher grades (the wiki's
// "you must go in order" rule), which this ordering preserves.
public sealed class RouteLine
{
    public MateriaType Type { get; init; }
    public int Grade { get; init; }            // 1..4

    // Points added to the stat by this line (each successful meld is +1 point).
    public int SuccessfulMelds { get; init; }

    // Expected materia to consume to land 'SuccessfulMelds' successes, accounting
    // for the per-position failure curve (failed melds destroy the materia but not
    // the Alexandrite). Always >= SuccessfulMelds.
    public double ExpectedMateria { get; init; }

    // TOTAL materia to stock in your bags to meld this line (ExpectedMateria rounded up),
    // regardless of what you already own. This is the meld/fetch target; the amount you
    // must still BUY is StockToBuy - Held. The UI may add a safety buffer for variance.
    public int StockToBuy { get; init; }

    // How many of this line's materia you already own (bags + retainers) and will consume
    // instead of buying, so the route prefers stats you already hold materia for. Always
    // <= StockToBuy, and Held + (net to buy) == StockToBuy. For Paladin's two Sphere Scrolls
    // a shared stack is split across them, so this is the portion allocated to THIS line.
    public int Held { get; init; }

    // Unit market price (gil) for this materia at the configured scope, or null when
    // Universalis returned no listing. LineCost prices only the portion you must BUY
    // (the Held part is free); it is 0 when your stock fully covers the line, and null
    // only when you must buy some and there is no listing.
    public long? UnitPrice { get; init; }
    public long? LineCost { get; init; }

    // Alexandrite consumed by this line (one per successful meld; never wasted on a
    // failed meld). Equal to SuccessfulMelds.
    public int Alexandrite => SuccessfulMelds;
}

// One scroll's worth of route (the whole weapon for every job except Paladin, which
// splits into two scrolls: Curtana 53 + Holy Shield 22).
public sealed class ScrollRoute
{
    public string ScrollName { get; init; } = string.Empty;
    public int TotalPoints { get; init; }
    public List<RouteLine> Lines { get; init; } = new();

    // Per-stat point totals chosen for this scroll (type -> points).
    public Dictionary<MateriaType, int> Allocation { get; init; } = new();

    // True when every line had a known market price.
    public bool FullyPriced { get; init; }

    // Sum of LineCost over priced lines (gil). Unpriced lines are excluded.
    public long KnownCost { get; init; }
}

// The full optimizer result: one or two scroll routes plus rolled-up totals.
public sealed class MateriaRoute
{
    public List<ScrollRoute> Scrolls { get; init; } = new();

    // Grand total gil over all priced lines.
    public long KnownCost { get; init; }
    public bool FullyPriced { get; init; }

    // Total successful melds (should equal 75, or 53+22 for Paladin) and total
    // expected materia to stock across all grades.
    public int TotalMelds { get; init; }
    public int TotalAlexandrite { get; init; }

    // Per material level (grade 1..4) gil subtotals across the whole route, for the
    // "total price per material level" view the user asked for. Missing grades are
    // simply absent from the dictionary.
    public Dictionary<int, long> CostByGrade { get; init; } = new();
}
