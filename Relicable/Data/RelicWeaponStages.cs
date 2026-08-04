using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;
using Relicable.BaseRelic;
using Relicable.Model;

namespace Relicable.Data;

// Maps a relic weapon's item id to the relic stage that weapon represents, by resolving
// the well-known item names via Lumina: the base 2-star relic, its "Unfinished" form,
// and Zenith all map to RelicStage.Relic; "<base> Atma/Animus/Novus/Nexus" map to their
// stage. This is the same name-driven, fail-safe approach the rest of the data layer uses
// (BaseRelicCatalog, NovusData), so no numeric item ids are hardcoded here.
//
// The weapon a player holds is the authoritative record of progress: every upgrade
// hands back a new item, so holding e.g. "Stardust Rod Nexus" proves every stage up
// to and including Nexus is finished. The controller uses this to keep an endless
// lower-stage farm -- whose inventory-based completion is intentionally re-armable
// (the Novus Alexandrite farm in particular never reads "complete" once the held
// Alexandrite is consumed) -- from parking Auto-mode selection below the player's
// real progress.
//
// The il125 "Zodiac Braves" weapon and the il135 "Zeta" weapon are absent here: both carry
// UNIQUE per-job names, not "<base> <tier>" (Excalibur / Yoichi Bow / Ragnarok ... and their
// "<name> Zeta" finals), so GameState recognizes them from its own verified BravesRelicItemIds
// / ZetaRelicItemIds tables instead. (Building "<base> Zeta" here was the source of the
// "unresolved names" warning, since no such item exists.) Names that resolve to nothing are
// logged and skipped, so a single missing entry degrades to "no detection" rather than throwing.
public static class RelicWeaponStages
{
    private static readonly Dictionary<uint, RelicStage> StageById = new();
    // The FINISHED bare base-relic ids (not "Unfinished <name>", not "<name> Zenith") -- the
    // exact form that still awaits the Zenith trade at the Furnace -- each mapped to its
    // Thavnairian Mist cost there. Verified against SpecialShop row 1769484 ("Relic
    // Enhancement", the Furnace's shop): every solo main hand trades for 3 mists, but the
    // Paladin pair is TWO separate entries, Curtana + 2 mists and Holy Shield + 1 mist
    // (3 mists total for the full set; no entry yields two items).
    private static readonly Dictionary<uint, int> MistCostByBareId = new();
    // The "<base> Zenith" (il90) form ids -- the weapon turned in at Jalzahn for the Atma
    // enhancement. Kept apart from the bare ids because the Atma turn-in consumes THIS form
    // (not the bare relic), and the menu must pick it by name from the bags (it is unequipped).
    private static readonly HashSet<uint> ZenithFormIds = new();
    // The "Unfinished <base>" form ids. This form exists ONLY inside the 'A Relic Reborn' quest --
    // Gerolt forges it at sequence 9 and takes it back at 14 -- and it is the weapon the quest
    // means by "arm yourself with the unfinished <weapon>". Tracked separately because it is the
    // ONLY form whose beastman kills and Hydra clear credit the quest: any other tier of the same
    // job's relic is equippable, reads as RelicStage.Relic, and credits NOTHING.
    private static readonly HashSet<uint> UnfinishedFormIds = new();
    // bare base relic id -> the "<base> Zenith" id its Furnace trade hands back. The Zenith trade
    // is driven by picking the shop row that YIELDS this item, so the automation never fires at a
    // row it cannot positively identify.
    private static readonly Dictionary<uint, uint> ZenithFormByBareId = new();
    // relic weapon item id -> the job whose line it belongs to, for EVERY tier of that job's
    // weapon (base, Unfinished, Zenith, Atma, Animus, Novus, Nexus). A relic can only be equipped
    // by its own job, so the auto-equip has to know which one it is holding: picking the first
    // relic in the armoury regardless of job means trying to equip another job's weapon, which the
    // game silently refuses -- and then the hunt runs with no relic on and the kills do not credit.
    private static readonly Dictionary<uint, RelicJob> JobByItemId = new();
    private static bool _resolved;
    private static long _lastResolveAttemptTicks;
    // Retry cadence while the stage map is still empty. Ensure() no longer latches BEFORE the work
    // (see below), so a call made before the Item sheet is ready cannot leave the map permanently
    // empty; but Ensure() is hit from the controller tick, so throttle the rescan so a permanent
    // mismatch (e.g. a non-English client) cannot rescan the ~40k-row Item sheet every frame.
    private const long ResolveRetryThrottleMs = 2000;

    // Upgrade suffix -> the stage that upgrade proves complete. These tiers are all named
    // "<base> <suffix>". The final Zeta tier is NOT (it takes the Braves weapon's unique name),
    // so it is recognized by GameState.ZetaRelicItemIds, not here.
    private static readonly (string Suffix, RelicStage Stage)[] Tiers =
    {
        ("Atma", RelicStage.Atma),
        ("Animus", RelicStage.Animus),
        ("Novus", RelicStage.Novus),
        ("Nexus", RelicStage.Nexus),
    };

    // The stage a relic weapon item id represents (Relic for the base/Unfinished/Zenith
    // forms, Atma..Zeta for the upgrades), or None when the id is not a recognized relic
    // weapon.
    public static RelicStage StageOf(uint itemId)
    {
        Ensure();
        return StageById.TryGetValue(itemId, out var stage) ? stage : RelicStage.None;
    }

    // True when the item id is a FINISHED bare base relic -- the Zenith-pending form. The
    // "Unfinished <name>" (mid A Relic Reborn) and "<name> Zenith" (already upgraded) forms
    // are different item ids and never match, so no name test on the held item is needed.
    public static bool IsBareBaseRelic(uint itemId)
    {
        Ensure();
        return MistCostByBareId.ContainsKey(itemId);
    }

    // The Thavnairian Mist this bare base relic's own Furnace trade costs (3 / 2 / 1, see
    // MistCostByBareId), or 0 when the id is not a bare base relic.
    public static int ZenithMistCost(uint itemId)
    {
        Ensure();
        return MistCostByBareId.TryGetValue(itemId, out var cost) ? cost : 0;
    }

    // True when the id is a "<base> Zenith" weapon -- the il90 form traded (unequipped) to
    // Jalzahn, with 12 atmas, for the il100 Atma weapon.
    public static bool IsZenithWeapon(uint itemId)
    {
        Ensure();
        return ZenithFormIds.Contains(itemId);
    }

    // True for an "Unfinished <base>" weapon -- the form 'A Relic Reborn' hands over at sequence 9
    // and takes back at 14. Holding one is proof the base-relic quest is mid-flight, and it is the
    // only weapon whose kills credit that quest's beastman hunt.
    public static bool IsUnfinishedForm(uint itemId)
    {
        Ensure();
        return UnfinishedFormIds.Contains(itemId);
    }

    // The "<base> Zenith" weapon a bare base relic's Furnace trade yields, or 0 when the id is not
    // a bare base relic (or its Zenith form did not resolve). The Zenith trade uses this to find
    // its row in the Furnace's shop BY RESULT, so it can never pick a neighbouring job's trade.
    public static uint ZenithFormFor(uint bareItemId)
    {
        Ensure();
        return ZenithFormByBareId.TryGetValue(bareItemId, out var id) ? id : 0u;
    }

    private static void Ensure()
    {
        if (_resolved)
            return;

        // Do NOT latch before the work: setting _resolved=true up front meant a first call made
        // before the Item sheet was ready (or one that threw) left StageById permanently EMPTY, so
        // every equipped relic read RelicStage.None and Auto selection mis-routed. Throttle rescans
        // while unresolved; a normal (English) client resolves fully on the first attempt.
        var now = Environment.TickCount64;
        if (now - _lastResolveAttemptTicks < ResolveRetryThrottleMs)
            return;
        _lastResolveAttemptTicks = now;

        try
        {
            // Build a canonicalized name -> Item row id map once from the Item sheet.
            var itemsByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                var n = Key(item.Name.ExtractText());
                if (!string.IsNullOrEmpty(n) && !itemsByName.ContainsKey(n))
                    itemsByName[n] = item.RowId;
            }

            var unresolved = new List<string>();
            foreach (var (baseName, mistCost, owningJob) in BaseWeaponNames())
            {
                // Every form of this weapon belongs to the one job that can equip it.
                void NoteJob(string name)
                {
                    if (owningJob != RelicJob.None
                        && itemsByName.TryGetValue(Key(name), out var jid) && jid != 0)
                        JobByItemId[jid] = owningJob;
                }
                NoteJob(baseName);
                NoteJob("Unfinished " + baseName);
                NoteJob("Unfinished " + StripThe(baseName));
                NoteJob($"{baseName} Zenith");
                foreach (var (suffix, _) in Tiers)
                    NoteJob($"{baseName} {suffix}");

                // The base 2-star relic (il80), its "Unfinished" form (equipped during
                // 'A Relic Reborn' for the beastman hunt and the unfinished-relic trials),
                // and Zenith (il90; the enum has no separate Zenith) all count as the Relic
                // stage. The Unfinished item drops a leading "The " for the Summoner relic
                // ("Unfinished Veil of Wiyu", not "Unfinished The Veil of Wiyu"), so both
                // forms are tried; the upgrade names keep "The", so only Unfinished needs it.
                MapFirst(itemsByName, RelicStage.Relic, unresolved, baseName);
                // Remember the bare (finished) form's id with its Furnace mist cost: it is the
                // Zenith-pending item that the inventory/armoury scan looks for (IsBareBaseRelic).
                if (itemsByName.TryGetValue(Key(baseName), out var bareId) && bareId != 0)
                    MistCostByBareId[bareId] = mistCost;
                // The "Unfinished" form is optional: the Paladin's Holy Shield off-hand has no
                // unfinished version, so a miss is expected data, not a gap -> do not warn.
                var unfinishedId = MapOptional(itemsByName, RelicStage.Relic,
                    "Unfinished " + baseName, "Unfinished " + StripThe(baseName));
                if (unfinishedId != 0)
                    UnfinishedFormIds.Add(unfinishedId);
                MapFirst(itemsByName, RelicStage.Relic, unresolved, $"{baseName} Zenith");
                // Remember the Zenith form's id (the Atma turn-in item, picked by name at Jalzahn),
                // and which bare relic's Furnace trade produces it (the Zenith trade's row match).
                if (itemsByName.TryGetValue(Key($"{baseName} Zenith"), out var zenId) && zenId != 0)
                {
                    ZenithFormIds.Add(zenId);
                    if (itemsByName.TryGetValue(Key(baseName), out var bare) && bare != 0)
                        ZenithFormByBareId[bare] = zenId;
                }

                foreach (var (suffix, stage) in Tiers)
                    MapFirst(itemsByName, stage, unresolved, $"{baseName} {suffix}");
            }

            if (unresolved.Count > 0)
                Plugin.Log.Warning(
                    $"Relicable: relic-weapon stage map has {unresolved.Count} unresolved name(s): {string.Join(", ", unresolved)}");

            // Latch only once the map actually resolved something; otherwise retry on a later call
            // (throttled above) instead of latching an empty map for the whole session.
            if (StageById.Count > 0)
                _resolved = true;
            else
                Plugin.Log.Warning("Relicable: relic-weapon stage map still empty (Item sheet not ready?); will retry.");
        }
        catch (Exception ex)
        {
            // Do NOT latch on failure; retry once the sheet is available.
            Plugin.Log.Warning($"Relicable: relic-weapon stage resolution failed (will retry): {ex.Message}");
        }
    }

    // The eleven base relic weapon names (ten job weapons plus the Paladin's Holy
    // Shield off-hand), taken from the base-relic data so the names live in one place,
    // each with its bare form's Thavnairian Mist cost at the Furnace (SpecialShop row
    // 1769484): a solo main hand costs 3; a main hand WITH a secondary reward splits the
    // set's 3 mists across two trades, 2 for the main hand and 1 for the off-hand (the
    // Paladin's Curtana + Holy Shield -- the only such pair in the data).
    private static IEnumerable<(string Name, int MistCost, RelicJob Job)> BaseWeaponNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in BaseRelicData.ByJob)
        {
            var (job, data) = (kv.Key, kv.Value);
            var hasSecondary = !string.IsNullOrEmpty(data.SecondaryRewardName);
            if (!string.IsNullOrEmpty(data.RelicWeaponName) && names.Add(data.RelicWeaponName))
                yield return (data.RelicWeaponName, hasSecondary ? 2 : 3, job);
            if (!string.IsNullOrEmpty(data.SecondaryRewardName) && names.Add(data.SecondaryRewardName))
                yield return (data.SecondaryRewardName, 1, job);
        }
    }

    // The job whose relic line an item id belongs to, or None when the id is not a name-derived
    // relic weapon (the il125 Braves and il135 Zeta finals carry unique names and are recognized
    // from GameState's own id tables, so they are not job-mapped here). Used by the auto-equip so
    // it never tries to put another job's relic in your hands.
    public static RelicJob JobOf(uint itemId)
    {
        Ensure();
        return JobByItemId.TryGetValue(itemId, out var j) ? j : RelicJob.None;
    }

    // Map the first candidate name that resolves in the Item sheet to the given stage;
    // if none resolve, record the primary candidate as unresolved (diagnostics only).
    private static void MapFirst(Dictionary<string, uint> itemsByName, RelicStage stage, List<string> unresolved, params string[] candidates)
    {
        foreach (var c in candidates)
            if (itemsByName.TryGetValue(Key(c), out var id) && id != 0)
            {
                StageById[id] = stage;
                return;
            }
        unresolved.Add(candidates[0]);
    }

    // Like MapFirst but for forms that legitimately may not exist (e.g. the Holy Shield has no
    // "Unfinished" version): map the first candidate that resolves and return its id, and record
    // nothing when none do (returning 0), so an expected absence does not surface as an
    // "unresolved names" warning.
    private static uint MapOptional(Dictionary<string, uint> itemsByName, RelicStage stage, params string[] candidates)
    {
        foreach (var c in candidates)
            if (itemsByName.TryGetValue(Key(c), out var id) && id != 0)
            {
                StageById[id] = stage;
                return id;
            }
        return 0u;
    }

    // Drop a leading "The " (used to derive the Summoner unfinished name "Unfinished Veil
    // of Wiyu" from the base "The Veil of Wiyu").
    private static string StripThe(string s)
        => s.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? s.Substring(4) : s;

    // Canonicalize for matching: trim only. Unlike the base-relic material names
    // (which include apostrophes, e.g. "Wildling's Cesti"), none of the relic weapon
    // names contain an apostrophe, so no glyph folding is needed here.
    private static string Key(string s)
        => (s ?? string.Empty).Trim();
}
