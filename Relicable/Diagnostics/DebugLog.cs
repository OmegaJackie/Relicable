namespace Relicable.Diagnostics;

// Lightweight logging facade. Verbose/Info output is gated by Enabled (driven by
// Configuration.EnableDebugLog) so normal runs stay quiet; warnings and errors
// always pass through. Every line is prefixed so it is easy to filter the
// Dalamud log for "[Relicable]".
//
// Design note: this is the one place that decides whether to emit, so call sites
// stay trivial and the per-frame controller can log freely without cost when the
// toggle is off (the Enabled check short-circuits before string work at the call
// site when callers guard with interpolation-free messages or DebugLog.On).
public static class DebugLog
{
    public static bool Enabled { get; set; }

    // Convenience for guarding expensive interpolated messages:
    //   if (DebugLog.On) DebugLog.Verbose($"...{expensive}...");
    public static bool On => Enabled;

    public static void Verbose(string message)
    {
        if (Enabled)
            Plugin.Log.Debug($"[Relicable] {message}");
    }

    public static void Info(string message)
    {
        if (Enabled)
            Plugin.Log.Information($"[Relicable] {message}");
    }

    // Always emitted: actionable problems the user should see regardless of the
    // debug toggle.
    public static void Warn(string message)
        => Plugin.Log.Warning($"[Relicable] {message}");

    public static void Error(string message)
        => Plugin.Log.Error($"[Relicable] {message}");
}
