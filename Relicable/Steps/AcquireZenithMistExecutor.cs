using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;

namespace Relicable.Steps;

// Zenith step 1: make sure the Thavnairian Mist the Furnace trade costs is in the bags.
//
// The order is bags -> retainers -> buy, and it is deliberately that order because the mist costs
// Poetics, which are farmed:
//   * bags: counted first, so a stocked player skips the trip to Mor Dhona entirely and the run
//     goes straight to the Furnace;
//   * retainers: consulted through PurchaseGuard's cache (inside AurianaPoeticsShop). The cache is
//     a snapshot and can only prove PRESENCE, never absence, so it is used one-directionally --
//     if a retainer is holding mist the step STOPS and says which retainer rather than spending
//     Poetics on a second set. Withdrawing needs a summoning bell and a retainer trip, which is
//     the player's call, not something to do silently mid-run;
//   * buy: the shortfall is bought from Auriana at Revenant's Toll in one exchange.
//
// How many: each weapon in the hands carries its OWN trade cost (3 for a solo main hand; the
// Paladin's Curtana 2 + Holy Shield 1), summed by ZenithData.MistNeededForEquipped.
public sealed class AcquireZenithMistExecutor : ITaskExecutor
{
    public StepType Handles => StepType.AcquireZenithMist;

    private readonly AurianaPoeticsShop _shop = new();
    private readonly AetheryteTeleportExecutor _teleport = new();

    private enum Phase { Check, Teleport, Buy, Done }

    private Phase _phase;
    private StepData? _teleStep;
    private int _need;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _shop.Reset();
        _teleStep = null;
        _phase = Phase.Check;
        _need = ZenithData.MistNeededForEquipped();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        var mist = ZenithData.MistItemId;
        if (mist == 0)
        {
            DebugLog.Warn($"Zenith mist: could not resolve '{ZenithData.MistItemName}' in the Item sheet. " +
                          "Buy it from Auriana (Revenant's Toll, Mor Dhona) yourself, then /relic start.");
            return ExecutorStatus.Failed;
        }

        // Nothing equipped needs a trade (already traded, or the weapon changed under us): there
        // is no mist to buy. The trade step re-checks the same thing and completes.
        if (_need <= 0)
        {
            _need = ZenithData.MistNeededForEquipped();
            if (_need <= 0)
                return ExecutorStatus.Complete;
        }

        // Authoritative: enough mist is in the bags. Checked every tick and BEFORE the trip, so a
        // player who already has it never travels to Mor Dhona at all.
        var have = GameState.InventoryCount(mist);
        if (have >= _need)
        {
            if (_phase != Phase.Done)
            {
                DebugLog.Info($"Zenith mist: {have}x {ZenithData.MistItemName} held (need {_need}); " +
                              "heading to the Furnace.");
                _phase = Phase.Done;
            }
            return ExecutorStatus.Complete;
        }

        switch (_phase)
        {
            case Phase.Check:
                DebugLog.Info($"Zenith mist: {have}/{_need} {ZenithData.MistItemName} held; " +
                              "going to Auriana (Revenant's Toll) for the rest.");
                if (AnimusBookData.MorDhonaAetheryte != 0)
                {
                    _teleStep = new StepData
                    {
                        Type = StepType.AetheryteTeleport,
                        AetheryteId = AnimusBookData.MorDhonaAetheryte,
                    };
                    _teleport.Start(_teleStep, ctx);
                    _phase = Phase.Teleport;
                }
                else
                {
                    // No aetheryte resolved: still try the purchase from wherever we are (the
                    // interactor's own timeout reports honestly if Auriana is out of reach).
                    _phase = Phase.Buy;
                }
                return ExecutorStatus.InProgress;

            case Phase.Teleport:
                var t = _teleport.Update(_teleStep!, ctx);
                if (t == ExecutorStatus.Failed)
                    return ExecutorStatus.Failed;
                if (t == ExecutorStatus.Complete)
                {
                    _teleport.Stop(ctx);
                    _shop.Reset();
                    _phase = Phase.Buy;
                }
                return ExecutorStatus.InProgress;

            default:
                return _shop.Tick(mist, ZenithData.MistItemName, _need, "Zenith mist", ctx) switch
                {
                    // Completion is the bag check at the top of Update, not the shop's own say-so.
                    AurianaPoeticsShop.Result.Complete => ExecutorStatus.InProgress,
                    AurianaPoeticsShop.Result.Failed => ExecutorStatus.Failed,
                    _ => ExecutorStatus.InProgress,
                };
        }
    }

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
