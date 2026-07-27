using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Relicable.Licensing;

namespace Relicable.Windows;

// The Early Alpha access gate.
//
// Shown instead of the main window until a valid code is entered. It is deliberately
// plain: it explains what the alpha is, what the code is for, and that the code is
// tied to a name -- because the name is the whole anti-sharing mechanism and hiding
// it would be dishonest.
public sealed class AlphaGateWindow : Window
{
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.35f, 1f);

    private readonly AlphaGate _gate;
    private readonly Action _openMain;

    // Buffer for the input box. Never pre-filled with a stored code: showing an
    // existing code back in plain text invites it being copied out and passed on.
    private string _entry = string.Empty;
    private bool _justRedeemed;

    public AlphaGateWindow(AlphaGate gate, Action openMain)
        : base("Relicable — Early Alpha")
    {
        _gate = gate;
        _openMain = openMain;

        Size = new Vector2(460, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.AlwaysAutoResize;
    }

    public override void Draw()
    {
        if (_gate.Unlocked)
        {
            DrawUnlocked();
            return;
        }

        DrawLocked();
    }

    private void DrawLocked()
    {
        ImGui.TextWrapped("Relicable is in Early Alpha and needs an access code to run.");
        ImGui.Spacing();

        if (_gate.HasStoredCode && !string.IsNullOrEmpty(_gate.Status))
        {
            // A code that used to work has stopped. Say exactly why, and name the owner
            // when the code is authentic-but-expired so it is obvious which code it is.
            Ui.Wrapped(Red, _gate.Status);
            if (_gate.License.IsPresent)
                Ui.Note($"Stored code was issued to {_gate.License.Owner}.");
            ImGui.Spacing();
        }
        else if (!_gate.HasStoredCode)
        {
            Ui.Note(
                "Codes are issued individually by the developer. Each one carries the name it "
                + "was issued to and an expiry date, and the name it was issued to is displayed "
                + "in this window while it is in use.");
            ImGui.Spacing();
        }

        ImGui.TextUnformatted("Access code");
        ImGui.SetNextItemWidth(-1f);

        // Enter submits, so the common path is paste-and-return.
        var submitted = ImGui.InputText(
            "##alphaCode", ref _entry, 512,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        Ui.Note("Starts with \"RLC1.\". Line breaks and stray spaces are fine.");
        ImGui.Spacing();

        if (ImGui.Button("Unlock") || submitted)
            Redeem();

        if (_gate.HasStoredCode)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove stored code"))
            {
                _gate.Clear();
                _entry = string.Empty;
            }
            Ui.Tooltip("Forgets the saved code on this character.");
        }

        // An attempt that failed while nothing is stored: the message belongs here,
        // under the button, rather than in the banner above.
        if (!_gate.HasStoredCode && !string.IsNullOrEmpty(_gate.Status))
        {
            ImGui.Spacing();
            Ui.Wrapped(Red, _gate.Status);
        }

        ImGui.Separator();
        Ui.Note(
            "Automating movement, combat and FATE participation is against the FINAL FANTASY XIV "
            + "User Agreement and can get your account suspended or terminated. You are choosing "
            + "to take that risk. See the README before you run anything.");
    }

    private void DrawUnlocked()
    {
        Ui.Wrapped(Green, $"Alpha access: {_gate.License.Owner}");

        var days = _gate.License.DaysRemaining(DateTime.UtcNow);
        if (_gate.ExpiringSoon)
            Ui.Wrapped(Yellow, $"Expires {_gate.License.Expires:yyyy-MM-dd} — {days} day{(days == 1 ? "" : "s")} left. Ask the developer for a renewal.");
        else
            Ui.Note($"Expires {_gate.License.Expires:yyyy-MM-dd} ({days} days left).");

        ImGui.Spacing();

        if (_justRedeemed)
        {
            Ui.Note("This code is now saved for this character. You will not be asked again.");
            ImGui.Spacing();
        }

        if (ImGui.Button("Open Relicable"))
        {
            _openMain();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove stored code"))
        {
            _gate.Clear();
            _entry = string.Empty;
            _justRedeemed = false;
        }
        Ui.Tooltip("Forgets the saved code on this character. Relicable will stop until a code is entered again.");
    }

    private void Redeem()
    {
        if (!_gate.TryRedeem(_entry))
            return;

        // Clear the buffer on success so the code is not left sitting in a text box
        // during a stream or a screenshot.
        _entry = string.Empty;
        _justRedeemed = true;
    }
}
