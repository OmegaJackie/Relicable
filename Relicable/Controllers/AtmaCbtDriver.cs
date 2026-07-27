using Relicable.BaseRelic;
using Relicable.Model;
using Relicable.Steps;

namespace Relicable.Controllers;

// Delegates the Atma stage's FATE farm to Croizat's Bundle of Tweaks (CBT) "Fate Tool Kit"
// when Configuration.AtmaBackend == CbtFateToolKit. CBT ships a self-contained "Atma (Zodiac)"
// grind mode (all 12 atma zones, requires a Zenith weapon), which Relicable's own Atma objective
// does not cover -- so this is both a delegation and an upgrade.
//
// CBT exposes no IPC to SELECT the grind mode, so the user picks "Atma (Zodiac)" once in CBT's
// Fate Tool Kit window. Relicable then: enables the tweak (best-effort IPC), starts the run
// (/dwd), watches the 12 atma item counts, and stops CBT once they are all collected. CBT does
// NOT perform the Zenith->Atma enhancement, so at 12/12 the delegation simply ENDS
// (ShouldDelegate false -> EnsureStopped) and the engine's own atma-upgrade objective
// (AtmaUpgradeExecutor) drives the enhancement at Jalzahn.
//
// The controller calls Tick() every running frame and parks its own objective engine while this
// returns true, so the built-in Atma farm objectives never run under the CBT backend.
public sealed class AtmaCbtDriver
{
    public enum Phase { Off, Farming }

    private const int AtmaTotal = 12;
    // Safety upper bound for /dwd run; CBT self-completes at 12 atmas and Relicable stops it on
    // the 12th regardless, so this only caps a runaway if both of those somehow miss.
    private const int RunFateCap = 300;
    // CBT's Fate Tool Kit tweak class name, passed to SetTweakState so its /dwd command registers.
    // Public so the controller's conflict guard can probe the SAME tweak (RelicController checks
    // whether CBT's Fate Tool Kit is running while Relicable is, to step aside for it).
    public const string TweakClassName = "FateToolKit";

    private bool _started;       // /dwd run has been sent for the current farm
    private bool _tweakEnsured;  // best-effort enable attempted for this delegation
    private Phase _phase = Phase.Off;
    private string _status = string.Empty;

    public string Status => _status;

    // True when CBT should own the Atma stage right now: the backend is CBT, the working stage
    // is Atma (per the quest/weapon recognition), a Zenith is equipped (CBT's requirement), and
    // the manual stage pin -- if any -- is Atma. EquippedNeedsZenith gates out "base relic done
    // but Zenith not applied" ON THE EQUIPPED WEAPON, which is a manual item gate that must come
    // first. Deliberately NOT the inventory-wide NeedsZenith: an alt job's untraded relic parked
    // in the armoury chest must not abort a valid Atma grind running on the equipped Zenith.
    public static bool ShouldDelegate(ExecutionContext ctx)
    {
        if (ctx.Config.AtmaBackend != Configuration.AtmaFarmBackend.CbtFateToolKit)
            return false;
        if (ctx.Config.StageMode == StageSelectionMode.Manual && ctx.Config.ManualStage != RelicStage.Atma)
            return false;
        if (BaseRelicState.EquippedNeedsZenith())
            return false;
        // All 12 atmas held: the farm CBT covers is done, so the delegation ends here and the
        // engine's atma-upgrade objective performs the Zenith enhancement at Jalzahn.
        if (GameState.AtmaCollectedCount() >= AtmaTotal)
            return false;
        return ZodiacQuestState.CurrentStage() == RelicStage.Atma;
    }

    // Drive one tick. Returns true while CBT is handling the Atma stage (the controller must skip
    // its own objective engine); false otherwise (and it ensures CBT is not left running).
    public bool Tick(ExecutionContext ctx)
    {
        if (!ShouldDelegate(ctx))
        {
            EnsureStopped(ctx);
            return false;
        }

        // Note: at 12/12 atmas ShouldDelegate is already false (handled above via EnsureStopped),
        // so reaching here means the farm is still running.
        _phase = Phase.Farming;
        if (!_tweakEnsured)
        {
            ctx.Bot?.EnsureTweakEnabled(TweakClassName);
            _tweakEnsured = true;
        }
        if (!_started)
        {
            ctx.Bot?.StartFateGrind(RunFateCap);
            _started = true;
        }
        var held = GameState.AtmaCollectedCount();
        var cbt = ctx.Bot?.Available == true ? string.Empty : " (CBT not detected -- is Bundle of Tweaks installed and enabled?)";
        _status = $"CBT Fate Tool Kit farming Atmas ({held}/{AtmaTotal}). Select the 'Atma (Zodiac)' mode in CBT if it is not already.{cbt}";
        return true;
    }

    // Halt a delegated grind (called when delegation ends or on a deliberate controller Stop).
    public void EnsureStopped(ExecutionContext ctx)
    {
        if (_phase == Phase.Off && !_started)
            return;
        StopCbt(ctx);
        // Disable the Fate Tool Kit tweak once this driver attempted to enable it, so it does not
        // linger past the delegation. Without this, the controller's conflict guard (which pauses
        // Relicable whenever the Fate Tool Kit tweak is enabled) would false-trigger on the tweak
        // and strand the run after the Atma stage ends -- so this deliberately turns the tweak off
        // even if the user had it on, which is exactly what lets the engine's atma-upgrade objective
        // run at 12/12. Gated on _tweakEnsured, so a tweak this driver never touched is left alone.
        if (_tweakEnsured)
            ctx.Bot?.DisableTweak(TweakClassName);
        _phase = Phase.Off;
        _tweakEnsured = false;
        _status = string.Empty;
    }

    private void StopCbt(ExecutionContext ctx)
    {
        if (!_started)
            return;
        ctx.Bot?.StopFateGrind();
        _started = false;
    }
}
