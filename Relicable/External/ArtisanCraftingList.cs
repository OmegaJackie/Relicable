using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Lumina.Excel.Sheets;

namespace Relicable.External;

// Creates a real Artisan crafting list (recipe + every pre-craft) and saves it into Artisan.
//
// WHY REFLECTION AND NOT IPC: Artisan's registered IPC surface (verified against the installed
// Artisan.dll, 4.0.5.17) is CraftItem / IsBusy / GetEnduranceStatus / SetEnduranceStatus /
// IsListRunning / IsListPaused / SetListPause / GetStopRequest / SetStopRequest / GetLists /
// StartListById / the Change*/SetTemp* solver knobs. It can RUN an existing list by id and it
// can queue a single Endurance craft, but there is no gate that CREATES a list -- so building
// one means calling Artisan's own public list API in its loaded assembly, the same
// AppDomain.CurrentDomain.GetAssemblies() approach RsrRotationOverride / RsrTargetingOverride
// already use for RSR here.
//
// The three members used are all PUBLIC STATIC in Artisan and are exactly what its own
// "new list" popup calls (verified by decompiling Artisan 4.0.5.17):
//   CraftingLists.CraftingListFunctions.SetID(NewCraftingList)             -- unique list id
//   CraftingLists.CraftingListUI.AddAllSubcrafts(Recipe, NewCraftingList, int amounts, int loops)
//                                                                          -- recursive pre-crafts
//   CraftingLists.CraftingListFunctions.Save(NewCraftingList, bool isNew)  -- adds to
//                                       Artisan.P.Config.NewCraftingLists and saves the config
// AddAllSubcrafts adds only the SUB-recipes, so the top recipe is appended here afterwards
// (merging into an existing row if the pre-craft walk already produced one).
//
// Every step is guarded: an absent/renamed member degrades to "unavailable" with a reason for
// the caller to show, never an exception into the UI draw.
public sealed class ArtisanCraftingList
{
    private const string ArtisanAssembly = "Artisan";
    private const long ResolveRetryMs = 10_000;

    private MethodInfo? _setId;
    private MethodInfo? _save;
    private MethodInfo? _addAllSubcrafts;
    private Type? _listType;
    private Type? _listItemType;
    private PropertyInfo? _listName;
    private PropertyInfo? _listRecipes;
    private PropertyInfo? _itemId;
    private PropertyInfo? _itemQuantity;
    private long _nextResolveTicks;

    // True when Artisan is loaded and its list API resolved, i.e. a list can actually be created.
    public bool Available => Resolve(out _);

    // Build "<listName>" containing `amount` of `recipeId` plus every pre-craft, and save it into
    // Artisan. Returns false with a human-readable reason on any failure.
    public bool TryCreate(string listName, uint recipeId, int amount, out string error)
    {
        error = string.Empty;
        if (recipeId == 0)
        {
            error = "that item has no crafting recipe.";
            return false;
        }
        if (amount <= 0)
            amount = 1;
        if (!Resolve(out error))
            return false;

        try
        {
            // The recipe row is passed by value into Artisan's own API; both plugins bind
            // Lumina.Excel from Dalamud, so the struct type identity matches.
            if (Plugin.DataManager.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId) is not { } recipe)
            {
                error = $"recipe {recipeId} is not in the Recipe sheet.";
                return false;
            }

            var list = Activator.CreateInstance(_listType!);
            if (list == null)
            {
                error = "Artisan's crafting-list type could not be constructed.";
                return false;
            }
            _listName!.SetValue(list, listName);
            _setId!.Invoke(null, new[] { list });

            // Pre-crafts first (recursive: intermediates of intermediates), then the weapon itself.
            _addAllSubcrafts!.Invoke(null, new object[] { recipe, list, amount, 1 });

            if (_listRecipes!.GetValue(list) is not IList rows)
            {
                error = "Artisan's crafting-list rows could not be read.";
                return false;
            }

            var merged = false;
            foreach (var row in rows)
            {
                if (row == null || Convert.ToUInt32(_itemId!.GetValue(row)) != recipeId)
                    continue;
                _itemQuantity!.SetValue(row, Convert.ToInt32(_itemQuantity.GetValue(row)) + amount);
                merged = true;
                break;
            }
            if (!merged)
            {
                var row = Activator.CreateInstance(_listItemType!);
                if (row == null)
                {
                    error = "Artisan's crafting-list row type could not be constructed.";
                    return false;
                }
                _itemId!.SetValue(row, recipeId);
                _itemQuantity!.SetValue(row, amount);
                rows.Add(row);
            }

            var saved = _save!.Invoke(null, new object[] { list, true });
            if (saved is bool ok && !ok)
            {
                error = "Artisan rejected the list.";
                return false;
            }

            Diagnostics.DebugLog.Info($"Artisan -> created crafting list '{listName}' " +
                                      $"(recipe {recipeId} x{amount}, {rows.Count} row(s) including pre-crafts).");
            return true;
        }
        catch (Exception ex)
        {
            // TargetInvocationException wraps whatever Artisan threw; report the inner cause.
            var cause = (ex as TargetInvocationException)?.InnerException ?? ex;
            error = $"Artisan's crafting-list API failed: {cause.Message}";
            Diagnostics.DebugLog.Warn($"Artisan crafting-list creation failed: {cause}");
            return false;
        }
    }

    // Resolve Artisan's list API once. Re-attempts on a throttle while unresolved, so a
    // late-loading Artisan is picked up without scanning every loaded assembly each frame
    // (Available is read from the window draw).
    private bool Resolve(out string error)
    {
        error = string.Empty;
        if (_save != null)
            return true;
        if (Environment.TickCount64 < _nextResolveTicks)
        {
            error = "Artisan is not installed, or its crafting-list API did not resolve.";
            return false;
        }
        _nextResolveTicks = Environment.TickCount64 + ResolveRetryMs;

        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == ArtisanAssembly);
        if (asm == null)
        {
            error = "Artisan is not installed (or not loaded).";
            return false;
        }

        try
        {
            _listType = asm.GetType("Artisan.CraftingLists.NewCraftingList");
            _listItemType = asm.GetType("Artisan.CraftingLists.ListItem");
            var functions = asm.GetType("Artisan.CraftingLists.CraftingListFunctions");
            var ui = asm.GetType("Artisan.CraftingLists.CraftingListUI");
            if (_listType == null || _listItemType == null || functions == null || ui == null)
            {
                error = "Artisan's crafting-list types were not found (Artisan internals changed?).";
                return Fail();
            }

            const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
            _setId = functions.GetMethod("SetID", PublicStatic, null, new[] { _listType }, null);
            _save = functions.GetMethod("Save", PublicStatic, null, new[] { _listType, typeof(bool) }, null);
            _addAllSubcrafts = ui.GetMethod("AddAllSubcrafts", PublicStatic, null,
                new[] { typeof(Recipe), _listType, typeof(int), typeof(int) }, null);
            _listName = _listType.GetProperty("Name");
            _listRecipes = _listType.GetProperty("Recipes");
            _itemId = _listItemType.GetProperty("ID");
            _itemQuantity = _listItemType.GetProperty("Quantity");

            if (_setId == null || _save == null || _addAllSubcrafts == null
                || _listName == null || _listRecipes == null || _itemId == null || _itemQuantity == null)
            {
                error = "Artisan's crafting-list API did not match (Artisan internals changed?).";
                return Fail();
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Reading Artisan's crafting-list API failed: {ex.Message}";
            return Fail();
        }
    }

    private bool Fail()
    {
        _save = null;
        return false;
    }
}
