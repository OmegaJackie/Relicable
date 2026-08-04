using System.Collections.Generic;
using System.Linq;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Novus;

// "Is the Sphere Scroll finished?" -- the signal that ends the Novus melding work and sends the run
// to Jalzahn for the Animus -> Novus enhancement.
//
// The scroll's infused total is only readable while its RelicSphereScroll window is open
// (RelicMeld.TryReadInfuseTotal), so the answer has to be recorded when it IS open and persisted for
// when it is not. Observe() does that from the framework tick, which means a scroll melded entirely
// BY HAND still registers as complete -- the plugin never has to have driven the melds itself.
//
// The reading is authoritative (it is the game's own counter, not our per-stat parse) and it is
// stored, never latched: a fresh scroll's live 0/75 overwrites a finished 75/75, so a repeat relic
// re-arms the melding work on its own.
public static class NovusScrollState
{
    // The live read is a cheap AtkUnitBase peek, but there is no reason to do it every frame.
    private const long ObserveIntervalMs = 1000;

    private static long _lastObserve;

    // Record the open scroll's infused count against the scroll spec it belongs to. The window does
    // not name its scroll, so it is identified by its max points (Curtana 53, Holy Shield 22, every
    // other profile 75) -- the same discriminator MateriaPlanner.ComputeRoute uses, so Paladin's two
    // scrolls stay independent. No-op when the window is closed or the max matches no known scroll.
    public static void Observe(Configuration config, System.Action? save = null)
    {
        if (System.Environment.TickCount64 - _lastObserve < ObserveIntervalMs)
            return;
        _lastObserve = System.Environment.TickCount64;

        if (!RelicMeld.TryReadInfuseTotal(out var current, out var max))
            return;

        var spec = MateriaCatalog.GetScrolls(config.NovusWeapon).FirstOrDefault(s => s.TotalPoints == max);
        if (spec == null)
            return;

        Record(config, spec.Name, current, max, save);
    }

    // Store one scroll's counter, saving only when it actually moved (this runs on a timer, and the
    // config save is a synchronous JSON write).
    public static void Record(Configuration config, string scrollName, int current, int max, System.Action? save = null)
    {
        var existing = config.ScrollInfusedByScroll.GetValueOrDefault(scrollName);
        if (existing != null && existing.Current == current && existing.Max == max)
            return;

        var wasFull = existing?.IsFull ?? false;
        config.ScrollInfusedByScroll[scrollName] = new ScrollInfusion { Current = current, Max = max };
        save?.Invoke();

        if (!wasFull && max > 0 && current >= max)
            DebugLog.Info($"Sphere Scroll '{scrollName}' is full ({current}/{max}); the Animus -> Novus " +
                          "enhancement at Jalzahn is next.");
    }

    // True once EVERY scroll the configured weapon needs is at its cap -- Paladin has two (Curtana 53
    // + Holy Shield 22) and both must be full before Jalzahn will make the Novus. A scroll that has
    // never been observed counts as not full, so the trip is never taken on no evidence.
    public static bool IsScrollFull(Configuration config)
    {
        var scrolls = MateriaCatalog.GetScrolls(config.NovusWeapon);
        if (scrolls.Count == 0)
            return false;
        foreach (var spec in scrolls)
        {
            var seen = config.ScrollInfusedByScroll.GetValueOrDefault(spec.Name);
            if (seen == null || seen.Current < spec.TotalPoints)
                return false;
        }
        return true;
    }

    // How far along the scrolls are, for the UI and for step guidance ("46/75").
    public static (int Current, int Max) Progress(Configuration config)
    {
        var current = 0;
        var max = 0;
        foreach (var spec in MateriaCatalog.GetScrolls(config.NovusWeapon))
        {
            max += spec.TotalPoints;
            var seen = config.ScrollInfusedByScroll.GetValueOrDefault(spec.Name);
            if (seen != null)
                current += System.Math.Min(seen.Current, spec.TotalPoints);
        }
        return (current, max);
    }

    // Forget the recorded counters (the scrolls are consumed by the Novus trade, so a repeat relic
    // starts from an unknown state rather than an inherited "full").
    public static void Clear(Configuration config, System.Action? save = null)
    {
        if (config.ScrollInfusedByScroll.Count == 0)
            return;
        config.ScrollInfusedByScroll.Clear();
        save?.Invoke();
    }
}
