using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using Relicable.Data;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Windows;

// The "A Relic Reborn" Part 2 step, drawn as an annotated, actionable line:
//
//   Paladin: Aeolian Scimitar (Battledance Materia III x2)
//
// The weapon and the materia are click targets -- clicking searches an OPEN market board for
// that exact name (GameState.TrySearchMarketBoard, the NovusWindow pattern) and falls back to
// copying the name when no board is open. Beneath them: travel to the market board nearest the
// Limsa Lominsa aetheryte, and hand the weapon (plus every pre-craft) to Artisan as a crafting
// list.
//
// Shared by the main window (shown while the quest actually sits on this step) and the
// questmap (shown as reference for whichever job is being previewed).
internal static class ClassWeaponPanel
{
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 Grey = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Link = new(0.55f, 0.78f, 1.00f, 1f);

    // Last crafting-list attempt, so a failure is shown in the window instead of only the log.
    private static string _lastError = string.Empty;
    private static RelicJob _lastErrorJob = RelicJob.None;

    // Draw the step for `job`. `heading` is the line above the annotation (the main window uses
    // its "Next step" phrasing, the questmap a plain label). Does nothing for a job with no
    // base-relic data. Pass idSuffix to keep ImGui ids unique when two panels can co-exist.
    public static void Draw(RelicJob job, ArtisanCraftingList artisan, string heading, string idSuffix)
    {
        var step = ClassWeaponSteps.For(job);
        if (step == null)
            return;

        if (!string.IsNullOrEmpty(heading))
            ImGui.TextColored(Yellow, heading);

        DrawAnnotation(step, idSuffix);
        DrawHave(step);
        DrawActions(step, artisan, idSuffix);
    }

    // "<Job>: <Weapon> (<Materia> xN)", with the weapon and materia as market-board search links.
    private static void DrawAnnotation(ClassWeaponStep step, string idSuffix)
    {
        ImGui.TextUnformatted($"{step.JobName}:");
        ImGui.SameLine();
        SearchLink(step.WeaponName, $"cwWeapon{idSuffix}{step.Job}",
            $"{step.WeaponName}\n" +
            (step.CraftJob.Length > 0 ? $"Level 50 {step.CraftJob} recipe, or buy it on the market board.\n" : string.Empty) +
            "Click to search an open market board for it; with no board open the name is copied to your clipboard.");
        ImGui.SameLine(0f, 0f);
        ImGui.TextUnformatted(" (");
        ImGui.SameLine(0f, 0f);
        SearchLink(step.MateriaName, $"cwMateria{idSuffix}{step.Job}",
            $"{step.MateriaName}\n" +
            $"Meld {step.MateriaCount} onto the {step.WeaponName} at a materia melder (needs 'Waking the Spirit').\n" +
            "Click to search an open market board for it; with no board open the name is copied to your clipboard.");
        ImGui.SameLine(0f, 0f);
        ImGui.TextUnformatted($" x{step.MateriaCount})");
    }

    // Live have/need for the two items, so the step says whether anything is still missing.
    private static void DrawHave(ClassWeaponStep step)
    {
        var haveWeapon = step.WeaponItemId == 0 ? 0 : GameState.InventoryCount(step.WeaponItemId);
        var haveMateria = step.MateriaItemId == 0 ? 0 : GameState.InventoryCount(step.MateriaItemId);
        ImGui.TextColored(haveWeapon > 0 ? Green : Grey, $"   weapon {haveWeapon} / 1");
        ImGui.SameLine();
        ImGui.TextColored(haveMateria >= step.MateriaCount ? Green : Grey,
            $"   materia {haveMateria} / {step.MateriaCount}");
        ImGui.SameLine();
        ImGui.TextColored(Grey, "(in your bags)");
        Ui.Tooltip("Counted in your own bags only. Once the melded weapon is handed to Gerolt these " +
                   "drop back to zero -- that is expected, the quest has taken it.");
    }

    private static void DrawActions(ClassWeaponStep step, ArtisanCraftingList artisan, string idSuffix)
    {
        if (ImGui.Button($"Market board (Limsa)##cwMb{idSuffix}"))
            LocationNavigator.GoWorld(ClassWeaponSteps.MarketBoardTerritory, ClassWeaponSteps.MarketBoardWorld);
        Ui.Tooltip($"Flag the market board nearest the {ClassWeaponSteps.MarketBoardLabel} aetheryte, teleport " +
                   "there, and walk to the flag. It is the closest board to the teleport point, so you land " +
                   "beside it; then click the weapon or materia above to search for it.");

        ImGui.SameLine();
        DrawCraftingListButton(step, artisan, idSuffix);

        if (_lastErrorJob == step.Job && _lastError.Length > 0)
            Ui.Wrapped(Grey, _lastError);
    }

    private static void DrawCraftingListButton(ClassWeaponStep step, ArtisanCraftingList artisan, string idSuffix)
    {
        if (step.RecipeId == 0)
        {
            ImGui.TextDisabled("no recipe");
            Ui.Tooltip($"{step.WeaponName} has no crafting recipe in the game data, so it can only be bought.");
            return;
        }
        if (!artisan.Available)
        {
            ImGui.TextDisabled("Artisan: not available");
            Ui.Tooltip("Install and enable Artisan to build a crafting list for this weapon and its pre-crafts.");
            return;
        }

        if (ImGui.Button($"Add to crafting list##cwList{idSuffix}"))
        {
            var listName = $"Relicable - {step.WeaponName}";
            if (artisan.TryCreate(listName, step.RecipeId, 1, out var error))
            {
                _lastError = string.Empty;
                _lastErrorJob = RelicJob.None;
                AnnounceListCreated(step);
            }
            else
            {
                _lastError = $"Crafting list not created: {error}";
                _lastErrorJob = step.Job;
            }
        }
        Ui.Tooltip($"Create an Artisan crafting list named \"Relicable - {step.WeaponName}\" containing the " +
                   $"{step.WeaponName}{(step.CraftJob.Length > 0 ? $" ({step.CraftJob})" : string.Empty)} and every " +
                   "pre-craft it needs. Open Artisan's Crafting Lists to run it.");
    }

    // "[Artisan] <weapon> Crafting List created." in the game chat log, with the weapon as a real
    // clickable item link (the same payload the game uses, so hovering/linking works normally).
    private static void AnnounceListCreated(ClassWeaponStep step)
    {
        try
        {
            var builder = new SeStringBuilder().AddText("[Artisan] ");
            if (step.WeaponItemId != 0)
                builder.AddItemLink(step.WeaponItemId, false, step.WeaponName);
            else
                builder.AddText(step.WeaponName);
            Svc.Chat.Print(builder.AddText(" Crafting List created.").Build());
        }
        catch (Exception ex)
        {
            // The list was still created; only the chat notice failed.
            Diagnostics.DebugLog.Warn($"Artisan crafting-list chat notice failed: {ex.Message}");
        }
    }

    // A clickable, coloured item name: click searches an open market board for it, otherwise the
    // name goes to the clipboard. Sized to the text so it composes inline with SameLine.
    private static void SearchLink(string name, string id, string tooltip)
    {
        if (string.IsNullOrEmpty(name))
        {
            ImGui.TextDisabled("(unresolved)");
            return;
        }
        var size = ImGui.CalcTextSize(name);
        ImGui.PushStyleColor(ImGuiCol.Text, Link);
        var clicked = ImGui.Selectable($"{name}##{id}", false, ImGuiSelectableFlags.None, size);
        ImGui.PopStyleColor();
        Ui.Tooltip(tooltip);
        if (!clicked)
            return;
        if (!GameState.TrySearchMarketBoard(name))
            ImGui.SetClipboardText(name);
    }
}
