using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Relicable.Model;

namespace Relicable.BaseRelic;

// Resolves the base-relic content's English names to game ids via Lumina, once,
// cached. Mirrors MateriaCatalog: build a name -> row-id map from the relevant sheet
// and look each referenced name up, so nothing is hardcoded to a numeric id.
//
// Two sheets are used:
//   Item       -> normal inventory items (materia, weapons, crafting mats, vendor
//                 consumables, trial drops, the relic rewards).
//   EventItem  -> key items (the Amdapor Glyph lives in the Key Items container).
//
// Name matching is case-insensitive and apostrophe-insensitive (the sheets and the
// wiki can disagree on U+0027 vs U+2019), so "Wildling's Cesti" matches regardless of
// which apostrophe glyph each side uses. Names that resolve in neither sheet are
// recorded in UnresolvedNames and surface as Unknown in the report rather than
// throwing -- the same fail-safe posture the rest of the data layer takes.
public static class BaseRelicCatalog
{
    private static readonly Dictionary<string, uint> ItemIdByName = new();
    private static readonly Dictionary<string, uint> KeyItemIdByName = new();
    private static readonly Dictionary<string, uint> QuestIdByName = new();
    // Case-insensitive: the ContentFinderCondition Name field stores duty titles with a
    // lowercase leading article ("the Bowl of Embers (Hard)"), but the objective tables use
    // the displayed "The ...". A case-sensitive map silently failed to resolve the trials, so
    // they were never generated and the run halted mid-quest with "no objective remains".
    private static readonly Dictionary<string, uint> DutyIdByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, uint> DutyTerritoryByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, uint> DutyContentByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<uint> RelicQuestRowIds = new();
    private static readonly List<string> Unresolved = new();
    private static IReadOnlyList<uint> _materialIds = Array.Empty<uint>();
    private static bool _resolved;

    // Item-sheet id for a referenced name, or 0 if unresolved.
    public static uint ItemId(string name)
    {
        Ensure();
        return ItemIdByName.TryGetValue(Key(name), out var id) ? id : 0u;
    }

    // EventItem-sheet (key item) id for a referenced name, or 0 if unresolved.
    public static uint KeyItemId(string name)
    {
        Ensure();
        return KeyItemIdByName.TryGetValue(Key(name), out var id) ? id : 0u;
    }

    // Quest-sheet row id for a quest title, or 0 if unresolved. Pass the full row id to
    // QuestManager.IsQuestComplete / GetQuestSequence; they mask it to a ushort.
    public static uint QuestId(string questName)
    {
        Ensure();
        return QuestIdByName.TryGetValue(Key(questName), out var id) ? id : 0u;
    }

    // InstanceContent row id for a duty (ContentFinderCondition) name, or 0 if
    // unresolved. Pass it to GameState.IsDutyUnlocked / IsDutyCompleted.
    public static uint DutyInstanceContentId(string dutyName)
    {
        Ensure();
        return DutyIdByName.TryGetValue(Key(dutyName), out var id) ? id : 0u;
    }

    // TerritoryType row id for a duty (ContentFinderCondition) name, or 0 if unresolved.
    // Used as the EnterDuty step's TerritoryType so AutoDuty can queue the trial.
    public static uint DutyTerritoryId(string dutyName)
    {
        Ensure();
        return DutyTerritoryByName.TryGetValue(Key(dutyName), out var t) ? t : 0u;
    }

    // InstanceContent row id for ANY duty (ContentFinderCondition) name (built for all rows,
    // not only quest-referenced ones), or 0. Used for the one-time-duty completion guard on
    // the Hydra battle, whose name is not among the quest-referenced duty names.
    public static uint DutyContentIdAny(string dutyName)
    {
        Ensure();
        return DutyContentByName.TryGetValue(Key(dutyName), out var id) ? id : 0u;
    }

    // Every base-relic quest row id (all "A Relic Reborn ..." rows). Used to detect an
    // active relic quest without depending on an exact per-job title match.
    public static IReadOnlyList<uint> RelicQuestRowIdList()
    {
        Ensure();
        return RelicQuestRowIds;
    }

    // Every resolved material item id (the meld materia, crafting ingredients, and
    // shared consumables across all jobs). Used to scan retainers and bags.
    public static IReadOnlyCollection<uint> AllMaterialItemIds()
    {
        Ensure();
        return _materialIds;
    }

    // Names that could not be resolved in either sheet (diagnostics; verify in-game).
    public static IReadOnlyList<string> UnresolvedNames()
    {
        Ensure();
        return Unresolved;
    }

    // Canonicalize a name for matching: trim, and fold the two typographic single
    // quotes (U+2019, U+2018) to the ASCII apostrophe so the wiki and the sheet agree.
    private static string Key(string s)
        => (s ?? string.Empty).Trim().Replace('\u2019', '\'').Replace('\u2018', '\'');

    private static void Ensure()
    {
        if (_resolved)
            return;
        _resolved = true;

        try
        {
            // ---- Build sheet name -> id maps once ----
            var items = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                var n = Key(item.Name.ExtractText());
                if (!string.IsNullOrEmpty(n) && !items.ContainsKey(n))
                    items[n] = item.RowId;
            }

            var keyItems = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in Plugin.DataManager.GetExcelSheet<EventItem>())
            {
                var n = Key(ev.Name.ExtractText());
                if (!string.IsNullOrEmpty(n) && !keyItems.ContainsKey(n))
                    keyItems[n] = ev.RowId;
            }

            var quests = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in Plugin.DataManager.GetExcelSheet<Quest>())
            {
                var n = Key(q.Name.ExtractText());
                if (string.IsNullOrEmpty(n))
                    continue;
                if (!quests.ContainsKey(n))
                    quests[n] = q.RowId;
                // Collect every base-relic quest row ("A Relic Reborn ..."). There is one
                // per job; gathering them by prefix makes active-quest detection robust
                // whether or not the sheet name carries the per-job weapon suffix.
                if (q.RowId != 0 && n.StartsWith("A Relic Reborn", StringComparison.OrdinalIgnoreCase))
                    RelicQuestRowIds.Add(q.RowId);
            }

            // ---- Resolve every referenced item name ----
            foreach (var name in ReferencedItemNames())
            {
                var k = Key(name);
                if (ItemIdByName.ContainsKey(k) || KeyItemIdByName.ContainsKey(k))
                    continue;
                if (items.TryGetValue(k, out var iid))
                    ItemIdByName[k] = iid;
                else if (keyItems.TryGetValue(k, out var kid))
                    KeyItemIdByName[k] = kid;
                else
                    Unresolved.Add(name);
            }

            // ---- Resolve every referenced quest name ----
            foreach (var name in ReferencedQuestNames())
            {
                var k = Key(name);
                if (QuestIdByName.ContainsKey(k))
                    continue;
                if (quests.TryGetValue(k, out var qid))
                    QuestIdByName[k] = qid;
                else
                    Unresolved.Add($"(quest) {name}");
            }

            // ---- Resolve referenced duty names -> InstanceContent id ----
            // ContentFinderCondition.Content references the InstanceContent for a duty
            // (the same RowRef shape DutyInfo.cs reads for ContentType/TerritoryType).
            var duties = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfc in Plugin.DataManager.GetExcelSheet<ContentFinderCondition>())
            {
                var n = Key(cfc.Name.ExtractText());
                if (string.IsNullOrEmpty(n))
                    continue;
                var content = cfc.Content.RowId;
                if (content != 0 && !duties.ContainsKey(n))
                    duties[n] = content;
                // TerritoryType for any duty name, so the trial objectives can be queued
                // by AutoDuty (the EnterDuty step uses TerritoryType). Built for all rows.
                if (!DutyTerritoryByName.ContainsKey(n))
                    DutyTerritoryByName[n] = cfc.TerritoryType.RowId;
                // InstanceContent id for any duty name, so a one-time quest duty (the Hydra)
                // can be checked for completion even though it is not a quest-referenced name.
                if (content != 0 && !DutyContentByName.ContainsKey(n))
                    DutyContentByName[n] = content;
            }
            foreach (var name in ReferencedDutyNames())
            {
                var k = Key(name);
                if (DutyIdByName.ContainsKey(k))
                    continue;
                if (duties.TryGetValue(k, out var did))
                    DutyIdByName[k] = did;
                else
                    Unresolved.Add($"(duty) {name}");
            }

            // ---- Cache the material id set for retainer/bag scans ----
            var matIds = new HashSet<uint>();
            foreach (var job in RelicJobs.All)
                foreach (var m in BaseRelicData.MaterialsFor(job))
                {
                    var id = ItemId(m.ItemName);
                    if (id != 0)
                        matIds.Add(id);
                }
            _materialIds = matIds.ToList();

            if (Unresolved.Count > 0)
                Plugin.Log.Warning($"Relicable: base-relic catalog has {Unresolved.Count} unresolved name(s): {string.Join(", ", Unresolved)}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Relicable: base-relic catalog resolution failed: {ex.Message}");
        }
    }

    // Distinct item names referenced anywhere in the base-relic content.
    private static IEnumerable<string> ReferencedItemNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in RelicJobs.All)
        {
            var data = BaseRelicData.For(job);
            if (data == null)
                continue;
            Add(names, data.ClassWeaponName);
            Add(names, data.RelicWeaponName);
            Add(names, data.SecondaryRewardName);
            foreach (var m in BaseRelicData.MaterialsFor(job))
                Add(names, m.ItemName);
        }

        foreach (var part in BaseRelicData.GlobalParts)
        {
            Add(names, part.HaveItemName);
            foreach (var it in part.Items)
                Add(names, it.ItemName);
        }

        return names;
    }

    private static IEnumerable<string> ReferencedQuestNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BaseRelicData.UnlockQuestName,
            // Resolved for the informational (non-gating) MSQ context line in the report.
            BaseRelicData.MsqGateQuestName,
        };
        // The relic quest is per job ("A Relic Reborn (<weapon>)"); resolve all ten.
        foreach (var n in BaseRelicData.AllRelicQuestNames())
            names.Add(n);
        foreach (var p in BaseRelicData.GlobalPrerequisites)
            names.Add(p.QuestName);
        return names;
    }

    // Distinct duty (ContentFinderCondition) names referenced by the quest parts.
    private static IEnumerable<string> ReferencedDutyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in BaseRelicData.GlobalParts)
            Add(names, part.DutyName);
        return names;
    }

    private static void Add(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            set.Add(value);
    }
}
