using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Relicable.Windows;

// Shared UI primitives so every window formats hover text the same way.
internal static class Ui
{
    // Wrapped hover tooltip for the last item. ImGui.SetTooltip never wraps, so a long
    // string renders as one screen-wide line; this caps the tooltip at a readable width.
    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // "(?)" help marker with a wrapped tooltip, for explanations too long to hang off
    // the control itself.
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        Tooltip(text);
    }

    // Wrapped colored body text: ImGui.TextColored never wraps, so long inline notes
    // render as one screen-wide line without this.
    public static void Wrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    // Wrapped disabled-grey note text, for inline explanations under a control.
    public static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }
}
