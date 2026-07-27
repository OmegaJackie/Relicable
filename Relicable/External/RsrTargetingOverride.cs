using System;
using System.Linq;
using System.Reflection;

namespace Relicable.External;

// Pins Rotation Solver Reborn's HOSTILE target-selection order while we are inside a duty, and
// releases it on the way out.
//
// WHY THIS EXISTS
//   Ifrit's Infernal Nails make him invulnerable until every nail is destroyed, so once a nail set
//   is up the ONLY way to advance the fight is to kill nails. Nothing a rotation can pass to
//   IBaseAction.CanUse influences that: verified against RotationSolver.Basic/Actions/
//   ActionTargetInfo.cs at tag 7.5.1.17, FindTargetByType's `targetOverride` switch has no LowMaxHP
//   case and falls through to `_ => isFriendly ? FindFriendly() : FindHostile()`. Only FindFriendly
//   reads targetOverride. FindHostile -> FindHostileRaw sorts hostiles EXCLUSIVELY on
//   DataCenter.TargetingType and never sees the override at all -- and its default arm sorts by
//   HitboxRadius descending, which is always Ifrit.
//
//   DataCenter.TargetingType (DataCenter.cs:262) reads:
//       if (TargetingTypeOverride.HasValue) return TargetingTypeOverride.Value;
//   so `DataCenter.TargetingTypeOverride` is the one supported lever that FindHostileRaw actually
//   honours, and it has a real `case TargetingType.LowMaxHP:` arm that sorts ascending on MaxHp.
//   An Infernal Nail has a tiny fraction of Ifrit's max HP, so LowMaxHP puts nails first the moment
//   they exist and is a complete no-op before that (Ifrit is then the only hostile).
//
//   Unlike the rotation swap, this works on the OFFICIAL RotationSolver plugin -- it needs no fork,
//   because it only reads and writes a property RSR already exposes.
//
// SAFETY
//   DataCenter is `internal static` in RotationSolver.Basic, so everything here is late-bound;
//   any lookup that does not resolve makes the whole thing a logged no-op. TargetingTypeOverride is
//   an in-memory property only -- RSR never persists it -- so a crash cannot leave it stuck, and
//   Clear() is idempotent and safe to call from Dispose.
//
//   We only ever clear an override WE set (tracked by _applied). If some other plugin, or RSR's own
//   UI, has pinned a different value, Apply refuses to take it over and Clear leaves it alone.
public sealed class RsrTargetingOverride
{
    private const string BasicAssemblyName = "RotationSolver.Basic";
    private const string DataCenterTypeName = "RotationSolver.Basic.DataCenter";
    private const string OverridePropertyName = "TargetingTypeOverride";

    // How long to wait before re-scanning the AppDomain after a failed resolve. Apply() is called
    // from the framework tick, so without this an uninstalled RSR would cost a full
    // AppDomain.GetAssemblies() scan every single frame for as long as the player stands in 295.
    private const int ResolveRetryMs = 5000;

    private PropertyInfo? _property;
    private Type? _targetingTypeEnum;
    private long _nextResolveTicks;

    // The value we pushed, or null when we do not currently own the override. Purely in-memory:
    // the override itself is in-memory too, so a session that dies never leaves residue.
    private object? _applied;

    private bool _warned;

    /// <summary>True while Relicable owns RSR's targeting override.</summary>
    public bool Active => _applied != null;

    /// <summary>
    /// Pin RSR's hostile targeting to <paramref name="targetingTypeName"/> (a
    /// <c>RotationSolver.Basic.Data.TargetingType</c> member name, e.g. "LowMaxHP"). Idempotent.
    /// Returns false and changes nothing when RSR is absent, the internals moved, or somebody else
    /// already owns the override.
    /// </summary>
    public bool Apply(string targetingTypeName)
    {
        var property = Resolve();
        if (property == null || _targetingTypeEnum == null)
            return false;

        try
        {
            var desired = Enum.Parse(_targetingTypeEnum, targetingTypeName);
            var current = property.GetValue(null);

            if (_applied == null && current != null && !current.Equals(desired))
            {
                // Someone else (RSR's UI, another plugin) pinned this first. Taking it over would
                // silently change their behaviour and we would have nothing correct to restore.
                return Warn($"RSR's targeting override is already set to '{current}' by something else, so " +
                            "Relicable left it alone. Infernal Nails may not be targeted first in the Bowl of " +
                            "Embers (Extreme).");
            }

            if (current == null || !current.Equals(desired))
            {
                property.SetValue(null, desired);
                Diagnostics.DebugLog.Info(
                    $"RSR targeting override -> {targetingTypeName} (Infernal Nails first in the Bowl of Embers (Extreme)).");
            }

            _applied = desired;
            _warned = false;
            return true;
        }
        catch (Exception ex)
        {
            return Warn($"Setting RSR's targeting override failed, so Infernal Nails may not be targeted first: {ex.Message}");
        }
    }

    /// <summary>
    /// Release the override if -- and only if -- we own it and RSR still holds the value we pushed.
    /// Safe to call at any time, including on dispose and when nothing was ever applied.
    /// </summary>
    public void Clear()
    {
        if (_applied == null)
            return;

        var property = Resolve();
        if (property == null)
        {
            // RSR has gone away. The override lives only in RSR's memory, so it went with it;
            // dropping ownership here is correct and cannot strand a setting.
            _applied = null;
            return;
        }

        try
        {
            var current = property.GetValue(null);
            // If it no longer matches what we pushed, the user or another plugin changed it after
            // us -- that value is now theirs, so leave it.
            if (current != null && current.Equals(_applied))
            {
                property.SetValue(null, null);
                Diagnostics.DebugLog.Info("RSR targeting override released (back to your own Targeting Type setting).");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.DebugLog.Warn($"Releasing RSR's targeting override failed: {ex.Message}");
        }
        finally
        {
            _applied = null;
        }
    }

    private PropertyInfo? Resolve()
    {
        // Cached for the session once found. RSR can be reloaded, which replaces the Configs
        // instance -- but DataCenter is a static type, so the PropertyInfo stays valid across that.
        if (_property != null)
            return _property;

        if (Environment.TickCount64 < _nextResolveTicks)
            return null;

        var basicAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == BasicAssemblyName);
        if (basicAsm == null)
        {
            _nextResolveTicks = Environment.TickCount64 + ResolveRetryMs;
            return null;
        }

        try
        {
            var dataCenter = basicAsm.GetType(DataCenterTypeName);
            var property = dataCenter?.GetProperty(OverridePropertyName, BindingFlags.Public | BindingFlags.Static);
            if (property == null || !property.CanWrite)
            {
                _nextResolveTicks = Environment.TickCount64 + ResolveRetryMs;
                Warn($"RSR's {DataCenterTypeName}.{OverridePropertyName} did not resolve; Infernal Nails will not be " +
                     "forced to the front of RSR's target order (RSR internals changed?).");
                return null;
            }

            // Nullable<TargetingType> -> TargetingType
            _targetingTypeEnum = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!_targetingTypeEnum.IsEnum)
            {
                _nextResolveTicks = Environment.TickCount64 + ResolveRetryMs;
                Warn($"RSR's {OverridePropertyName} is not a nullable enum any more; Infernal Nail targeting is skipped.");
                _targetingTypeEnum = null;
                return null;
            }

            _property = property;
            return _property;
        }
        catch (Exception ex)
        {
            _nextResolveTicks = Environment.TickCount64 + ResolveRetryMs;
            Warn($"Reading RSR's targeting internals failed: {ex.Message}");
            return null;
        }
    }

    // One warning per problem, re-armed by the next success, so a permanently missing RSR does not
    // spam the log every frame.
    private bool Warn(string message)
    {
        if (!_warned)
        {
            _warned = true;
            Diagnostics.DebugLog.Warn(message);
        }
        return false;
    }
}
