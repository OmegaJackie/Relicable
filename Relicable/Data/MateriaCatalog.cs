using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Relicable.Model;

namespace Relicable.Data;

// Specification of one Sphere Scroll's melding rules. Encodes the verified wiki
// mechanics: a total point target, the per-grade "tier" sizes a stat fills in
// ascending grade order, optional per-type tier overrides (Piety stops a grade
// early on most weapons; healer Direct Hit starts with a +9 base), and the meld
// success curve used to estimate wasted materia.
public sealed class ScrollSpec
{
    public string Name { get; init; } = string.Empty;
    public int TotalPoints { get; init; }

    // Materia per grade a stat consumes, in ascending grade order. Index 0 = Grade I.
    public int[] DefaultTiers { get; init; } = Array.Empty<int>();

    // Per-type overrides of DefaultTiers (e.g. Piety, healer Direct Hit).
    public Dictionary<MateriaType, int[]> TierOverrides { get; init; } = new();

    // Probability that the k-th successful meld within a single (type, grade) tier
    // lands. Index 0 = first meld. Failed melds destroy the materia (never the
    // Alexandrite), so expected materia exceeds successes once a tier passes its
    // guaranteed positions.
    public double[] SuccessRates { get; init; } = Array.Empty<double>();

    public int[] Tiers(MateriaType type)
        => TierOverrides.TryGetValue(type, out var t) ? t : DefaultTiers;

    public int Cap(MateriaType type) => Tiers(type).Sum();

    // Split 'points' allocated to a stat into successful melds per grade, honouring
    // the ascending tier fill order. Returns an array indexed by grade-1.
    public int[] GradeMelds(MateriaType type, int points)
    {
        var tiers = Tiers(type);
        var result = new int[tiers.Length];
        var remaining = Math.Min(points, Cap(type));
        for (var g = 0; g < tiers.Length && remaining > 0; g++)
        {
            var take = Math.Min(remaining, tiers[g]);
            result[g] = take;
            remaining -= take;
        }
        return result;
    }

    // Expected materia to consume to land 'melds' successes inside one tier, summing
    // 1/p over the first 'melds' positions of the success curve.
    public double ExpectedMateriaForTier(int melds)
    {
        if (melds <= 0)
            return 0.0;
        var total = 0.0;
        for (var i = 0; i < melds; i++)
        {
            var rate = i < SuccessRates.Length ? SuccessRates[i] : SuccessRates[^1];
            if (rate <= 0.0)
                rate = 1.0; // defensive; never divide by zero
            total += 1.0 / rate;
        }
        return total;
    }
}

// Static catalog of the seven Novus materia types: their stat names, item names per
// grade, item-id resolution via Lumina, and the per-weapon scroll specifications.
// Item ids are resolved by English name (like NovusData) so nothing is hardcoded.
public static class MateriaCatalog
{
    public const int MaxGrade = 4;

    public static readonly IReadOnlyList<MateriaType> AllTypes = new[]
    {
        MateriaType.HeavensEye, MateriaType.Quickarm, MateriaType.SavageAim,
        MateriaType.Piety, MateriaType.SavageMight, MateriaType.Quicktongue,
        MateriaType.Battledance,
    };

    // Standard meld success curve (every weapon except Paladin): first six guaranteed,
    // then 96/90/82/72/60 percent for melds 7..11.
    private static readonly double[] StandardSuccess =
        { 1, 1, 1, 1, 1, 1, 0.96, 0.90, 0.82, 0.72, 0.60 };

    // Paladin Curtana curve: first four guaranteed, then 96/86/74/60 for melds 5..8.
    private static readonly double[] CurtanaSuccess =
        { 1, 1, 1, 1, 0.96, 0.86, 0.74, 0.60 };

    // Paladin Holy Shield curve: first two guaranteed, then 80/60 for melds 3..4.
    private static readonly double[] HolyShieldSuccess =
        { 1, 1, 0.80, 0.60 };

    private static string StatName(MateriaType t) => t switch
    {
        MateriaType.HeavensEye => "Direct Hit Rate",
        MateriaType.Quickarm => "Skill Speed",
        MateriaType.SavageAim => "Critical Hit",
        MateriaType.Piety => "Piety",
        MateriaType.SavageMight => "Determination",
        MateriaType.Quicktongue => "Spell Speed",
        MateriaType.Battledance => "Tenacity",
        _ => t.ToString(),
    };

    public static string Stat(MateriaType t) => StatName(t);

    private static string BaseName(MateriaType t) => t switch
    {
        MateriaType.HeavensEye => "Heavens' Eye",
        MateriaType.Quickarm => "Quickarm",
        MateriaType.SavageAim => "Savage Aim",
        MateriaType.Piety => "Piety",
        MateriaType.SavageMight => "Savage Might",
        MateriaType.Quicktongue => "Quicktongue",
        MateriaType.Battledance => "Battledance",
        _ => t.ToString(),
    };

    private static readonly string[] Roman = { "I", "II", "III", "IV", "V" };

    // The item name as it appears in the Item sheet, e.g. "Savage Aim Materia III".
    public static string MateriaName(MateriaType t, int grade)
        => $"{BaseName(t)} Materia {Roman[Math.Clamp(grade, 1, Roman.Length) - 1]}";

    // The gradeless materia name, e.g. "Savage Aim Materia" (the grade is shown in a
    // separate column in the route view).
    public static string MateriaBaseName(MateriaType t) => $"{BaseName(t)} Materia";

    // ---- Item id resolution (Lumina, by name, cached) ----

    private static readonly Dictionary<(MateriaType, int), uint> IdByTypeGrade = new();
    private static readonly Dictionary<uint, (MateriaType type, int grade)> TypeGradeById = new();
    private static bool _resolved;
    private static long _lastResolveAttemptTicks;
    // Retry cadence while ids are still unresolved. Ensure() no longer latches on failure (so a
    // scan that runs before the Item sheet is ready can succeed on a later call), but Ensure() is
    // hit every framework tick, so without a throttle a PERMANENT mismatch (e.g. a non-English
    // client, where the hardcoded English names never match) would rescan the ~40k-row Item sheet
    // every frame forever. Throttle re-attempts; a normal (English) client resolves on the first try.
    private const long ResolveRetryThrottleMs = 2000;

    public static uint ItemId(MateriaType type, int grade)
    {
        Ensure();
        return IdByTypeGrade.TryGetValue((type, grade), out var id) ? id : 0u;
    }

    public static bool TryResolve(uint itemId, out MateriaType type, out int grade)
    {
        Ensure();
        if (TypeGradeById.TryGetValue(itemId, out var tg))
        {
            type = tg.type;
            grade = tg.grade;
            return true;
        }
        type = default;
        grade = 0;
        return false;
    }

    // Every materia item id the catalog knows (for inventory and retainer scans).
    public static IEnumerable<uint> AllMateriaItemIds()
    {
        Ensure();
        return TypeGradeById.Keys;
    }

    private static void Ensure()
    {
        if (_resolved)
            return;

        // Cap the rescan frequency while unresolved (see _lastResolveAttemptTicks). First call runs
        // immediately (TickCount64 is large from boot); after that, at most once per throttle window.
        var now = Environment.TickCount64;
        if (now - _lastResolveAttemptTicks < ResolveRetryThrottleMs)
            return;
        _lastResolveAttemptTicks = now;

        try
        {
            // Build a name -> rowId map once, then resolve each type/grade by its
            // expected English name. Falls back to the gradeless name for Grade I in
            // case the sheet omits the "I" suffix.
            var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                var n = item.Name.ExtractText();
                if (!string.IsNullOrEmpty(n) && !byName.ContainsKey(n))
                    byName[n] = item.RowId;
            }

            foreach (var type in AllTypes)
            {
                for (var grade = 1; grade <= MaxGrade; grade++)
                {
                    var name = MateriaName(type, grade);
                    uint id = 0;
                    if (byName.TryGetValue(name, out var hit))
                        id = hit;
                    else if (grade == 1 && byName.TryGetValue($"{BaseName(type)} Materia", out var bare))
                        id = bare;

                    if (id != 0)
                    {
                        IdByTypeGrade[(type, grade)] = id;
                        TypeGradeById[id] = (type, grade);
                    }
                }
            }

            // Latch success ONLY once ids actually resolved. Previously _resolved was set true
            // BEFORE this work, so if the first call ran before the Item sheet was ready (the
            // RetainerScanner ticks this from plugin load, well before you open the Novus window)
            // and resolved nothing, the catalog stayed permanently EMPTY: every materia id 0, so
            // Universalis was queried for no items and every route line read "no listing" -- the
            // reported "Universalis lookup not working". Leaving it unlatched retries next call.
            if (IdByTypeGrade.Count > 0)
                _resolved = true;
            else
                Plugin.Log.Warning("Relicable: Novus materia item ids not resolved yet (Item sheet not ready?); will retry.");
        }
        catch (Exception ex)
        {
            // Do NOT latch on failure; retry on the next call once the sheet is available.
            Plugin.Log.Warning($"Relicable: materia catalog resolution failed (will retry): {ex.Message}");
        }
    }

    // ---- Scroll specifications per weapon profile ----

    public static IReadOnlyList<ScrollSpec> GetScrolls(NovusWeaponProfile profile) => profile switch
    {
        NovusWeaponProfile.Healer => new[] { HealerScroll() },
        NovusWeaponProfile.Paladin => new[] { CurtanaScroll(), HolyShieldScroll() },
        _ => new[] { StandardScroll() },
    };

    private static ScrollSpec StandardScroll() => new()
    {
        Name = "Novus",
        TotalPoints = 75,
        DefaultTiers = new[] { 11, 11, 11, 11 },           // cap 44
        TierOverrides = new() { [MateriaType.Piety] = new[] { 11, 11, 9 } }, // cap 31, no Grade IV
        SuccessRates = StandardSuccess,
    };

    private static ScrollSpec HealerScroll() => new()
    {
        Name = "Novus (healer)",
        TotalPoints = 75,
        DefaultTiers = new[] { 11, 11, 11, 11 },
        TierOverrides = new()
        {
            [MateriaType.Piety] = new[] { 11, 11, 11, 11 },  // healers reach Grade IV Piety, cap 44
            [MateriaType.HeavensEye] = new[] { 2, 11, 11, 11 }, // +9 base Direct Hit, cap 35
        },
        SuccessRates = StandardSuccess,
    };

    private static ScrollSpec CurtanaScroll() => new()
    {
        Name = "Curtana Novus",
        TotalPoints = 53,
        DefaultTiers = new[] { 7, 8, 8, 8 },                // cap 31
        // Cap 22: grade III truncated and no grade IV, matching the shape of the other
        // profiles' Piety overrides (standard 44->31, Holy Shield 13->9). The previous
        // {7,8,8} summed to 23, contradicting the cap and letting the optimizer plan a
        // 23rd Piety point the scroll would reject.
        TierOverrides = new() { [MateriaType.Piety] = new[] { 7, 8, 7 } }, // cap 22
        SuccessRates = CurtanaSuccess,
    };

    private static ScrollSpec HolyShieldScroll() => new()
    {
        Name = "Holy Shield Novus",
        TotalPoints = 22,
        DefaultTiers = new[] { 4, 3, 3, 3 },                // cap 13
        TierOverrides = new() { [MateriaType.Piety] = new[] { 4, 3, 2 } }, // cap 9
        SuccessRates = HolyShieldSuccess,
    };
}
