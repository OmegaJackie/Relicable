using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Relicable.External;

// Temporarily swaps Rotation Solver Reborn's ACTIVE job rotation while we are inside a
// specific duty, and puts the user's own choice back on the way out.
//
// MECHANISM (verified against RSR 7.5.1.17 source, commit 5b7e5510):
//   RSR exposes NO IPC that selects a rotation. Its OtherCommandType.Rotations gate sets a
//   config value ON the already-active rotation (RSCommands_OtherCommand.ExecuteRotationCommand
//   -> DataCenter.CurrentRotation.Configs), and OtherCommandType.Settings can write the
//   RotationChoice property but nothing else: RotationUpdater.UpdateCustomRotation() early-
//   returns while the job and combat type are unchanged, and only reads the config AFTER that
//   guard, so a config write alone never applies in-session. RSR's own picker
//   (UI/RotationConfigWindow.cs) therefore does three things, and so do we:
//       Service.Config.RotationChoice = <Type.FullName>;   // a per-JOB dictionary entry
//       Service.Config.Save();                             // deliberately NOT done here
//       RotationUpdater.ChangeRotation(rotation);          // the live apply
//   Every RSR type involved (Service, Configs, RotationUpdater) is `internal`, so all of this
//   is late-bound; any lookup that does not resolve makes the whole thing a logged no-op and
//   leaves the user's configuration untouched.
//
// We never call RSR's Save() on APPLY: the override must not reach RSR's own config file. RSR does
// save unconditionally in its dispose, though, so a clean game exit while the override is live
// would bake it in -- hence the breadcrumb in Relicable's Configuration (RsrRotationOverrideActive /
// RsrRotationOverrideJobId / RsrRotationOverridePrevious), written the moment the override is
// applied so the next load can still put the user's choice back after a crash or a kill.
//
// Restore() DOES call RSR's Save(), and must. Dalamud does not guarantee plugin dispose order: if
// RotationSolver disposes before Relicable, RSR has already written the override to disk, and the
// value Restore puts back into the in-memory dictionary would never be persisted by anybody. The
// user's real choice would be gone with no record, because Restore had also cleared the breadcrumb
// -- and Apply's "already selected" branch deliberately claims no ownership, so it could never be
// recovered afterwards. Saving on restore is strictly protective: it only ever writes back the
// value the user themselves had selected. The breadcrumb is therefore cleared ONLY once both the
// dictionary write and the save have actually succeeded; on any failure it is kept so a later tick
// (or a later session) retries.
//
// Writes go straight into the private `_rotationChoiceDict` keyed by the job we captured, never
// through the public RotationChoice property: that property resolves its key from DataCenter.Job
// at call time, so restoring after a job change would write the old value into the NEW job's
// slot and corrupt it.
public sealed class RsrRotationOverride
{
    private const string BasicAssemblyName = "RotationSolver.Basic";
    private const string PluginAssemblyName = "RotationSolver";
    private const string ServiceTypeName = "RotationSolver.Basic.Service";
    private const string UpdaterTypeName = "RotationSolver.Updaters.RotationUpdater";
    private const string ChoiceDictFieldName = "_rotationChoiceDict";

    private readonly Configuration _config;
    private readonly Action _save;

    // Warn once per distinct reason. The caller retries on a timer while the player stands in
    // the duty, and "RSR has no such rotation" would otherwise repeat forever.
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    // Last Apply failure, surfaced in the config window. Null while things are working.
    private string? _lastFailure;

    public RsrRotationOverride(Configuration config, Action save)
    {
        _config = config;
        _save = save;
    }

    // True while WE own RSR's rotation choice for ActiveJobId and still owe the user a restore.
    // Backed by the persisted breadcrumb, so it survives a plugin reload or a crash.
    public bool Active => _config.RsrRotationOverrideActive;

    public uint ActiveJobId => _config.RsrRotationOverrideJobId;

    // True when RSR is loaded and every internal we need still resolves. Diagnostics only; the
    // verbs below are safe to call regardless and no-op when it is false.
    public bool Available => Resolve() != null;

    // Point RSR at `rotationFullName` (a Type.FullName) for `classJobId`, remembering whatever
    // was selected before. Idempotent: re-applying for the job we already own re-asserts the
    // choice without touching the remembered value. Returns false (and mutates nothing) when
    // RSR is absent or does not have that rotation.
    public bool Apply(uint classJobId, string rotationFullName)
    {
        var rsr = Resolve();
        if (rsr == null)
            return false;

        var target = FindRotation(rsr, classJobId, rotationFullName, out _);
        if (target == null)
        {
            return Warn($"missing:{rotationFullName}",
                "The Ifrit EX burst rotations are not in your Rotation Solver build, so nothing was changed. " +
                "Rotation Solver loads rotations only from its own assembly -- it does not scan a folder -- so " +
                "RelicBurstRotations has to be compiled into a custom RotationSolver build, and a Rotation " +
                "Solver update replaces that with the official one.",
                "The Ifrit EX burst rotations are not present in this Rotation Solver build, so nothing was " +
                "changed. They require a custom Rotation Solver build.");
        }

        // A live override for a DIFFERENT job has to be handed back first: the remembered value
        // belongs to that job's dictionary slot, and writing it anywhere else corrupts it.
        if (Active && ActiveJobId != classJobId)
        {
            Restore(classJobId);
            if (Active)
            {
                // The hand-back did not complete (RSR went away between the two calls). Taking
                // this job over now would overwrite the breadcrumb and lose the other job's
                // choice, so do nothing and let the caller retry.
                return Warn("handback", "Could not hand the previous job's RSR rotation back, so the Ifrit EX burst swap was skipped this time.");
            }
        }

        var current = ReadChoice(rsr, classJobId);

        if (!Active)
        {
            if (string.Equals(current, rotationFullName, StringComparison.Ordinal))
            {
                // Already the user's OWN selection. Apply it live but claim no ownership, so
                // leaving the duty cannot rewrite their configuration.
                ChangeRotation(rsr, target);
                Diagnostics.DebugLog.Info(
                    $"RSR rotation '{rotationFullName}' is already selected for ClassJob {classJobId}; " +
                    "applied live, nothing to restore later.");
                _warned.Clear();
                return true;
            }

            _config.RsrRotationOverridePrevious = current;
            _config.RsrRotationOverrideJobId = classJobId;
            _config.RsrRotationOverrideActive = true;
            _config.RsrRotationOverrideRsrVersion = rsr.RsrVersion;
            _save();
        }

        if (!WriteChoice(rsr, classJobId, rotationFullName))
        {
            // Nothing reached RSR, so we own nothing. Roll the breadcrumb back rather than leaving
            // a restore owed for a change that never happened.
            _config.RsrRotationOverrideActive = false;
            _config.RsrRotationOverrideJobId = 0;
            _config.RsrRotationOverridePrevious = string.Empty;
            _config.RsrRotationOverrideRsrVersion = string.Empty;
            _save();
            return false;
        }

        ChangeRotation(rsr, target);
        _lastFailure = null;
        _warned.Clear();
        Diagnostics.DebugLog.Info(
            $"RSR rotation -> {rotationFullName} for ClassJob {classJobId} " +
            $"(was '{_config.RsrRotationOverridePrevious}').");
        return true;
    }

    // ---- state the UI needs ----

    /// <summary>
    /// The reason the last <see cref="Apply"/> failed, or null when the last attempt succeeded (or
    /// none has been made). Rendered next to the feature's checkbox so a silently non-working setup
    /// is visible instead of being buried in one debug-log line per session.
    /// </summary>
    public string? LastFailure => _lastFailure;

    /// <summary>
    /// The version of the loaded RotationSolver plugin assembly, or null when RSR is not
    /// resolvable. Recorded alongside the breadcrumb so an RSR update -- which reinstalls the
    /// official build and therefore removes any custom rotations -- can be reported rather than
    /// silently ending the feature.
    /// </summary>
    public string? RsrAssemblyVersion => Resolve()?.RsrVersion;

    /// <summary>
    /// RSR's rotation choice for <paramref name="classJobId"/> right now, or null when RSR is not
    /// resolvable. Lets the caller notice that the user picked a different rotation themselves
    /// instead of blindly trusting a cached "we applied it" flag.
    /// </summary>
    public string? ReadCurrentChoice(uint classJobId)
    {
        var rsr = Resolve();
        return rsr == null ? null : ReadChoice(rsr, classJobId);
    }

    /// <summary>
    /// Drop ownership WITHOUT writing anything back to RSR. For the one case where the remembered
    /// value must not be re-applied: the user deliberately changed the rotation themselves while we
    /// held the override, so their new choice is now the correct one.
    /// </summary>
    public void AbandonOwnership(string reason)
    {
        if (!Active)
            return;

        Diagnostics.DebugLog.Info($"Relicable dropped its RSR rotation override without restoring: {reason}");
        _config.RsrRotationOverrideActive = false;
        _config.RsrRotationOverrideJobId = 0;
        _config.RsrRotationOverridePrevious = string.Empty;
        _config.RsrRotationOverrideRsrVersion = string.Empty;
        _save();
    }

    // Put the remembered choice back and drop ownership. `liveClassJobId` is the job the player
    // is on RIGHT NOW (0 when unknown / not logged in): the live re-apply only happens when it
    // matches the job we saved, because ChangeRotation would otherwise install a rotation for
    // the wrong job. The config write always happens, and RSR re-picks from it by itself on the
    // next job change.
    public void Restore(uint liveClassJobId = 0)
    {
        if (!Active)
            return;

        var job = _config.RsrRotationOverrideJobId;
        var previous = string.IsNullOrEmpty(_config.RsrRotationOverridePrevious)
            ? string.Empty
            : _config.RsrRotationOverridePrevious;

        var rsr = Resolve();
        if (rsr == null)
        {
            // KEEP the breadcrumb. RSR may simply not be loaded at this instant; clearing it
            // here would lose the user's choice permanently. The next load retries.
            Warn("restore",
                "RSR is not resolvable right now, so the burst-rotation override could not be undone yet. " +
                "Relicable keeps the remembered choice and restores it the next time RSR is available.");
            return;
        }

        if (!WriteChoice(rsr, job, previous))
        {
            // The dictionary write itself failed. KEEP the breadcrumb: dropping it here would
            // delete the only record of what the user had selected.
            Warn("restorewrite",
                "Writing your RSR rotation choice back failed, so Relicable is keeping the remembered value and " +
                "will retry. Your RSR rotation may be wrong until then.");
            return;
        }

        if (liveClassJobId == job)
        {
            // Empty means "RSR's own default", which RSR resolves as the first discovered
            // rotation for the job -- mirror that fallback here.
            var target = FindRotation(rsr, job, previous, out var first) ?? first;
            if (target == null)
            {
                // RotationUpdater.GetRotations returns [] until RSR has finished loading (it
                // requires Player.Object != null and CustomRotations.Length >= 22), so this is the
                // window where RSR resolves but has no rotations yet. The config write above landed,
                // but DataCenter.CurrentRotation is still pointing at the burst rotation and
                // UpdateCustomRotation early-returns while the job is unchanged, so it would stay
                // there indefinitely. KEEP the breadcrumb so the next tick actually re-applies.
                Warn("restorelive",
                    "RSR has not finished loading its rotations, so the burst rotation could not be swapped back " +
                    "live yet. Relicable keeps the remembered choice and retries.");
                return;
            }

            ChangeRotation(rsr, target);
        }

        // Force the restored value to RSR's own config file BEFORE dropping the breadcrumb -- see
        // the header note on dispose ordering. A failed save keeps the breadcrumb, exactly like a
        // failed write.
        if (!SaveRsrConfig(rsr))
        {
            Warn("restoresave",
                "Your RSR rotation choice was put back in memory but RSR's config file could not be saved, so " +
                "Relicable is keeping the remembered value and will retry rather than risk losing it.");
            return;
        }

        _config.RsrRotationOverrideActive = false;
        _config.RsrRotationOverrideJobId = 0;
        _config.RsrRotationOverridePrevious = string.Empty;
        _save();
        _warned.Clear();
        Diagnostics.DebugLog.Info(
            $"RSR rotation restored -> '{previous}' for ClassJob {job}"
            + (liveClassJobId == job ? " (applied live)." : " (config only; not on that job)."));
    }

    // ---- reflection plumbing ----

    // Everything RSR-side needed for one operation. Resolved per call rather than cached: RSR
    // can be reloaded mid-session (which replaces the Configs instance), and these lookups
    // happen a handful of times per duty, not per frame.
    private sealed record Bindings(
        IDictionary Choices,
        Type JobType,
        MethodInfo GetRotations,
        MethodInfo ChangeRotationMethod,
        object PveCombatType,
        object Config,
        MethodInfo? SaveMethod,
        string RsrVersion);

    private Bindings? Resolve()
    {
        try
        {
            var basicAsm = FindAssembly(BasicAssemblyName);
            var pluginAsm = FindAssembly(PluginAssemblyName);
            if (basicAsm == null || pluginAsm == null)
            {
                Warn("asm", "Rotation Solver Reborn is not loaded, so the Ifrit EX burst rotation swap is skipped.");
                return null;
            }

            var config = basicAsm.GetType(ServiceTypeName)
                ?.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (config == null)
            {
                Warn("cfg", "RSR's Service.Config did not resolve; the burst rotation swap is skipped (RSR internals changed?).",
                    "This Rotation Solver version is not compatible with the burst rotation swap, so it is skipped.");
                return null;
            }

            var dictField = config.GetType().GetField(ChoiceDictFieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (dictField?.GetValue(config) is not IDictionary choices)
            {
                Warn("dict", $"RSR's {ChoiceDictFieldName} did not resolve; the burst rotation swap is skipped (RSR internals changed?).",
                    "This Rotation Solver version is not compatible with the burst rotation swap, so it is skipped.");
                return null;
            }

            var jobType = dictField.FieldType.GetGenericArguments().FirstOrDefault();
            var updater = pluginAsm.GetType(UpdaterTypeName);
            var getRotations = updater?.GetMethod("GetRotations", BindingFlags.Public | BindingFlags.Static);
            var changeRotation = updater?.GetMethod("ChangeRotation", BindingFlags.Public | BindingFlags.Static);
            if (jobType is not { IsEnum: true } || getRotations == null || changeRotation == null
                || getRotations.GetParameters().Length != 2)
            {
                Warn("updater", "RSR's RotationUpdater surface did not resolve; the burst rotation swap is skipped (RSR internals changed?).",
                    "This Rotation Solver version is not compatible with the burst rotation swap, so it is skipped.");
                return null;
            }

            // Take CombatType.PvE from the method's OWN parameter type rather than a hardcoded
            // value, so a renumbered enum cannot silently select the PvP list.
            var pve = Enum.Parse(getRotations.GetParameters()[1].ParameterType, "PvE");

            // Configs.Save() is a public instance method (verified by reflection against
            // RotationSolver.Basic 7.5.1.17). Missing it is not fatal for Apply -- only Restore
            // needs it, and Restore keeps its breadcrumb when the save cannot happen.
            var save = config.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);

            var version = pluginAsm.GetName().Version?.ToString() ?? string.Empty;

            return new Bindings(choices, jobType, getRotations, changeRotation, pve, config, save, version);
        }
        catch (Exception ex)
        {
            Warn("resolve", $"Reading RSR's internals failed, so the burst rotation swap is skipped: {ex.Message}",
                "Rotation Solver could not be read, so the burst rotation swap is skipped (details in /xllog).");
            return null;
        }
    }

    // The rotation instance whose Type.FullName matches, plus the FIRST rotation RSR offers for
    // the job (its own fallback when a choice does not match). Both are null when RSR has not
    // finished loading its rotations -- GetRotations returns an empty array until then.
    private object? FindRotation(Bindings rsr, uint classJobId, string fullName, out object? first)
    {
        first = null;
        try
        {
            var job = Enum.ToObject(rsr.JobType, (byte)classJobId);
            if (rsr.GetRotations.Invoke(null, new[] { job, rsr.PveCombatType }) is not IEnumerable rotations)
                return null;

            object? match = null;
            foreach (var rotation in rotations)
            {
                first ??= rotation;
                if (string.Equals(rotation?.GetType().FullName, fullName, StringComparison.Ordinal))
                {
                    match = rotation;
                    break;
                }
            }
            return match;
        }
        catch (Exception ex)
        {
            Warn("find", $"Enumerating RSR's rotations failed, so the burst rotation swap is skipped: {ex.Message}",
                "Rotation Solver's rotation list could not be read, so the burst rotation swap is skipped (details in /xllog).");
            return null;
        }
    }

    private string ReadChoice(Bindings rsr, uint classJobId)
    {
        try
        {
            var key = Enum.ToObject(rsr.JobType, (byte)classJobId);
            // A missing key means RSR has never resolved a choice for this job, which it treats
            // as "use my default" -- the same meaning as the empty string.
            return rsr.Choices.Contains(key) ? rsr.Choices[key] as string ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Returns whether the write actually landed. Restore() gates the breadcrumb clear on this: a
    // swallowed failure there would delete the only record of the user's own choice.
    private bool WriteChoice(Bindings rsr, uint classJobId, string value)
    {
        try
        {
            rsr.Choices[Enum.ToObject(rsr.JobType, (byte)classJobId)] = value;
            return true;
        }
        catch (Exception ex)
        {
            return Warn("write", $"Writing RSR's rotation choice failed: {ex.Message}",
                "The rotation choice could not be written to Rotation Solver (details in /xllog).");
        }
    }

    // Persist RSR's own config file. Only ever called from Restore -- see the header note.
    private bool SaveRsrConfig(Bindings rsr)
    {
        if (rsr.SaveMethod == null)
            return Warn("save", "RSR's Configs.Save() did not resolve, so the restored rotation choice could not be persisted.",
                "Rotation Solver's settings could not be saved, so the restored rotation choice may not persist.");

        try
        {
            rsr.SaveMethod.Invoke(rsr.Config, null);
            return true;
        }
        catch (Exception ex)
        {
            return Warn("save", $"Saving RSR's config failed: {ex.Message}",
                "Rotation Solver's settings could not be saved (details in /xllog).");
        }
    }

    private void ChangeRotation(Bindings rsr, object rotation)
    {
        try
        {
            rsr.ChangeRotationMethod.Invoke(null, new[] { rotation });
        }
        catch (Exception ex)
        {
            // The config write already landed, so RSR still picks this rotation up on the next
            // job change; only the immediate live apply was lost.
            Warn("change", $"RSR's live rotation apply failed (the choice is still written): {ex.Message}",
                "The rotation was set but could not be applied live; it takes effect on the next job change.");
        }
    }

    private static Assembly? FindAssembly(string name)
        => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);

    // Emit a warning at most once per reason, and return false so callers can `return Warn(...)`.
    // The full message goes to the log; the settings window shows uiMessage when given, so
    // reflection internals and raw exception text never reach the UI.
    private bool Warn(string key, string message, string? uiMessage = null)
    {
        _lastFailure = uiMessage ?? message;
        if (_warned.Add(key))
            Diagnostics.DebugLog.Warn(message);
        return false;
    }
}
