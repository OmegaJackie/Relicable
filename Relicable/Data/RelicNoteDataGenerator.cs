using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Relicable.Model;

namespace Relicable.Data;

// Generates Animus (Trials of the Braves) objectives by reading the RelicNote
// Excel sheet, rather than hand-authoring them. This uses the CURRENT Lumina API
// (Lumina.Excel.Sheets, struct rows, RowRef links) as bundled in recent Dalamud.
//
// Schema verified against xivdev/EXDSchema RelicNote.yml:
//   EventItem               : RowRef<EventItem>
//   MonsterNoteTargetCommon : Collection<RowRef<MonsterNoteTarget>> (10)
//   MonsterNoteTargetNM     : Collection<RowRef<MonsterNoteTarget>> (3)
//   Fate                    : Collection<RowRef<Fate>>             (3)
//   PlaceNameFate           : Collection<RowRef<PlaceName>>        (3)
//   Leve                    : Collection<RowRef<Leve>>             (3)
//   MonsterCount            : Collection<...>                      (10)
//
// NOTE on the modern Lumina API (changed from the old GeneratedSheets):
//   - sheets come from IDataManager.GetExcelSheet<T>()
//   - rows are value structs; access by sheet.GetRow(id) / TryGetRow / GetRowOrDefault
//   - links are RowRef<T> with .RowId, .IsValid, .Value, .ValueNullable
//   - sub-rows / arrays are Collection<T> you index or enumerate
public static class RelicNoteDataGenerator
{
    public static IReadOnlyList<RelicObjective> Generate(IDataManager data)
    {
        var result = new List<RelicObjective>();

        // Defensive: Lumina member shapes can shift between game/Lumina versions.
        // Any failure here degrades to "no generated objectives" (the static JSON
        // still loads) rather than breaking plugin startup.
        try
        {
        var relicNotes = data.GetExcelSheet<RelicNote>();

        // The set of real dungeon TerritoryTypes (ContentType 2 = Dungeon), plus their
        // display names, built once. A book's dungeon entries (MonsterNoteTargetNM) are
        // resolved to a TerritoryType and validated against this set, so a bogus/unknown
        // territory is skipped instead of being handed to AutoDuty -- clearing a wrong
        // duty would never credit the book slot and would re-run forever.
        var dungeonTerritories = new HashSet<uint>();
        var dungeonNameByTerritory = new Dictionary<uint, string>();
        // Standard (lowest) TerritoryType per dungeon NAME. The authored NM territory can point at a
        // non-standard instance of a dungeon -- a Duty-Support / revised variant that shares the name
        // but has a higher TerritoryType. Killing the boss in a variant does NOT credit the book,
        // which tracks the original, so the resolved territory is normalised to the lowest same-named
        // dungeon. Today every book dungeon name has exactly one CFC row, so this only matters if a
        // same-named duplicate ever appears.
        var standardByName = new Dictionary<string, uint>();
        try
        {
            foreach (var cfc in data.GetExcelSheet<ContentFinderCondition>())
            {
                if (cfc.ContentType.RowId != 2)
                    continue;
                var terr = cfc.TerritoryType.RowId;
                if (terr == 0)
                    continue;
                dungeonTerritories.Add(terr);
                var dn = cfc.Name.ExtractText();
                if (!string.IsNullOrEmpty(dn))
                {
                    if (!dungeonNameByTerritory.ContainsKey(terr))
                        dungeonNameByTerritory[terr] = dn;
                    if (!standardByName.TryGetValue(dn, out var cur) || terr < cur)
                        standardByName[dn] = terr;
                }
            }
        }
        catch { /* leave empty -> dungeon objectives simply are not generated */ }

        foreach (var note in relicNotes)
        {
            if (note.RowId == 0)
                continue;

            // Monster slots: 10 common targets, each requiring 3 kills.
            var common = note.MonsterNoteTargetCommon;
            for (var slot = 0; slot < common.Count; slot++)
            {
                var targetRef = common[slot];
                if (!targetRef.IsValid || targetRef.RowId == 0)
                    continue;

                // Teleport to the mob's zone if derivable. Exact spawn coordinates
                // are not in the sheets, so targeting relies on IsMonsterNoteTarget
                // once nearby.
                var monsterName = Sheets.MonsterName(targetRef.RowId);
                var monsterSteps = new List<StepData>();
                // Prefer the authored BraveBookPositions territory (always correct); fall
                // back to deriving it from the sheets.
                var monsterTerritory = BraveBookPositions.MonsterTerritory(targetRef.RowId);
                if (monsterTerritory == 0)
                    monsterTerritory = Locations.MonsterTerritory(targetRef.RowId);
                var monsterAeth = Locations.AetheryteForTerritory(monsterTerritory);
                if (monsterAeth != 0)
                    monsterSteps.Add(new() { Type = StepType.AetheryteTeleport, AetheryteId = monsterAeth });
                // Authored spawn coordinate (BraveBookPositions) so KillTarget can
                // travel to the spawn and auto-place a flag without a manual flag.
                monsterSteps.Add(new()
                {
                    Type = StepType.KillTarget,
                    Count = 3,
                    TargetName = monsterName,
                    Position = BraveBookPositions.MonsterWorld(targetRef.RowId),
                });

                result.Add(new RelicObjective
                {
                    Stage = RelicStage.Animus,
                    Book = (int)note.RowId,
                    Id = $"animus-{note.RowId}-monster-{slot}",
                    DisplayName = $"Book {note.RowId}: {Name(monsterName, $"monster slot {slot}")}",
                    TargetName = monsterName,
                    Territory = BraveBookPositions.MonsterTerritory(targetRef.RowId),
                    Steps = monsterSteps,
                    Completion = new CompletionCondition
                    {
                        Kind = CompletionKind.MonsterSlot,
                        Slot = slot,
                        Threshold = 3,
                        Book = (int)note.RowId,
                    },
                });
            }

            // FATE slots (3) -> FateSlot completion.
            var fates = note.Fate;
            for (var slot = 0; slot < fates.Count; slot++)
            {
                var fateRef = fates[slot];
                if (!fateRef.IsValid || fateRef.RowId == 0)
                    continue;

                // Teleport to the FATE's zone (from the authored BraveBookPositions territory)
                // so the engine can reach a book FATE that is in a different zone from
                // the monster/leve work -- and so the 3-minute rotation can actually
                // move between the book's FATE zones rather than idling in place. Exact
                // FATE coordinates are still not in the sheets, so once in the zone
                // ParticipateFate travels to the authored staging spot and waits.
                var fateSteps = new List<StepData>();
                var fateTerritory = BraveBookPositions.FateTerritory(fateRef.RowId);
                var fateAeth = Locations.AetheryteForTerritory(fateTerritory);
                if (fateAeth != 0)
                    fateSteps.Add(new() { Type = StepType.AetheryteTeleport, AetheryteId = fateAeth });
                fateSteps.Add(new()
                {
                    Type = StepType.ParticipateFate,
                    FateId = fateRef.RowId,
                    Count = 1,
                    // Authored FATE staging coordinate (BraveBookPositions) so we travel
                    // to the FATE and flag it before it is active.
                    Position = BraveBookPositions.FateWorld(fateRef.RowId),
                    // A predecessor FATE that must be cleared first for this one to spawn (0 = none).
                    // The executor drives the prereq when the target is not yet in the FATE table.
                    PrerequisiteFateId = BraveBookPositions.PrerequisiteFate(fateRef.RowId),
                });

                result.Add(new RelicObjective
                {
                    Stage = RelicStage.Animus,
                    Book = (int)note.RowId,
                    Id = $"animus-{note.RowId}-fate-{slot}",
                    DisplayName = $"Book {note.RowId}: {Name(Sheets.FateName(fateRef.RowId), $"FATE {fateRef.RowId}")}",
                    Territory = fateTerritory,
                    Steps = fateSteps,
                    Completion = new CompletionCondition
                    {
                        Kind = CompletionKind.FateSlot,
                        Slot = slot,
                        Book = (int)note.RowId,
                    },
                });
            }

            // Leve slots (3) -> LeveSlot completion. Leves are fully derivable:
            // teleport to the levemete's zone and StartLeve handles the levemete.
            var leves = note.Leve;
            for (var slot = 0; slot < leves.Count; slot++)
            {
                var leveRef = leves[slot];
                if (!leveRef.IsValid || leveRef.RowId == 0)
                    continue;

                var leveSteps = new List<StepData>();
                var lm = Locations.LeveLevemete(leveRef.RowId);
                if (lm is { } m)
                {
                    var leveAeth = Locations.AetheryteForTerritory(m.Territory);
                    if (leveAeth != 0)
                        leveSteps.Add(new() { Type = StepType.AetheryteTeleport, AetheryteId = leveAeth });
                    leveSteps.Add(new()
                    {
                        Type = StepType.StartLeve,
                        LeveId = leveRef.RowId,
                        LevemeteDataId = m.NpcId,
                        Position = m.Pos,
                    });
                }
                else
                {
                    leveSteps.Add(new() { Type = StepType.StartLeve, LeveId = leveRef.RowId });
                }

                result.Add(new RelicObjective
                {
                    Stage = RelicStage.Animus,
                    Book = (int)note.RowId,
                    Id = $"animus-{note.RowId}-leve-{slot}",
                    DisplayName = $"Book {note.RowId}: {Name(Sheets.LeveName(leveRef.RowId), $"Leve {leveRef.RowId}")}",
                    Steps = leveSteps,
                    Completion = new CompletionCondition
                    {
                        Kind = CompletionKind.LeveSlot,
                        Slot = slot,
                        Book = (int)note.RowId,
                    },
                });
            }

            // Dungeon slots (3) -> DungeonSlot completion. The book's dungeon entries
            // are the MonsterNoteTargetNM "notorious monsters" -- the final bosses of
            // instanced ARR dungeons -- so they cannot be walked to in the open world.
            // Each is handed to AutoDuty via an unsynced EnterDuty (the same IPC path the
            // base-relic trials and Braves dungeons use); clearing the dungeon defeats the
            // boss and credits the book's dungeon slot. The dungeon's TerritoryType is
            // resolved from the boss's zone (game sheet) with the authored BraveBookPositions
            // territory as a fallback, validated against the real dungeon list; an
            // unresolved entry is skipped rather than queueing a wrong duty.
            var nmTargets = note.MonsterNoteTargetNM;
            for (var slot = 0; slot < nmTargets.Count; slot++)
            {
                var nmRef = nmTargets[slot];
                if (!nmRef.IsValid || nmRef.RowId == 0)
                    continue;

                var dungeonTerritory = ResolveDungeonTerritory(nmRef.RowId, dungeonTerritories);
                if (dungeonTerritory == 0)
                {
                    Plugin.Log.Warning($"Relicable: RelicNote book {note.RowId} dungeon slot {slot} " +
                        $"(NM target {nmRef.RowId}) did not resolve to a known dungeon TerritoryType; " +
                        "skipping (no dungeon objective generated for this slot).");
                    continue;
                }

                // Normalise to the ORIGINAL instance of this dungeon: the authored NM territory can
                // point at a higher-numbered same-named variant (Duty-Support / revised, e.g.
                // "Copperbell Mines" 1038) whose boss kill does NOT credit the book. The book tracks
                // the original ARR dungeon, which always has the lowest same-named TerritoryType.
                if (dungeonNameByTerritory.TryGetValue(dungeonTerritory, out var resolvedName)
                    && standardByName.TryGetValue(resolvedName, out var standardTerr)
                    && standardTerr != dungeonTerritory)
                {
                    Diagnostics.DebugLog.Verbose($"book {note.RowId} dungeon '{resolvedName}' resolved to " +
                        $"variant TerritoryType {dungeonTerritory}; using the original {standardTerr} so the clear credits.");
                    dungeonTerritory = standardTerr;
                }

                var bossName = Sheets.MonsterName(nmRef.RowId);
                var dungeonName = dungeonNameByTerritory.TryGetValue(dungeonTerritory, out var dn)
                    ? dn : $"dungeon {dungeonTerritory}";

                result.Add(new RelicObjective
                {
                    Stage = RelicStage.Animus,
                    Book = (int)note.RowId,
                    Id = $"animus-{note.RowId}-dungeon-{slot}",
                    DisplayName = $"Book {note.RowId}: {dungeonName}"
                        + (string.IsNullOrEmpty(bossName) ? " (dungeon)" : $" (dungeon boss: {bossName})"),
                    TargetName = bossName,
                    Steps = new List<StepData>
                    {
                        // Equip the relic FIRST: the ARR book dungeon credit is an equip-check --
                        // the Atma/Zodiac weapon must be equipped on the killing player at the moment
                        // the dungeon's final boss (the notorious monster) dies, or no credit is sent
                        // at all (like the Zeta/Mahatma charge). Without this, an unsynced clear on a
                        // different weapon "does not count" no matter how long we poll for the credit.
                        new() { Type = StepType.EnsureRelicEquipped },
                        // Unsynced so AutoDuty can solo the old ARR dungeon; killing the final boss
                        // with the relic equipped credits the book's dungeon slot. AutoDuty
                        // queues/travels itself, so no teleport step is needed.
                        new() { Type = StepType.EnterDuty, TerritoryType = dungeonTerritory, Loops = 1, Unsynced = true },
                    },
                    Completion = new CompletionCondition
                    {
                        Kind = CompletionKind.DungeonSlot,
                        Slot = slot,
                        Book = (int)note.RowId,
                    },
                });
            }
        }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Relicable: RelicNote generation failed ({ex.Message}); using static data only");
        }

        return result;
    }

    private static string Name(string resolved, string fallback)
        => string.IsNullOrEmpty(resolved) ? fallback : resolved;

    // Resolve an NM (dungeon-boss) target to its dungeon TerritoryType, validated against
    // the real dungeon set. Prefer the game sheet -- the boss's zone place name is the
    // game's own link to the dungeon -- then the authored BraveBookPositions territory. Returns 0
    // when neither is a known dungeon, so the caller skips the slot rather than handing
    // AutoDuty a wrong (or non-dungeon) territory.
    private static uint ResolveDungeonTerritory(uint nmTargetId, HashSet<uint> dungeonTerritories)
    {
        var sheet = Locations.MonsterTerritory(nmTargetId);
        if (sheet != 0 && dungeonTerritories.Contains(sheet))
            return sheet;
        var authored = BraveBookPositions.MonsterTerritory(nmTargetId);
        if (authored != 0 && dungeonTerritories.Contains(authored))
            return authored;
        return 0;
    }
}
