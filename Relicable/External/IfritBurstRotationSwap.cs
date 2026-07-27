using System;
using System.Collections.Generic;

namespace Relicable.External;

// Drives the two Rotation-Solver-side tweaks that belong to the Bowl of Embers (Extreme) relic
// farm, both scoped to territory 295 and both self-restoring:
//
//   1. TARGETING (RsrTargetingOverride, on by default, works on the OFFICIAL RSR plugin).
//      Pins RSR's hostile pick to LowMaxHP so Ifrit's Infernal Nails are targeted before Ifrit. A
//      run that reaches the nail phase cannot finish without this -- Ifrit is invulnerable until
//      every nail dies, and RSR's default hostile sort is by hitbox radius descending, i.e. always
//      Ifrit. It is a no-op before nails spawn, since Ifrit is then the only hostile.
//
//   2. ROTATION (RsrRotationOverride, OFF by default). Points RSR at the job's purpose-built
//      "Ifrit EX Burst (<ABBR>)" rotation. This one CANNOT work on the official plugin: RSR
//      7.5.1.17 loads rotations only from its own assembly, so unless the user is running a
//      RotationSolver build with RelicBurstRotations compiled in, the rotations do not exist. That
//      is a legitimate setup, so the feature stays -- but it defaults off and reports its state
//      through LastStatus instead of failing quietly.
//
// Polled from the framework tick rather than hooked to TerritoryChanged on purpose:
//   * RSR's rotations are not loaded yet at zone-in (RotationUpdater.GetRotations returns an
//     empty array until MajorUpdater has run and the local player exists), so a one-shot event
//     handler would fire too early and silently do nothing;
//   * a poll is idempotent by construction -- a duplicate territory event, a re-entry, or a
//     zone within the instance all converge on the same desired state instead of re-running a
//     handler that could clobber the remembered choice.
// A failed attempt backs off (RetryDelayMs) so a genuinely missing rotation does not re-probe
// RSR every frame. The RESTORE path is on the same backoff: a stale breadcrumb plus an uninstalled
// RSR would otherwise run a full AppDomain assembly scan every single frame, forever.
public sealed class IfritBurstRotationSwap
{
    // the Bowl of Embers (Extreme) -- the Nexus light / Zeta mahatma farm duty. (292 is Hard,
    // 1045 Normal; those are NOT this fight.)
    public const uint BowlOfEmbersExtremeTerritory = 295;

    // RSR's TargetingType member that sorts hostiles ascending by MAX HP. An Infernal Nail has a
    // tiny fraction of Ifrit's max HP, so this puts nails first the instant they exist.
    private const string NailTargetingType = "LowMaxHP";

    private const int RetryDelayMs = 5000;

    // ClassJob row id -> Type.FullName of that job's burst rotation (RelicBurstRotations).
    // JOB rows only: a base class (Gladiator, Marauder, ...) has no relic rotation, and only
    // these ten jobs have an ARR Zodiac relic at all, so everything else no-ops. Ids match
    // Model.RelicJobs' verified ClassJob mapping.
    private static readonly IReadOnlyDictionary<uint, string> BurstRotationByClassJobId =
        new Dictionary<uint, string>
        {
            [19] = "RelicBurstRotations.Rotations.PLD_IfritEX", // Paladin
            [21] = "RelicBurstRotations.Rotations.WAR_IfritEX", // Warrior
            [20] = "RelicBurstRotations.Rotations.MNK_IfritEX", // Monk
            [22] = "RelicBurstRotations.Rotations.DRG_IfritEX", // Dragoon
            [30] = "RelicBurstRotations.Rotations.NIN_IfritEX", // Ninja
            [23] = "RelicBurstRotations.Rotations.BRD_IfritEX", // Bard
            [24] = "RelicBurstRotations.Rotations.WHM_IfritEX", // White Mage
            [25] = "RelicBurstRotations.Rotations.BLM_IfritEX", // Black Mage
            [27] = "RelicBurstRotations.Rotations.SMN_IfritEX", // Summoner
            [28] = "RelicBurstRotations.Rotations.SCH_IfritEX", // Scholar
        };

    private readonly Configuration _config;
    private readonly RsrRotationOverride _override;
    private readonly RsrTargetingOverride _targeting;

    // The job the swap is currently applied for (0 = not applied). Separate from
    // RsrRotationOverride.Active because the override deliberately claims no ownership when the
    // burst rotation was already the user's own selection. NEVER trusted on its own -- Tick
    // re-reads RSR's live choice before deciding there is nothing to do.
    private uint _appliedForJob;

    // Set when the user picked a different rotation themselves while we held the override. We then
    // stay out of the way until they leave the duty, instead of fighting their choice every tick.
    private bool _userTookOver;

    private long _nextAttemptTicks;
    private bool _recovered;

    public IfritBurstRotationSwap(Configuration config, RsrRotationOverride rotationOverride,
        RsrTargetingOverride targetingOverride)
    {
        _config = config;
        _override = rotationOverride;
        _targeting = targetingOverride;
    }

    /// <summary>
    /// What the rotation swap is actually doing right now, for the config window. Null while there
    /// is nothing to report (feature off, or not in the duty, or working).
    /// </summary>
    public string? LastStatus { get; private set; }

    /// <summary>True when <see cref="LastStatus"/> describes a problem rather than a success.</summary>
    public bool LastStatusIsError { get; private set; }

    public void Tick()
    {
        // Crash safety: the breadcrumb lives in Relicable's own config, so a session that was
        // killed inside the duty left the override in place. Undo it once, on the first tick of
        // the next session.
        if (!_recovered)
        {
            _recovered = true;
            RecoverFromPreviousSession();
        }

        var inDuty = Plugin.ClientState.TerritoryType == BowlOfEmbersExtremeTerritory;

        // ---- 1. Targeting override (independent of the rotation swap, and of the job) ----------
        if (inDuty && _config.PrioritiseIfritNailTargeting)
            _targeting.Apply(NailTargetingType);
        else
            _targeting.Clear();

        // ---- 2. Rotation swap ------------------------------------------------------------------
        if (!inDuty)
            _userTookOver = false;

        if (!_config.AutoSwapIfritBurstRotation)
        {
            // Turned off while it was applied: hand the user's choice back immediately.
            if (_appliedForJob != 0 || _override.Active)
                RestoreThrottled();
            else
                SetStatus(null, false);
            return;
        }

        var job = Steps.GameState.ActiveClassJobId();
        if (job == 0)
            return; // zoning / not logged in -- decide nothing on a missing player

        string? rotation = null;
        if (inDuty)
            BurstRotationByClassJobId.TryGetValue(job, out rotation);

        if (rotation == null)
        {
            // Outside the duty, or a job with no burst rotation (including a base class).
            if (_appliedForJob != 0 || _override.Active)
                RestoreThrottled();
            else
                SetStatus(null, false);
            return;
        }

        if (_userTookOver)
            return;

        // Already applied for this job -- but VERIFY rather than trust the cache. Three things can
        // invalidate it without us noticing:
        //   * the user picked a different rotation in RSR's own dropdown (RSR writes the dict AND
        //     saves its config). Silently reverting that on the way out would be a real config
        //     regression, so we drop ownership without writing anything back;
        //   * RSR was reloaded, which builds a fresh Configs from disk and discards our write. The
        //     live value is then the user's own choice and we simply re-apply;
        //   * RSR is momentarily unresolvable, in which case we leave everything alone.
        if (_appliedForJob == job)
        {
            var live = _override.ReadCurrentChoice(job);
            if (live == null || string.Equals(live, rotation, StringComparison.Ordinal))
                return; // still ours, or RSR unreadable -- nothing to do

            if (string.Equals(live, _config.RsrRotationOverridePrevious, StringComparison.Ordinal))
            {
                // Our write was lost (RSR reload). Fall through and re-apply.
                _appliedForJob = 0;
            }
            else
            {
                // A deliberate, different choice. It is theirs now.
                _userTookOver = true;
                _appliedForJob = 0;
                _override.AbandonOwnership($"you selected '{live}' in Rotation Solver while inside the duty");
                SetStatus($"You changed the rotation to '{live}' inside the duty, so Relicable stopped managing it " +
                          "for this visit.", false);
                return;
            }
        }

        if (Environment.TickCount64 < _nextAttemptTicks)
            return;

        if (_override.Apply(job, rotation))
        {
            _appliedForJob = job;
            _nextAttemptTicks = 0;
            SetStatus(null, false);
        }
        else
        {
            // RSR absent, still loading its rotations, or -- overwhelmingly the common case on the
            // official plugin -- the burst rotations are not compiled into the RotationSolver
            // assembly at all. Nothing was mutated; try again shortly.
            _nextAttemptTicks = Environment.TickCount64 + RetryDelayMs;
            SetStatus(_override.LastFailure ?? "The Ifrit EX burst rotation could not be applied.", true);
        }
    }

    // Hand the user's rotation choice back and release the targeting override. Safe to call at any
    // time, including on dispose.
    public void Restore()
    {
        _targeting.Clear();
        _override.Restore(Steps.GameState.ActiveClassJobId());

        // Only forget that we applied it once the override has ACTUALLY gone. Restore keeps the
        // breadcrumb when it could not complete (RSR not loaded, rotations not loaded yet, the
        // config save failed), and zeroing the cache regardless would mean nothing ever retried.
        if (!_override.Active)
        {
            _appliedForJob = 0;
            _nextAttemptTicks = 0;
        }
    }

    // Restore on the same backoff as Apply. Without this a stale breadcrumb plus an absent RSR
    // makes every frame run two full AppDomain.GetAssemblies() scans that can never succeed.
    private void RestoreThrottled()
    {
        if (Environment.TickCount64 < _nextAttemptTicks)
            return;

        Restore();

        if (_override.Active)
        {
            _nextAttemptTicks = Environment.TickCount64 + RetryDelayMs;
            SetStatus("Your Rotation Solver rotation choice is still waiting to be put back " +
                      "(Rotation Solver is not available right now). Relicable keeps retrying.", true);
        }
        else
        {
            SetStatus(null, false);
        }
    }

    private void SetStatus(string? status, bool isError)
    {
        LastStatus = status;
        LastStatusIsError = isError;
    }

    private void RecoverFromPreviousSession()
    {
        if (!_override.Active)
            return;

        // An RSR update installs into a NEW version-stamped directory, which silently replaces a
        // custom RotationSolver build with the official one -- and with it the burst rotations. Say
        // so, because the symptom otherwise is just "my farm got slower at some point".
        var previousVersion = _config.RsrRotationOverrideRsrVersion;
        if (!string.IsNullOrEmpty(previousVersion))
        {
            var current = _override.RsrAssemblyVersion;
            if (current != null && !string.Equals(current, previousVersion, StringComparison.Ordinal))
                Diagnostics.DebugLog.Warn(
                    $"Rotation Solver changed version ({previousVersion} -> {current}) since the Ifrit EX burst " +
                    "rotation was last applied. A Rotation Solver update reinstalls the official build, which does " +
                    "not contain the burst rotations, so the swap will stop working until you rebuild it.");
        }

        // Still standing in the duty: the override is what we want anyway, so leave it and let
        // the tick below re-assert it for whatever job is live now.
        if (Plugin.ClientState.TerritoryType == BowlOfEmbersExtremeTerritory)
            return;

        Diagnostics.DebugLog.Info(
            "An Ifrit EX burst rotation override was still recorded from a previous session; restoring your RSR choice.");
        Restore();
    }
}
