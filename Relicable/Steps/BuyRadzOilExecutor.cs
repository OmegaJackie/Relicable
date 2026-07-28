using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps;

// Base-relic FINAL step (quest sequence 19): buy one Radz-at-Han Quenching Oil from Auriana at
// Revenant's Toll (15 Allagan Tomestones of Poetics), which the subsequent InteractNpc turns in to
// Gerolt to finish the il80 relic.
//
// The entire Auriana drive -- walking to her stall, ranking and walking her several identically
// named "Allagan Tomestones of Poetics (...)" exchanges to find the one that actually stocks the
// item, buying, and answering the confirmation that always follows -- lives in AurianaPoeticsShop,
// which the Zenith step's Thavnairian Mist purchase shares. This class is just the step wrapper:
// which item, how many, and the log label.
public sealed class BuyRadzOilExecutor : ITaskExecutor
{
    public StepType Handles => StepType.BuyRadzOil;

    private readonly AurianaPoeticsShop _shop = new();

    public void Start(StepData step, ExecutionContext ctx)
    {
        _shop.Reset();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        switch (_shop.Tick(NovusData.RadzOilItemId, "Radz-at-Han Quenching Oil", 1, "Buy oil", ctx))
        {
            case AurianaPoeticsShop.Result.Complete:
                DebugLog.Info("Buy oil: quenching oil obtained; heading to Gerolt to finish the relic.");
                return ExecutorStatus.Complete;
            case AurianaPoeticsShop.Result.Failed:
                return ExecutorStatus.Failed;
            default:
                return ExecutorStatus.InProgress;
        }
    }

    public void Stop(ExecutionContext ctx)
    {
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
