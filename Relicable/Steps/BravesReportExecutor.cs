using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ECommons.UIHelpers.AddonMasterImplementations;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;
using static ECommons.GenericHelpers;

namespace Relicable.Steps;

// Braves (il125) material-quest report / turn-in. After a dungeon batch is obtained, the quest waits
// for you to report to its NPC (Papana / Guiding Star / Adkin / Brangwine) and HAND OVER the batch,
// which advances it to the next batch (confirmed by the user: the four quests advance via an NPC
// report-back, and the interaction is an item hand-over menu).
//
// Flow: (wait out a duty if still bound) -> teleport to the NPC's zone -> approach the NPC by data id
// -> interact (TextAdvance drives the Talk) -> the item hand-over window (Request): the game auto-fills
// the required Key Items and only ENABLES "Hand Over" when they are all present, so this hands over
// exactly when it can, and if the window sits with Hand Over disabled it means the step also needs
// items you have not gathered (the vendor/crafted/seals materials, which the engine does not farm) ->
// fail with guidance instead of looping -> the quest-advance summary (JournalResult) -> confirm.
// Completes when the quest's sequence changes (advanced, or the final turn-in set it complete).
//
// Which quest is read from ctx.CurrentObjective.BravesQuest; the controller builds a per-quest report
// objective. SEAM: the exact dialog is not offline-verifiable, so the Talk is TextAdvance-driven and
// the Request/JournalResult windows use ECommons' AddonMaster. Wants an in-game run.
public sealed class BravesReportExecutor : ITaskExecutor
{
    private const long AdvanceGraceMs = 4000;    // after the dialog ends, wait for the sequence to change
    private const long HandOverStuckMs = 8000;   // Request open but Hand Over never enables -> missing items
    private const long ActionCooldownMs = 400;

    public StepType Handles => StepType.BravesReport;

    private enum Phase { Done, WaitExit, Teleport, Interact }

    private readonly AetheryteTeleportExecutor _teleport = new();
    private readonly NpcInteractor _npc = new();

    private Phase _phase;
    private string _quest = string.Empty;
    private uint _questId;
    private int _startSeq;
    private uint _npcDataId;
    private Vector3 _npcPos;
    private uint _territory;
    private StepData? _teleStep;
    private long _doneDeadline;
    private long _handOverBlockedSince;
    private long _lastAction;

    public void Start(StepData step, ExecutionContext ctx)
    {
        _teleStep = null;
        _doneDeadline = 0;
        _handOverBlockedSince = 0;
        _lastAction = 0;

        _quest = ctx.CurrentObjective?.BravesQuest ?? string.Empty;
        _questId = BravesData.MaterialQuestId(_quest);
        var (_, dataId, territory, pos, _) = BravesData.TurnInNpc(_quest);
        _npcDataId = dataId;
        _npcPos = pos;
        _territory = territory;
        _startSeq = _questId == 0 ? 0 : GameState.QuestSequence(_questId);

        // Not a known/accepted quest, or the NPC did not resolve -> nothing to do; the controller re-plans.
        if (_questId == 0 || _npcDataId == 0 || _startSeq <= 0)
        {
            _phase = Phase.Done;
            return;
        }

        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        _phase = BoundByDuty() ? Phase.WaitExit : StartTrip(ctx);
    }

    private Phase StartTrip(ExecutionContext ctx)
    {
        var aeth = Locations.AetheryteForTerritory(_territory);
        if (aeth != 0)
        {
            _teleStep = new StepData { Type = StepType.AetheryteTeleport, AetheryteId = aeth };
            _teleport.Start(_teleStep, ctx);
            return Phase.Teleport;
        }
        _npc.Reset();
        return Phase.Interact;
    }

    private static bool BoundByDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_phase == Phase.Done)
            return ExecutorStatus.Complete;

        // Authoritative completion: the quest sequence changed -- it advanced to the next batch, or the
        // final turn-in completed it (sequence -> 0). Job/language proof; never a proxy event.
        if (_questId != 0 && GameState.QuestSequence(_questId) != _startSeq)
            return ExecutorStatus.Complete;

        switch (_phase)
        {
            case Phase.WaitExit:
                if (BoundByDuty())
                    return ExecutorStatus.InProgress;
                _phase = StartTrip(ctx);
                return ExecutorStatus.InProgress;

            case Phase.Teleport:
                var t = _teleport.Update(_teleStep!, ctx);
                if (t == ExecutorStatus.Failed)
                    return ExecutorStatus.Failed;
                if (t == ExecutorStatus.Complete)
                {
                    _teleport.Stop(ctx);
                    _npc.Reset();
                    _phase = Phase.Interact;
                }
                return ExecutorStatus.InProgress;

            case Phase.Interact:
                var p = _npc.Tick(_npcDataId, _npcPos, ctx);
                if (p == InteractionPhase.Failed)
                    return ExecutorStatus.Failed;

                // Hand-over window has sat with "Hand Over" disabled too long -> the step needs items we
                // have not gathered (vendor/crafted/seals). Stop with guidance rather than wait forever.
                if (_handOverBlockedSince != 0 && Environment.TickCount64 - _handOverBlockedSince > HandOverStuckMs)
                {
                    DebugLog.Warn($"Braves report ({_quest}): {NpcName()} will not accept the hand-over -- the quest also " +
                                  "needs items the engine does not farm (the Bombard Core, Sacred Spring Water, the " +
                                  "100k-gil vendor item, and the two crafted items). Gather those (see /relic braves), " +
                                  "then /relic start.");
                    return ExecutorStatus.Failed;
                }

                var windowUp = DriveTurnIn();

                if (p == InteractionPhase.Done && !windowUp)
                {
                    // Dialog ended and no turn-in window is up; give the sequence a moment to change.
                    if (_doneDeadline == 0)
                        _doneDeadline = Environment.TickCount64 + AdvanceGraceMs;
                    else if (Environment.TickCount64 > _doneDeadline)
                    {
                        DebugLog.Warn($"Braves report ({_quest}): the {NpcName()} dialogue ended but the quest did not " +
                                      "advance. It likely needs the non-dungeon items (vendor/crafted/seals) -- see /relic braves.");
                        return ExecutorStatus.Failed;
                    }
                }
                else
                {
                    _doneDeadline = 0; // still talking, or a turn-in window is up
                }
                return ExecutorStatus.InProgress;

            default:
                return ExecutorStatus.Complete;
        }
    }

    // Drive the two quest turn-in windows via ECommons' AddonMaster. Returns true while either is open.
    // Hand over ONLY when the game has enabled it (all required items present); track how long it stays
    // disabled so the caller can fail on missing items. The quest-advance summary (JournalResult) is
    // confirmed with Complete.
    private bool DriveTurnIn()
    {
        var throttled = Environment.TickCount64 - _lastAction < ActionCooldownMs;

        if (TryGetAddonMaster<AddonMaster.Request>("Request", out var req) && req.IsAddonReady)
        {
            if (req.IsHandOverEnabled)
            {
                _handOverBlockedSince = 0;
                if (!throttled)
                {
                    _lastAction = Environment.TickCount64;
                    req.HandOver();
                    DebugLog.Info($"Braves report ({_quest}): handing over the obtained items.");
                }
            }
            else if (_handOverBlockedSince == 0)
            {
                _handOverBlockedSince = Environment.TickCount64;
            }
            return true;
        }
        _handOverBlockedSince = 0;

        if (TryGetAddonMaster<AddonMaster.JournalResult>("JournalResult", out var jr) && jr.IsAddonReady)
        {
            if (!throttled)
            {
                _lastAction = Environment.TickCount64;
                jr.Complete();
                DebugLog.Info($"Braves report ({_quest}): confirming the quest advance.");
            }
            return true;
        }

        return false;
    }

    private string NpcName() => BravesData.TurnInNpc(_quest).Npc is { Length: > 0 } n ? n : "the quest";

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
