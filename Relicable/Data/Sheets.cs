using System;
using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Small Lumina lookups used at runtime. Uses the current Lumina API
// (IDataManager.GetExcelSheet<T>, GetRowOrDefault). All access is wrapped because
// Lumina member shapes can shift between game/Lumina versions; a failure returns a
// safe default rather than throwing into the controller tick.
public static class Sheets
{
    public static string FateName(uint fateId)
    {
        if (fateId == 0)
            return string.Empty;
        try { return Plugin.DataManager.GetExcelSheet<Fate>().GetRowOrDefault(fateId)?.Name.ExtractText() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static string LeveName(uint leveId)
    {
        if (leveId == 0)
            return string.Empty;
        try { return Plugin.DataManager.GetExcelSheet<Leve>().GetRowOrDefault(leveId)?.Name.ExtractText() ?? string.Empty; }
        catch { return string.Empty; }
    }

    // The mob name for a monster-note target (e.g. "Amalj'aa Thaumaturge").
    public static string MonsterName(uint monsterNoteTargetId)
    {
        if (monsterNoteTargetId == 0)
            return string.Empty;
        try
        {
            var m = Plugin.DataManager.GetExcelSheet<MonsterNoteTarget>().GetRowOrDefault(monsterNoteTargetId);
            return m?.BNpcName.ValueNullable?.Singular.ExtractText() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // The crafting recipe that produces an item, as (recipe row id, crafter name) --
    // e.g. item 1670 "Aeolian Scimitar" -> (1214, "Smithing"). (0, "") when the item is
    // not craftable or the sheet is unavailable. The whole Recipe sheet is folded into a
    // result-item -> recipe map on first use and cached, because the caller (the class-weapon
    // step's Artisan button) would otherwise re-scan ~10k rows per frame.
    public static (uint RecipeId, string CraftJob) RecipeForItem(uint itemId)
    {
        if (itemId == 0)
            return (0u, string.Empty);
        EnsureRecipes();
        return _recipeByResult != null && _recipeByResult.TryGetValue(itemId, out var hit)
            ? hit
            : (0u, string.Empty);
    }

    private static Dictionary<uint, (uint RecipeId, string CraftJob)>? _recipeByResult;

    private static void EnsureRecipes()
    {
        if (_recipeByResult != null)
            return;
        var map = new Dictionary<uint, (uint, string)>();
        try
        {
            foreach (var r in Plugin.DataManager.GetExcelSheet<Recipe>())
            {
                var result = r.ItemResult.RowId;
                // Recipe row 0 is the sheet's empty row; several items have more than one
                // recipe (different crafters) -- the first is the one Artisan's own list UI
                // would offer, so keep it and skip the rest.
                if (r.RowId == 0 || result == 0 || map.ContainsKey(result))
                    continue;
                map[result] = (r.RowId, r.CraftType.ValueNullable?.Name.ExtractText() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            // Leave whatever was gathered; a missing entry just means "no recipe" to the caller.
            Plugin.Log.Warning($"Relicable: recipe lookup build failed: {ex.Message}");
        }
        _recipeByResult = map;
    }

    // World position of a levequest's objective area, from Leve.LevelStart -> Level
    // (X/Y/Z). Used to navigate to an accepted leve. Returns null if unavailable.
    public static Vector3? LeveStartPosition(uint leveId)
    {
        if (leveId == 0)
            return null;
        try
        {
            var leve = Plugin.DataManager.GetExcelSheet<Leve>().GetRowOrDefault(leveId);
            if (leve is not { } l)
                return null;
            if (l.LevelStart.ValueNullable is not { } level)
                return null;
            return new Vector3(level.X, level.Y, level.Z);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Relicable: LeveStartPosition({leveId}) failed: {ex.Message}");
            return null;
        }
    }
}
