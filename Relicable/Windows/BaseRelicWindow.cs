using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Relicable.BaseRelic;
using Relicable.Model;

namespace Relicable.Windows;

// A Questionable-style "questmap" for the base ARR relic (A Relic Reborn): the whole Zodiac
// line laid out for a first-timer -- the one-time prerequisite quests directly attached to
// the line, the per-job relic quest and its live state, the ten ordered parts with
// live-verified checkmarks, and the materials to stock -- all read live from game state each
// frame (this draws only while open). The MSQ finale is shown as informational context, not
// a checked gate (per scope). Everything here is the PrerequisiteChecker report rendered
// visually, so the panel and the /relic prereq log agree.
public sealed class BaseRelicWindow : Window
{
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Grey = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 Amber = new(0.95f, 0.80f, 0.35f, 1f);

    private readonly PrerequisiteChecker _checker;
    private readonly Configuration _config;
    private readonly Relicable.External.ArtisanCraftingList _artisanLists;

    // The job the questmap is showing. None = follow the active/overridden job automatically.
    private RelicJob _viewJob = RelicJob.None;

    public BaseRelicWindow(PrerequisiteChecker checker, Configuration config,
        Relicable.External.ArtisanCraftingList artisanLists)
        : base("Relicable - A Relic Reborn questmap")
    {
        Size = new Vector2(580, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        _checker = checker;
        _config = config;
        _artisanLists = artisanLists;
    }

    public override void Draw()
    {
        DrawJobSelector();

        var report = _viewJob == RelicJob.None ? _checker.Build() : _checker.BuildFor(_viewJob);

        DrawHeader(report);
        ImGui.Separator();
        DrawPrerequisites(report);
        ImGui.Separator();
        DrawRelicQuest(report);
        DrawParts(report);
        ImGui.Separator();
        DrawMaterials(report);
    }

    // Preview any job's line, or follow the active/overridden job automatically. This is a
    // view-only selection (it does not change the config job override or the controller).
    private void DrawJobSelector()
    {
        var current = _viewJob == RelicJob.None ? "Active job (auto)" : RelicJobs.DisplayName(_viewJob);
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("View job", current))
        {
            if (ImGui.Selectable("Active job (auto)", _viewJob == RelicJob.None))
                _viewJob = RelicJob.None;
            foreach (var job in RelicJobs.All)
                if (ImGui.Selectable(RelicJobs.DisplayName(job), _viewJob == job))
                    _viewJob = job;
            ImGui.EndCombo();
        }
        Ui.Tooltip("Preview any job's Zodiac line, or follow your active job automatically.");
    }

    private static void DrawHeader(PrerequisiteReport r)
    {
        var jobLabel = r.Job == RelicJob.None ? "No relic job selected" : RelicJobs.DisplayName(r.Job);
        ImGui.TextUnformatted($"Job: {jobLabel}");
        ImGui.SameLine();
        if (r.Job == RelicJob.None)
            ImGui.TextColored(Grey, "(pick a job above, or switch to a relic job)");
        else if (r.PrerequisitesMet)
            ImGui.TextColored(Green, "[ready to begin]");
        else
            ImGui.TextColored(Amber, "[prerequisites not yet met]");
    }

    private static void DrawPrerequisites(PrerequisiteReport r)
    {
        ImGui.TextDisabled("Prerequisites (one-time quests attached to the Zodiac line)");
        StateLine(r.JobLevelRequirement.State, r.JobLevelRequirement.Label, r.JobLevelRequirement.Detail);
        foreach (var g in r.GlobalPrerequisites)
            StateLine(g.State, g.Label, g.Detail);
    }

    private static void DrawRelicQuest(PrerequisiteReport r)
    {
        ImGui.TextDisabled("Relic quest");
        var status = r.LiveQuestSequence > 0
            ? "in progress"
            : r.RelicQuestEverCompleted ? "completed" : "not started";
        var name = string.IsNullOrEmpty(r.RelicQuestName) ? "(select a job)" : r.RelicQuestName;

        var color = r.RelicQuestEverCompleted ? Green : r.LiveQuestSequence > 0 ? Amber : Grey;
        ImGui.TextColored(color, $"{name}  ({status})");
        if (r.RelicQuestId != 0)
            ImGui.TextColored(Grey, "   Accept from Gerolt (Hyrstmill, North Shroud).");
    }

    private void DrawParts(PrerequisiteReport r)
    {
        if (r.Parts.Count == 0)
            return;
        ImGui.TextDisabled("Quest parts");
        foreach (var p in r.Parts)
        {
            // Part 2 carries the job-specific annotation "<Job>: <Weapon> (<Materia> x2)" on the
            // part line itself, and the interactive step (market-board search, travel, Artisan
            // crafting list) directly beneath it -- it is the only part that is not automated.
            if (p.Part == 2 && Data.ClassWeaponSteps.For(r.Job) is { } cw)
            {
                StateLine(p.State, $"{p.Part}. {p.Name} -- {cw.Annotation}", p.Detail);
                ImGui.Indent(20f);
                ClassWeaponPanel.Draw(r.Job, _artisanLists, string.Empty, "questmap");
                ImGui.Unindent(20f);
                continue;
            }
            StateLine(p.State, $"{p.Part}. {p.Name}", p.Detail);
        }
    }

    private static void DrawMaterials(PrerequisiteReport r)
    {
        ImGui.TextDisabled("Materials (have / need)");
        if (r.Materials.Count == 0)
        {
            ImGui.TextColored(Grey, "Select a job to see its material list.");
            return;
        }
        foreach (var m in r.Materials)
        {
            var (glyph, color) = Mark(m.State);
            ImGui.TextColored(color, $"{glyph}  {m.ItemName}: {m.Total} / {m.Needed}  (inv {m.InInventory} / retainers {m.OnRetainers})");
            if (!string.IsNullOrEmpty(m.SourceDetail))
                SubText(m.SourceDetail);
        }
    }

    // A colour-coded requirement line: the label in the state colour, an optional wrapped
    // grey detail beneath it. Mirrors the [x]/[ ]/[?] the /relic prereq log prints.
    private static void StateLine(RequirementState state, string label, string? detail = null)
    {
        var (glyph, color) = Mark(state);
        ImGui.TextColored(color, $"{glyph}  {label}");
        if (!string.IsNullOrEmpty(detail))
            SubText(detail);
    }

    private static void SubText(string text)
    {
        ImGui.Indent(20f);
        ImGui.PushStyleColor(ImGuiCol.Text, Grey);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        ImGui.Unindent(20f);
    }

    private static (string Glyph, Vector4 Color) Mark(RequirementState state) => state switch
    {
        RequirementState.Satisfied => ("[x]", Green),
        RequirementState.Unsatisfied => ("[ ]", Red),
        _ => ("[?]", Amber),
    };
}
