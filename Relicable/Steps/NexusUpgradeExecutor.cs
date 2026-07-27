using Relicable.Model;

namespace Relicable.Steps;

// Nexus stage upgrade: once the Novus relic's Light reaches 2000, perform the Novus -> Nexus
// upgrade at Jalzahn. All the travel/interact/menu machinery lives in
// JalzahnUpgradeExecutorBase; this class only supplies the Nexus-specific needles and target.
public sealed class NexusUpgradeExecutor : JalzahnUpgradeExecutorBase
{
    public override StepType Handles => StepType.NexusUpgrade;

    protected override RelicStage TargetStage => RelicStage.Nexus;

    // Stage 2 (inside the Novus branch): the light/Nexus upgrade action. Best-effort needles.
    protected override string[] SubMenuNeedles => new[] { "nexus", "trueshot", "add light", "sacred spring", "light" };

    // Stage 1: Jalzahn's main menu -> the Novus branch (kept as the full phrase so it does not
    // also match a Novus-branch submenu header). "Relic Weapon: Animus Enhancement" is the
    // sibling we skip.
    protected override string[] MainMenuNeedles => new[] { "novus enhancement" };

    protected override string FlowLabel => "Nexus upgrade (Jalzahn)";

    protected override string RegisterFailGuidance =>
        "Jalzahn's dialogue ended but the relic did not upgrade to Nexus. Either the Light is " +
        "not full (need 2000/2000 on the equipped Novus weapon) or the upgrade menu option was " +
        "not matched (see the logged menu entries above). Perform the 'Relic Weapon: Novus " +
        "Enhancement' upgrade at Jalzahn manually if this persists.";
}
