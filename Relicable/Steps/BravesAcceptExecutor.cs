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

// Braves (il125) stage entry: ACCEPT one of the stage's quests from its giver.
//
// This is what a Nexus weapon needs before anything else can run. Until "Wherefore Art Thou, Zodiac"
// (Quest.csv 65892, from Jalzahn) is taken, none of the four material quests are even offered; until a
// material quest is accepted, none of its dungeons are requested, so the whole Braves stage has no
// work and the run simply stopped with a log line nobody was reading.
//
// Flow (the sibling of BravesReportExecutor, minus the hand-over): wait out a duty -> teleport to the
// giver's nearest aetheryte -> approach by data id -> interact (TextAdvance drives the Talk) -> pick
// the quest by name if the NPC answers with a menu (Jalzahn's does: his relic-enhancement branches sit
// alongside it) -> click Accept on the quest offer window. Completes when the quest is in hand.
//
// SEAM: the offer window is ECommons' AddonMaster.JournalAccept (button ids 44/45, stable) but which
// menu the giver opens first is not offline-verifiable, so a menu is driven by the quest NAME as a
// needle and a stuck menu fails with the wording logged rather than looping.
public sealed class BravesAcceptExecutor : ITaskExecutor
{
    private const long AcceptGraceMs = 4000;      // dialogue over -> allow the quest to register
    private const long ActionCooldownMs = 400;    // never re-fire a list addon every frame
    private const long MenuStuckMs = 12_000;      // same menu, unchanged, while we keep picking

    public StepType Handles => StepType.AcceptBravesQuest;

    private enum Phase { Done, Unresolved, WaitExit, Teleport, Interact }

    private readonly AetheryteTeleportExecutor _teleport = new();
    private readonly NpcInteractor _npc = new();

    private Phase _phase;
    private string _quest = string.Empty;
    private uint _questId;
    private string _npcName = string.Empty;
    private uint _npcDataId;
    private Vector3 _npcPos;
    private uint _territory;
    private StepData? _teleStep;
    private long _doneDeadline;
    private long _lastAction;
    private long _menuSince;
    private string _lastMenuSig = string.Empty;

    // Is a Braves stage quest already in hand? Accepted (a live sequence) always counts. Completed
    // counts only for the ONE-TIME umbrella quest: the four material quests are REPEATABLE, so a
    // completed one still has to be re-accepted for the next weapon and must never read as in hand --
    // that difference is why this is not a plain IsQuestComplete.
    public static bool IsInHand(string questName)
    {
        var id = BravesData.MaterialQuestId(questName);
        if (id == 0)
            return false;
        if (GameState.QuestSequence(id) != 0)
            return true;
        foreach (var repeatable in BravesData.MaterialQuests)
            if (string.Equals(repeatable, questName, StringComparison.OrdinalIgnoreCase))
                return false;
        return GameState.IsQuestComplete(id);
    }

    public void Start(StepData step, ExecutionContext ctx)
    {
        _teleStep = null;
        _doneDeadline = 0;
        _lastAction = 0;
        _menuSince = 0;
        _lastMenuSig = string.Empty;

        _quest = ctx.CurrentObjective?.BravesQuest ?? string.Empty;
        _questId = BravesData.MaterialQuestId(_quest);
        var (npc, dataId, territory, pos, _) = BravesData.QuestGiver(_quest);
        _npcName = npc;
        _npcDataId = dataId;
        _npcPos = pos;
        _territory = territory;

        // Already in hand (accepted between selection and now) -> nothing to do; the controller re-plans
        // and skips it.
        if (IsInHand(_quest))
        {
            _phase = Phase.Done;
            return;
        }

        // Already delivered for this weapon (reward item banked): re-accepting would restart a
        // finished quest. The controller skips these; this covers a user-forced run.
        if (BravesData.QuestDelivered(_quest))
        {
            _phase = Phase.Done;
            return;
        }

        // The quest or its giver did not resolve. This must FAIL, not complete: completing would leave
        // the quest still not in hand, the controller would re-select this very objective, and the pair
        // would spin instantly forever. Failing records it as un-acceptable and moves the stage on.
        if (_questId == 0 || _npcDataId == 0)
        {
            _phase = Phase.Unresolved;
            return;
        }

        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Enable();

        _phase = BoundByDuty() ? Phase.WaitExit : StartTrip(ctx);
    }

    private Phase StartTrip(ExecutionContext ctx)
    {
        // Nearest aetheryte to the NPC, not merely one in the zone (the same reason BravesReport does
        // it: a zone-level pick can land you a long walk away).
        var aeth = Locations.NearestAetheryteToWorld(_territory, Locations.MapForTerritory(_territory), _npcPos)
                       ?.AetheryteId
                   ?? Locations.AetheryteForTerritory(_territory);
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

        if (_phase == Phase.Unresolved)
        {
            DebugLog.Warn($"Braves: could not resolve '{_quest}' (quest id {_questId}, giver {_npcDataId}). " +
                          "Accept it yourself, then /relic start.");
            return ExecutorStatus.Failed;
        }

        // Authoritative completion: the quest is in hand. Language- and job-proof, and it fires the
        // instant the accept registers regardless of which window did it.
        if (IsInHand(_quest))
        {
            DebugLog.Info($"Braves: '{_quest}' accepted.");
            return ExecutorStatus.Complete;
        }

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

                var windowUp = DriveAccept();

                if (p == InteractionPhase.Done && !windowUp)
                {
                    // Dialogue ended with no offer window and the quest still not in hand.
                    if (_doneDeadline == 0)
                        _doneDeadline = Environment.TickCount64 + AcceptGraceMs;
                    else if (Environment.TickCount64 > _doneDeadline)
                    {
                        DebugLog.Warn($"Braves: {NpcLabel()} did not offer '{_quest}'. " + WhyNotOffered() +
                                      " Accept it manually, then /relic start.");
                        return ExecutorStatus.Failed;
                    }
                }
                else
                {
                    _doneDeadline = 0;
                }
                return ExecutorStatus.InProgress;

            default:
                return ExecutorStatus.Complete;
        }
    }

    // The likeliest reason a giver has nothing to offer, so the failure names a fix rather than a
    // symptom. Ordered by what actually gates the quest.
    private string WhyNotOffered()
    {
        if (!string.Equals(_quest, BravesData.QuestZodiac, StringComparison.OrdinalIgnoreCase)
            && !IsInHand(BravesData.QuestZodiac))
            return $"'{BravesData.QuestZodiac}' is not in hand, and none of the material quests are offered until it is.";
        if (GameState.EquippedRelicStage() < RelicStage.Nexus)
            return "the Braves stage needs your Nexus weapon equipped.";
        return "It may need a prerequisite this engine does not track.";
    }

    // Drive the quest offer. Returns true while a window we are steering is open, so the caller does
    // not start its "dialogue over" clock mid-flow.
    //
    // Two windows, in the order they appear: the giver may answer with a list first (Jalzahn's relic
    // enhancement branches sit next to the quest), so the quest NAME is used as the needle there; then
    // JournalAccept, whose Accept button is what actually takes the quest.
    private bool DriveAccept()
    {
        var throttled = Environment.TickCount64 - _lastAction < ActionCooldownMs;

        if (TryGetAddonMaster<AddonMaster.JournalAccept>("JournalAccept", out var accept) && accept.IsAddonReady)
        {
            _menuSince = 0;
            if (!throttled)
            {
                _lastAction = Environment.TickCount64;
                accept.Accept();
                DebugLog.Info($"Braves: accepting '{_quest}' from {NpcLabel()}.");
            }
            return true;
        }

        if (!DialogueMenu.AnyOpen())
        {
            _menuSince = 0;
            _lastMenuSig = string.Empty;
            return false;
        }

        // Log each DISTINCT menu once as the flow advances, so the real wording is captured, and give
        // up if our pick never moves it rather than looping to the interaction timeout.
        var sig = DialogueMenu.OpenSignature();
        if (sig.Length > 0 && sig != _lastMenuSig)
        {
            DialogueMenu.LogOpenMenus($"Braves accept ({_quest})");
            _lastMenuSig = sig;
            _menuSince = Environment.TickCount64;
        }
        if (_menuSince != 0 && Environment.TickCount64 - _menuSince > MenuStuckMs)
        {
            DebugLog.Warn($"Braves: {NpcLabel()}'s menu did not lead to '{_quest}' (see the logged entries above). " +
                          "Accept it manually, then /relic start.");
            _menuSince = 0;
            return false;
        }

        if (!throttled)
        {
            _lastAction = Environment.TickCount64;
            foreach (var addon in MenuAddons)
                if (DialogueMenu.IsOpen(addon) && DialogueMenu.SelectByTextSafe(addon, _quest))
                    break;
            DialogueMenu.ConfirmYes();
        }
        return true;
    }

    private static readonly string[] MenuAddons = { "SelectString", "SelectIconString" };

    private string NpcLabel() => string.IsNullOrEmpty(_npcName) ? "the quest giver" : _npcName;

    public void Stop(ExecutionContext ctx)
    {
        _teleport.Stop(ctx);
        ctx.Navmesh.Stop();
        if (ctx.Config.EnableTextAdvance)
            ctx.TextAdvance.Disable();
    }
}
