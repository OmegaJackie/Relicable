using System;

namespace Relicable.Licensing;

// Runtime state for the Early Alpha access gate.
//
// Holds the validated license, persists the redeemed code, and re-checks expiry as
// the session runs so a code cannot outlive its window just because the game was
// left open across midnight.
//
// The gate is enforced at a SINGLE choke point -- Plugin.OnUpdate returns before
// ticking the controller or any of the independent runners when Unlocked is false --
// rather than being sprinkled across the executors. One place to read, one place to
// audit, and no path where a window's button quietly bypasses it.
public sealed class AlphaGate
{
    private readonly Configuration _config;
    private readonly Action _save;

    // Expiry is date-based, so re-checking more than once a minute is pointless.
    private DateTime _nextRecheckUtc = DateTime.MinValue;

    public AlphaGate(Configuration config, Action save)
    {
        _config = config;
        _save = save;
        Revalidate();
    }

    // True when a valid, unexpired, unrevoked code is stored. Everything the plugin
    // automates is gated on this.
    public bool Unlocked { get; private set; }

    // The validated code's contents. Also populated when a stored code is authentic
    // but EXPIRED, so the window can say "your code expired on <date>" and name the
    // owner rather than showing a bare failure.
    public AlphaLicense License { get; private set; }

    // Why the gate is closed, in words meant for the user. Empty when unlocked.
    public string Status { get; private set; } = string.Empty;

    // True when a stored code was rejected (as opposed to no code ever being entered).
    // Lets the window distinguish "welcome, enter your code" from "your code stopped
    // working", which are very different messages to receive.
    public bool HasStoredCode => !string.IsNullOrWhiteSpace(_config.AlphaAccessCode);

    // Warn in the last stretch of the window so a tester can ask for a renewal before
    // a run stops mid-grind rather than after.
    public bool ExpiringSoon => Unlocked && License.DaysRemaining(DateTime.UtcNow) <= 7;

    // Tries a pasted code and, on success, persists it. Returns false with the reason
    // in Status so the window can render it inline.
    public bool TryRedeem(string? code)
    {
        if (!AlphaCode.TryValidate(code, out var license, out var error))
        {
            // Keep any previously working license intact: a mistyped attempt should not
            // lock out a tester who is already running.
            Status = error;
            return false;
        }

        _config.AlphaAccessCode = (code ?? string.Empty).Trim();
        _save();

        License = license;
        Unlocked = true;
        Status = string.Empty;
        _nextRecheckUtc = DateTime.UtcNow.AddMinutes(1);
        return true;
    }

    // Re-validates the stored code from scratch. Called on load, and periodically from
    // the framework tick.
    public void Revalidate()
    {
        _nextRecheckUtc = DateTime.UtcNow.AddMinutes(1);

        if (!HasStoredCode)
        {
            Unlocked = false;
            License = default;
            Status = string.Empty; // no code yet is not an error; the window welcomes instead
            return;
        }

        if (AlphaCode.TryValidate(_config.AlphaAccessCode, out var license, out var error))
        {
            License = license;
            Unlocked = true;
            Status = string.Empty;
            return;
        }

        // Authentic but expired codes keep their parsed contents so the message can be
        // specific; a forged or damaged one clears out.
        License = license;
        Unlocked = false;
        Status = error;
    }

    // Cheap per-frame hook. Only does real work once a minute.
    public void Tick()
    {
        if (DateTime.UtcNow >= _nextRecheckUtc)
            Revalidate();
    }

    // Forgets the stored code (the window's "Remove code" action), so a tester can hand
    // a machine back or swap to a renewed code cleanly.
    public void Clear()
    {
        _config.AlphaAccessCode = string.Empty;
        _save();
        Unlocked = false;
        License = default;
        Status = string.Empty;
    }
}
