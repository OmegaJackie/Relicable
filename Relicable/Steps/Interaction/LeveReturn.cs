using System;
using System.Collections.Generic;
using ECommons;                    // GetText / ContainsAny (GenericHelpers, namespace ECommons)
using ECommons.EzSharedDataManager; // EzSharedData.TryGet ("YesAlready.StopRequests")
using ECommons.ExcelServices.Sheets; // QuestDialogueText (leve/LeveDirector)
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Diagnostics;
using static ECommons.GenericHelpers; // TryGetAddonMaster

namespace Relicable.Steps.Interaction;

// Accepts the "Return to the levemete at <place>?" prompt the LEVE DIRECTOR raises when a battle
// leve completes -- ported from how Battlevest handles it (Core.OnUpdate -> Utils.HandleYesno).
//
// The problem it fixes: that prompt is a SelectYesno that pops a beat AFTER the objective clears --
// frequently after BoundByDuty has already dropped. Accepting it teleports the character back to the
// levemete, which is REQUIRED here: the RelicNote book slot credits on the "Collect Reward." TURN-IN
// at the levemete, NOT on clearing the objective in the field (StartLeveExecutor drives that collection
// after the run), so getting back to the levemete is the whole point. Relicable used to accept the
// prompt only from inside a per-leve/per-step handler (LeveRunner.Tick's ConfirmYes), alive ONLY while
// that runner/step runs. So when the prompt appears in the gap between the run finishing and the next
// step -- or after the LAST leve of a run, when the controller has already Stopped -- nothing is left
// to accept it and the character is stranded at the leve site. That is the reported "not accepting the
// teleport back."
// Four prior builds widened the runner's completion grace chasing this; the real defect is that the
// acceptor's lifetime was coupled to the run state.
//
// This mirrors Battlevest's decoupling: Plugin.OnUpdate calls Tick() EVERY frame (independent of the
// controller's state, so it survives a Stop right after the final leve), and the handler self-gates on
// a leve-activity WINDOW that StartLeveExecutor keeps warm (NoteLeveActivity, extended by a grace so a
// late prompt is still covered). Inside the window it (a) TEXT-MATCHES the leve-director message so it
// never touches an unrelated Yes/No (the "Commence?" confirm stays with LeveRunner; a treasure coffer
// stays with its executor), and (b) registers our InternalName in YesAlready's "stop requests" set so
// YesAlready cannot race and click No. Scoping to the window (not the whole multi-hour run) is the
// difference from Battlevest, which only ever runs leves: it keeps YesAlready live for Relicable's
// non-leve segments (dungeon/turn-in/FATE confirms). Relicable runs one leve at a time, so it always
// says Yes (Battlevest says No while more of its selected leves remain).
internal static class LeveReturn
{
    // The shared set YesAlready reads: any plugin whose InternalName is present is told to leave every
    // prompt alone. Created and owned by YesAlready; we only add / remove our own name.
    private const string YesAlreadyStopKey = "YesAlready.StopRequests";

    // How long past the last leve-activity tick to keep accepting the return prompt. Covers the delay
    // between the leve slot crediting (which completes the StartLeve step and can Stop the controller a
    // frame or two later) and the leve director raising the SelectYesno. Generous vs the observed
    // ~sub-second delay, and bounded so YesAlready is only ever suppressed briefly around a leve.
    private const long GraceMs = 20_000;

    // Tick64 up to which the handler is active. Stamped by StartLeveExecutor each tick a leve step is
    // running (NoteLeveActivity), including its completion tick, so the window outlives the step.
    private static long _activeUntil;
    private static bool _suppressed;

    // leve/LeveDirector rows 0 & 1 are the return-prompt director lines Battlevest matches for its
    // isLeveFinish check. Row 0 ("Return to the levemete at <place>?") is a TEMPLATE with a settlement-
    // name macro mid-string, so we read only the FIRST text payload (GetText(true)) -> the stable prefix
    // "Return to the levemete at ", which substring-matches the rendered prompt. (ExtractText would
    // fuse the trailing "?" onto the prefix, yielding a needle that can never appear in the live text.)
    // Read once and cached (static game data); a read failure or empty row degrades to "no match"
    // rather than throwing into the framework tick.
    private static string[]? _returnPromptText;

    private static string[] ReturnPromptText()
    {
        if (_returnPromptText != null)
            return _returnPromptText;
        var list = new List<string>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<QuestDialogueText>(name: "leve/LeveDirector");
            foreach (var row in new uint[] { 0, 1 })
            {
                var text = sheet?.GetRowOrDefault(row)?.Value.GetText(true);
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(text.Trim());
            }
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"LeveReturn: could not read leve/LeveDirector text: {ex.Message}");
        }
        _returnPromptText = list.ToArray();
        return _returnPromptText;
    }

    // Called by StartLeveExecutor every tick a leve step is running (including its completion tick) to
    // keep the acceptance window open GraceMs past the last leve activity.
    public static void NoteLeveActivity()
        => _activeUntil = Environment.TickCount64 + GraceMs;

    // Called every framework frame (Plugin.OnUpdate), independent of the controller's run state so it
    // still fires if the run Stops right after the final leve credits. Inside the leve-activity window:
    // keep YesAlready off our prompt and accept the return prompt if it is up. Outside it: drop our
    // YesAlready stop-request (once).
    public static void Tick()
    {
        if (Environment.TickCount64 <= _activeUntil)
        {
            Suppress();
            Accept();
        }
        else if (_suppressed)
        {
            Release();
        }
    }

    // Accept the leve-completion return prompt if it is showing. Text-matched to the leve-director
    // message, so it never touches the "Commence levequest?" confirm (owned by LeveRunner) or any
    // unrelated Yes/No that other objectives raise (e.g. a treasure coffer). Returns true if it fired.
    public static bool Accept()
    {
        if (!TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var m) || !m.IsAddonReady)
            return false;

        var prompts = ReturnPromptText();
        if (prompts.Length == 0 || !m.Text.ContainsAny(StringComparison.OrdinalIgnoreCase, prompts))
            return false;

        m.Yes();
        DebugLog.Verbose("LeveReturn: accepted the return-to-aetheryte prompt");
        return true;
    }

    // Register with YesAlready so it stands down on prompts while we are in the leve window (mirrors
    // Battlevest adding its InternalName to the stop set). Idempotent; a no-op when YesAlready is not
    // installed, since the shared set then does not exist. Paired with Release.
    public static void Suppress()
    {
        if (EzSharedData.TryGet<HashSet<string>>(YesAlreadyStopKey, out var stop))
        {
            stop.Add(Plugin.PluginInterface.InternalName);
            _suppressed = true;
        }
    }

    // Undo Suppress: stop asking YesAlready to stand down, so it resumes covering non-leve prompts.
    // Called when the window expires, and from Plugin.Dispose, so our entry never lingers in the set.
    public static void Release()
    {
        if (EzSharedData.TryGet<HashSet<string>>(YesAlreadyStopKey, out var stop))
            stop.Remove(Plugin.PluginInterface.InternalName);
        _suppressed = false;
    }
}
