using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Interaction;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Relicable.Steps;

// Novus stage: farm Alexandrite by running Mysterious Maps in a loop.
//
// One cycle: decipher a map -> read the dig location from the in-game map flag
// (Globetrotter drops it on decipher) -> teleport to its zone if needed -> travel to
// the spot -> Dig to spawn the coffer -> examine it (spawns guardians) -> clear them
// with RSR -> open it for ~5 Alexandrite. Repeats until step.Count Alexandrite or maps
// run out.
//
// The flag is read straight from AgentMap (territory + world coords), NOT vnavmesh's
// FlagToPoint, because treasure maps are frequently in a different zone and FlagToPoint
// only resolves a flag in the loaded zone.
//
// Live-client seams (cannot be unit-tested here): Decipher (use the map item), Dig (the
// General "Dig" action), and opening the coffer (ObjectKind.Treasure interact). Ids are
// resolved from Lumina by name (see NovusData).
public sealed class TreasureMapExecutor : ITaskExecutor
{
    public StepType Handles => StepType.RunTreasureMaps;

    private enum Phase { NeedMap, Decipher, Travel, Dig, Open, Fight, Resupply }

    private const float DigRange = 5f;
    private const float CofferArrive = 1.0f;        // open once within this HORIZONTAL distance of the coffer
    private const float CofferApproachStop = 0.5f;  // walk right onto the coffer (nav stop distance)
    private const long CofferArriveStuckMs = 2500;  // open anyway once we stop getting closer (nav's best effort)
    private const float CofferSearchRadius = 60f;
    private const float SameFlagEpsilonSq = 25f;  // within 5y counts as "the same flag"
    private const long DecipherWaitMs = 12000;
    private const long DigWaitMs = 8000;
    private const long ActionCooldownMs = 1500;
    private const long DialogCooldownMs = 400;   // confirm prompts promptly, not on the 1.5s action cadence
    private const long PhaseTimeoutMs = 180_000;
    private const uint MorDhonaTerritory = 156;       // Revenant's Toll zone
    private const uint RevenantsTollAetheryte = 24;   // teleport target for restocking maps
    private const int ResupplyTarget = 2;             // maps to hold after a restock trip
    private const long ResupplyDecipherRetryMs = 5000; // re-fire the free-slot decipher if it did not take

    private readonly NpcInteractor _npc = new();
    private Phase _phase;
    private long _phaseStart;
    private long _lastAction;
    private long _lastDialog;
    private long _lastDiag;
    private bool _deciphered;
    private bool _resupplyDecipherFired; // mid-restock: we have triggered the free-slot decipher
    // CombatAssist.DefendSelf's per-caller latch: the id we last armed the backend for, so the
    // mode is re-sent only when the aggressor changes and never per tick.
    private ulong _defendArmedId;
    private long _resupplyDecipherAt;    // when we fired it, so a decipher that did not take is retried
    private Vector3? _handledFlag; // the last flag we already looted, to ignore it as stale
    private uint _alexandriteId;
    private float _cofferBestHoriz; // closest horizontal distance to the coffer reached this Open phase
    private long _cofferBestAt;     // when that closest distance was last improved (detect "cannot get closer")

    public void Start(StepData step, ExecutionContext ctx)
    {
        _alexandriteId = step.ItemId != 0 ? step.ItemId : NovusData.AlexandriteItemId;
        _handledFlag = null;
        _resupplyDecipherFired = false;
        _defendArmedId = 0; // executors are singletons; a stale latch would suppress the re-arm
        _npc.Reset();
        EnterPhase(Phase.NeedMap);
        ctx.TextAdvance.Enable(); // carry the decipher confirm and the loot prompt
        var (goal, endless) = ResolveTarget(step, ctx);
        var targetLabel = endless ? "endless farm" : $"/{goal}";
        DebugLog.Info($"TreasureMap: start. Alexandrite {GameState.InventoryCount(_alexandriteId)} {targetLabel}, maps available {MapsAvailable()}");
    }

    // The Alexandrite goal and whether to farm endlessly. A positive configured
    // AlexandriteTarget (the user-set number) is authoritative -- farm until you hold
    // that many. Otherwise fall back to the step's count and the endless-farm toggle.
    private static (int target, bool endless) ResolveTarget(StepData step, ExecutionContext ctx)
    {
        if (ctx.Config.AlexandriteTarget > 0)
            return (ctx.Config.AlexandriteTarget, false);
        return (step.Count, ctx.Config.EndlessTreasureMapFarm || step.Count <= 0);
    }

    public ExecutorStatus Update(StepData step, ExecutionContext ctx)
    {
        if (_alexandriteId == 0)
        {
            DebugLog.Warn("TreasureMap: could not resolve the Alexandrite item id");
            return ExecutorStatus.Failed;
        }

        // Completion: enough Alexandrite gathered (absolute inventory count). In endless
        // farm mode (config toggle, or a non-positive target) never auto-complete on count
        // -- keep farming and restocking until the user stops.
        var (target, endless) = ResolveTarget(step, ctx);
        if (!endless && GameState.InventoryCount(_alexandriteId) >= target)
        {
            ctx.Navmesh.Stop();
            ctx.Rotation.Disable();
            // Completing on the tick right after the final loot returns here BEFORE the
            // Phase.Open loot branch could clear the flag, so remove the spent flag now --
            // otherwise it dangles into the next stage as a stray "treasure".
            MapFlag.Clear();
            return ExecutorStatus.Complete;
        }

        if (Environment.TickCount64 - _phaseStart > PhaseTimeoutMs)
        {
            DebugLog.Warn($"TreasureMap: phase {_phase} timed out; re-planning");
            ctx.Navmesh.Stop();
            ctx.Rotation.Disable();
            return ExecutorStatus.Failed;
        }

        // Self-defense for every phase that is NOT already a fight. Open and Fight run their own
        // combat handling (Fight reads InCombat and targets the coffer guardians); the rest --
        // restocking, deciphering, walking to the flag, digging -- never read combat state at all,
        // so an ambient hostile that aggroed on the long walk in was never targeted and the loop
        // kept calling Rotation.Disable() while it hit us. Re-mounting is blocked in combat
        // (Mount.cs), so that walk finishes on foot and cannot outrun it either.
        //
        // Freeze the phase clock while defending, or a long fight burns PhaseTimeoutMs and the step
        // fails as "phase timed out" for the wrong reason. The per-phase DecipherWaitMs / DigWaitMs
        // waits read the same clock, so they are frozen with it.
        if (_phase is not (Phase.Open or Phase.Fight)
            && Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
        {
            _phaseStart = Environment.TickCount64;
            return ExecutorStatus.InProgress;
        }

        switch (_phase)
        {
            case Phase.NeedMap:
                // A freshly deciphered map already gives us a flag -> go run it.
                if (HasFreshFlag(out _, out _))
                {
                    EnterPhase(Phase.Travel);
                    break;
                }
                if (MapsAvailable() <= 0)
                {
                    // Out of maps, but a flag may still be set: it is stale (the last looted coffer, or
                    // a map the player ran manually, leaves its flag marker behind). Clear it now so it
                    // is not misread as a fresh treasure later -- by the next decipher's HasFreshFlag,
                    // by a restart of this step, or by other flag-driven steps -- and cannot re-deadlock
                    // the loop. The restock's decipher drops a new, valid flag anyway.
                    MapFlag.Clear();
                    DebugLog.Info("TreasureMap: out of maps; heading to Auriana (Revenant's Toll) to restock");
                    _npc.Reset();
                    EnterPhase(Phase.Resupply);
                    break;
                }
                ctx.Navmesh.Stop();
                ctx.Rotation.Disable();
                EnterPhase(Phase.Decipher);
                break;

            case Phase.Decipher:
                // Globetrotter drops a map flag when the map is deciphered; that flag
                // (read from AgentMap, so it works cross-zone) is the success signal.
                if (HasFreshFlag(out _, out _))
                {
                    EnterPhase(Phase.Travel);
                    break;
                }
                // Walk the Decipher UI: the action opens a "which map?" selection list
                // (even for a single map), then a "Decipher this map?" yes/no. Pick the
                // first map, then confirm.
                if (DialogueMenu.IsOpen("SelectString"))
                {
                    if (DialogThrottle())
                    {
                        DebugLog.Info("TreasureMap: selecting the map");
                        DialogueMenu.Select("SelectString", 0);
                    }
                    break;
                }
                if (DialogueMenu.IsOpen("SelectIconString"))
                {
                    if (DialogThrottle())
                        DialogueMenu.Select("SelectIconString", 0);
                    break;
                }
                if (DialogueMenu.IsOpen("SelectYesno"))
                {
                    if (DialogThrottle())
                    {
                        DebugLog.Info("TreasureMap: confirming decipher");
                        DialogueMenu.ConfirmYes();
                    }
                    break;
                }
                // No prompt up yet: fire the Decipher action once to open it.
                if (!_deciphered)
                {
                    _deciphered = true;
                    DebugLog.Info("TreasureMap: deciphering the map");
                    TreasureHunt.Decipher();
                }
                if (Environment.TickCount64 - _phaseStart > DecipherWaitMs && DiagThrottle())
                    DebugLog.Warn("TreasureMap: no map flag appeared - check Decipher and that Globetrotter places an actual map flag");
                break;

            case Phase.Travel:
                if (!HasFreshFlag(out var flagTerr, out var flagWorld))
                {
                    EnterPhase(Phase.NeedMap); // flag gone/stale; re-evaluate
                    break;
                }

                // Treasure is in another zone: teleport to its nearest aetheryte first.
                if (Plugin.ClientState.TerritoryType != flagTerr)
                {
                    if (Teleporter.IsCasting() || Teleporter.IsZoning() || Teleporter.TeleportRequested())
                        break;
                    var aeth = Locations.AetheryteForTerritory(flagTerr);
                    if (aeth == 0)
                    {
                        DebugLog.Warn($"TreasureMap: no teleport aetheryte known for treasure territory {flagTerr}");
                        return ExecutorStatus.Failed;
                    }
                    if (Throttle())
                    {
                        DebugLog.Info($"TreasureMap: treasure in territory {flagTerr}; teleporting");
                        Teleporter.Teleport(aeth);
                    }
                    break;
                }

                // In the treasure's zone: navigate to the dig spot once the mesh is up AND
                // the player object is loaded. Right after a teleport LocalPlayer is briefly
                // null; do NOT fall back to the destination position or the distance reads
                // as zero and we "arrive" (and start digging) at the aetheryte.
                if (!ctx.Navmesh.IsReady())
                    break;
                var travelPlayer = Plugin.ObjectTable.LocalPlayer;
                if (travelPlayer == null)
                    break;
                // Resolve the precise navmesh point under the flag. FlagToPoint (which
                // vnavmesh probes from Y=1024) gives the correct floor height now that we
                // are in the flag's zone. The AgentMap flag world has no Y (X/Z only), so
                // the fallback resolves it the same high-Y way via FloorForMapPoint rather
                // than a Y=0 nearest-point search; flagWorld is only the last resort when
                // the XZ column has no mesh at all.
                var dest = ctx.Navmesh.FlagToPoint()
                           ?? ctx.Navmesh.FloorForMapPoint(flagWorld, 50f)
                           ?? flagWorld;
                var me = travelPlayer.Position;
                if (Vector3.Distance(me, dest) <= DigRange)
                {
                    ctx.Navmesh.Stop();
                    EnterPhase(Phase.Dig);
                }
                else
                {
                    // dest carries a navmesh-resolved height (from FlagToPoint /
                    // NearestPoint), so flying to it is safe; honor AllowFlight for speed.
                    Combat.Mount.EnsureMounted(ctx, Vector3.Distance(me, dest));
                    ctx.Navmesh.MoveCloseTo(dest, Flight.Allowed(ctx), DigRange - 1f);
                }
                break;

            case Phase.Dig:
                // Land properly when arriving by air (descend to a floor point and
                // dismount) so we are grounded on the spot before digging.
                if (!Combat.Mount.IsGrounded())
                {
                    Combat.Mount.LandAndDismount(ctx, Plugin.ObjectTable.LocalPlayer?.Position ?? default);
                    break;
                }
                if (FindCoffer() is not null)
                {
                    DebugLog.Info("TreasureMap: coffer found");
                    EnterPhase(Phase.Open);
                    break;
                }
                // Travel has already brought us onto the spot (it only enters Dig after
                // genuinely arriving with the player loaded), so dig here.
                if (Throttle())
                {
                    DebugLog.Info("TreasureMap: digging");
                    TreasureHunt.Dig();
                }
                // Dig can miss if we stopped a little off the spot; nudge back to the flag.
                if (Environment.TickCount64 - _phaseStart > DigWaitMs && FindCoffer() is null)
                {
                    DebugLog.Warn("TreasureMap: dug but no coffer appeared - check the 'Dig' action and that we are on the exact spot");
                    EnterPhase(Phase.Travel);
                }
                break;

            case Phase.Open:
                if (FindCoffer() is not { } coffer)
                {
                    // No coffer => it was opened/looted. REMOVE the spent map flag so its
                    // lingering marker is not misread as a fresh treasure -- any leftover flag
                    // reads as "a map to run" (both on the next loop iteration and if this step
                    // is restarted, and by other flag-driven steps that follow). Also remember
                    // its position as a backstop in case the clear does not take.
                    if (MapFlag.TryGetFlag(out _, out _, out var doneWorld))
                        _handledFlag = doneWorld;
                    MapFlag.Clear();
                    DebugLog.Info("TreasureMap: coffer looted; cleared the map flag, on to the next map");
                    EnterPhase(Phase.NeedMap);
                    break;
                }
                // Auto-confirm the "Open this treasure coffer?" Yes/No prompt (both the
                // examine that spawns guardians and the final open). TextAdvance does not
                // carry this one.
                if (DialogueMenu.IsOpen("SelectYesno"))
                {
                    if (DialogThrottle())
                    {
                        DialogueMenu.ConfirmYes();
                        DebugLog.Info("TreasureMap: confirming the treasure prompt");
                    }
                    break;
                }
                var here = Plugin.ObjectTable.LocalPlayer?.Position ?? coffer.Position;
                // Walk fully ONTO the coffer before opening. Gate on HORIZONTAL distance -- a coffer's
                // object origin sits above the floor, so the 3D distance never falls to the interact
                // reach even standing on it ("a hair too far to open"). Interact once within
                // CofferArrive, OR once we have stopped getting any closer (nav's best effort -- the
                // coffer's own footprint can block the exact point), so we never loop just short of it.
                var cofferHoriz = Vector2.Distance(new(here.X, here.Z), new(coffer.Position.X, coffer.Position.Z));
                if (cofferHoriz < _cofferBestHoriz - 0.1f)
                {
                    _cofferBestHoriz = cofferHoriz;
                    _cofferBestAt = Environment.TickCount64;
                }
                var stalledClose = Environment.TickCount64 - _cofferBestAt > CofferArriveStuckMs;
                if (cofferHoriz > CofferArrive && !stalledClose)
                {
                    ctx.Navmesh.MoveCloseTo(coffer.Position, false, CofferApproachStop);
                    if (DiagThrottle())
                        DebugLog.Info($"TreasureMap: approaching coffer, {cofferHoriz:0.0}y horizontal (best {_cofferBestHoriz:0.0})");
                    break;
                }
                ctx.Navmesh.Stop();
                // Examining the coffer spawns guardians and puts us in combat; fight them
                // first, then return and open it. Gate on actually being in combat, NOT on
                // nearby mobs -- ambient zone enemies near the dig site would otherwise
                // stall the open forever.
                if (Plugin.Condition[ConditionFlag.InCombat])
                {
                    EnterPhase(Phase.Fight);
                    break;
                }
                if (Throttle())
                {
                    DebugLog.Info("TreasureMap: interacting with the coffer");
                    TreasureHunt.Interact(coffer);
                }
                break;

            case Phase.Fight:
                // A leftover "Open this treasure coffer?" prompt keeps us in an event
                // state and blocks RSR; dismiss it before fighting.
                if (DialogueMenu.IsOpen("SelectYesno"))
                {
                    if (DialogThrottle())
                        DialogueMenu.ConfirmYes();
                    break;
                }
                // Done when combat ends. (Ambient mobs that aggro are fought too, which is
                // fine; what matters is that we stop fighting once out of combat so we can
                // reopen the coffer.)
                if (!Plugin.Condition[ConditionFlag.InCombat])
                {
                    ctx.Rotation.Disable();
                    DebugLog.Info("TreasureMap: guardians cleared; reopening the coffer");
                    EnterPhase(Phase.Open); // cleared; reopen the coffer
                    break;
                }
                // Coffer guardians are aggroed (they have a target), so RSR engages them
                // under any hostile-target setting. Set a target and let RSR clear them.
                if (ctx.Targeting.EngageNearestHostile(fateBound: false))
                {
                    ctx.Rotation.EnableAuto();
                    Combat.CombatAssist.Engage(ctx);
                }
                else
                {
                    ctx.Rotation.Disable();
                }
                break;

            case Phase.Resupply:
                // Restock to two maps per trip. The undeciphered "Mysterious Map" is unique
                // (only one can be held at a time), so a single Auriana visit cannot buy two
                // outright. The sequence is: buy one, decipher it to free that slot, then buy
                // a second. Resume farming once two maps are held.
                if (MapsAvailable() >= ResupplyTarget)
                {
                    _npc.Reset();
                    EnterPhase(Phase.NeedMap);
                    break;
                }
                if (NovusData.AurianaDataId == 0)
                {
                    DebugLog.Warn("TreasureMap: cannot restock - Auriana NPC id not resolved");
                    return ExecutorStatus.Failed;
                }
                // Get to Revenant's Toll first; both the purchase and the slot-freeing
                // decipher happen here.
                if (Plugin.ClientState.TerritoryType != MorDhonaTerritory)
                {
                    if (Teleporter.IsCasting() || Teleporter.IsZoning() || Teleporter.TeleportRequested())
                        break;
                    if (Throttle())
                    {
                        DebugLog.Info("TreasureMap: teleporting to Revenant's Toll to restock");
                        Teleporter.Teleport(RevenantsTollAetheryte);
                    }
                    break;
                }

                // No undeciphered map held -> buy one from Auriana's Mysterious Map Exchange.
                // Drive the dialogs directly (a vendor SelectString does not reliably flip the
                // "in event" flag): pick "Mysterious Map Exchange", then confirm the buy.
                if (UndecipheredCount() == 0)
                {
                    _resupplyDecipherFired = false; // the next decipher starts clean
                    // Auriana's exchange menu can be a text list (SelectString) or an icon
                    // list (SelectIconString); handle both. Pick the "Mysterious Map" entry,
                    // then confirm the Yes/No buy prompt.
                    if (DialogueMenu.IsOpen("SelectString"))
                    {
                        if (DialogThrottle())
                        {
                            DialogueMenu.SelectByText("SelectString", "Mysterious Map");
                            DebugLog.Info("TreasureMap: selecting Mysterious Map Exchange (SelectString)");
                        }
                        break;
                    }
                    if (DialogueMenu.IsOpen("SelectIconString"))
                    {
                        if (DialogThrottle())
                        {
                            DialogueMenu.SelectByText("SelectIconString", "Mysterious Map");
                            DebugLog.Info("TreasureMap: selecting Mysterious Map Exchange (SelectIconString)");
                        }
                        break;
                    }
                    if (DialogueMenu.IsOpen("SelectYesno"))
                    {
                        if (DialogThrottle())
                        {
                            DialogueMenu.ConfirmYes();
                            DebugLog.Info("TreasureMap: confirming map purchase");
                        }
                        break;
                    }
                    // Some other menu is up (an unhandled shop/dialog addon): do NOT
                    // re-interact, which would toggle it shut. Log what is open so the exact
                    // addon and labels can be handled, then wait.
                    if (DialogueMenu.AnyOpen())
                    {
                        if (DiagThrottle())
                            DialogueMenu.LogOpenMenus("Auriana");
                        break;
                    }
                    // Nothing open: walk to Auriana and interact to open her menu. She stands behind a
                    // market stall, so approach the authored spot in FRONT of the counter (and interact
                    // over it) instead of homing on her exact position, which paths behind the shop.
                    if (_npc.Tick(NovusData.AurianaDataId, NovusData.AurianaApproachPosition, ctx, approachFromPlayerSide: true) == InteractionPhase.Failed)
                    {
                        DebugLog.Warn("TreasureMap: could not reach Auriana to restock");
                        return ExecutorStatus.Failed;
                    }
                    break;
                }

                // Holding an undeciphered map -> decipher it to free the unique slot so we can
                // buy the second. Close Auriana's menu first if it lingered after the buy,
                // then fire the Decipher action once.
                if (!_resupplyDecipherFired)
                {
                    if (DialogueMenu.IsOpen("SelectString"))
                    {
                        if (DialogThrottle())
                            DialogueMenu.FireClose("SelectString");
                        break;
                    }
                    if (DialogueMenu.IsOpen("SelectIconString"))
                    {
                        if (DialogThrottle())
                            DialogueMenu.FireClose("SelectIconString");
                        break;
                    }
                    // Be fully OUT of Auriana's event before firing Decipher. The Decipher GENERAL
                    // action is blocked while occupied in an NPC event, so firing it the instant her
                    // exchange menu closes no-ops -- the map is never deciphered, UndecipheredCount stays
                    // 1, and the loop below waits forever for a decipher UI that never opens (the reported
                    // "stuck, doesn't repurchase"). The normal Decipher phase works precisely because it
                    // runs outside any NPC event; mirror that here by waiting for the event to end.
                    if (Interaction.EventConditions.InEvent)
                        break;
                    if (DialogThrottle())
                    {
                        TreasureHunt.Decipher();
                        _npc.Reset(); // re-open her menu cleanly for the second purchase
                        _resupplyDecipherFired = true;
                        _resupplyDecipherAt = Environment.TickCount64;
                        DebugLog.Info("TreasureMap: deciphering the first map to free a slot");
                    }
                    break;
                }
                // Decipher UI is up: pick the (single) held map, then confirm. Once it is
                // deciphered the undeciphered count drops to zero and the buy branch above
                // purchases the second map.
                if (DialogueMenu.IsOpen("SelectString"))
                {
                    if (DialogThrottle())
                        DialogueMenu.Select("SelectString", 0);
                    break;
                }
                if (DialogueMenu.IsOpen("SelectIconString"))
                {
                    if (DialogThrottle())
                        DialogueMenu.Select("SelectIconString", 0);
                    break;
                }
                if (DialogueMenu.IsOpen("SelectYesno"))
                {
                    if (DialogThrottle())
                        DialogueMenu.ConfirmYes();
                    break;
                }
                // Backstop: the Decipher fired but no UI appeared and the map is still undeciphered
                // (e.g. it was swallowed by a lingering event). After a short wait, re-arm so it re-fires
                // rather than looping here forever. Safe because we only reach this branch while
                // UndecipheredCount != 0 -- a decipher that DID take drops the count and routes to the
                // buy branch above, so this never re-fires a successful decipher.
                if (Environment.TickCount64 - _resupplyDecipherAt > ResupplyDecipherRetryMs)
                {
                    _resupplyDecipherFired = false;
                    DebugLog.Warn("TreasureMap: free-slot decipher did not take; retrying");
                }
                break;
        }

        return ExecutorStatus.InProgress;
    }

    public void Stop(ExecutionContext ctx)
    {
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        Combat.CombatAssist.Disengage(ctx);
    }

    private void EnterPhase(Phase p)
    {
        _phase = p;
        _phaseStart = Environment.TickCount64;
        _lastAction = 0;
        if (p == Phase.Decipher)
            _deciphered = false;
        if (p == Phase.Open)
        {
            // Reset the closest-approach tracking each time we (re)enter Open -- after clearing the
            // coffer guardians the character has moved, so the "cannot get closer" detection must
            // start fresh for the reopen.
            _cofferBestHoriz = float.MaxValue;
            _cofferBestAt = Environment.TickCount64;
        }
    }

    private bool Throttle()
    {
        if (Environment.TickCount64 - _lastAction < ActionCooldownMs)
            return false;
        _lastAction = Environment.TickCount64;
        return true;
    }

    // Faster cadence for dismissing UI prompts so they do not linger (a lingering
    // prompt keeps the character in an event state and blocks RSR).
    private bool DialogThrottle()
    {
        if (Environment.TickCount64 - _lastDialog < DialogCooldownMs)
            return false;
        _lastDialog = Environment.TickCount64;
        return true;
    }

    // Slow cadence for diagnostic logging so it does not spam the log every frame.
    private bool DiagThrottle()
    {
        if (Environment.TickCount64 - _lastDiag < 1000)
            return false;
        _lastDiag = Environment.TickCount64;
        return true;
    }

    // A flag is "fresh" if it is set and is not the one we already looted (the in-game
    // flag marker lingers after a chest is opened, so we must ignore the stale one and
    // decipher the next map instead of walking back to the emptied spot).
    private bool HasFreshFlag(out uint territory, out Vector3 world)
    {
        territory = 0;
        world = default;
        if (!MapFlag.TryGetFlag(out territory, out _, out world))
            return false;
        // A flag only marks a live, diggable treasure while we still HOLD its map. Deciphering a
        // "Mysterious Map" converts it to an "Alexandrite Map" that stays in inventory (counted by
        // MapsAvailable) from decipher until the coffer is looted, so a valid dig always has
        // MapsAvailable >= 1. Once maps hit 0 -- the coffer was looted, or the map was run by hand --
        // the in-game flag marker LINGERS (the game does not clear it on loot). Treating that leftover
        // as fresh flew the character to an emptied spot with no coffer to dig or fight, and hijacked
        // the out-of-maps restock path (the reported "bugged location, nothing to attack, won't
        // re-queue"). With no map held, the only correct next move is to restock, so report no flag.
        if (MapsAvailable() <= 0)
            return false;
        if (_handledFlag is { } h && Vector3.DistanceSquared(world, h) <= SameFlagEpsilonSq)
            return false;
        return true;
    }

    // Total relic maps held, across the normal inventory and the Key Items container,
    // under either name (the relic maps live in Key Items rather than the bags).
    private static int MapsAvailable()
        => GameState.InventoryCount(NovusData.MysteriousMapItemId)
         + GameState.InventoryCount(NovusData.AlexandriteMapItemId)
         + GameState.KeyItemCount(NovusData.MysteriousMapKeyId)
         + GameState.KeyItemCount(NovusData.AlexandriteMapKeyId);

    // Undeciphered ("Mysterious Map") maps held. This is the unique-limited form Auriana
    // sells; deciphering converts it to an "Alexandrite Map", freeing the slot to buy
    // another. The restock loop uses this to sequence buy -> decipher -> buy.
    private static int UndecipheredCount()
        => GameState.InventoryCount(NovusData.MysteriousMapItemId)
         + GameState.KeyItemCount(NovusData.MysteriousMapKeyId);

    private static IGameObject? FindCoffer()
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        IGameObject? best = null;
        var bestDist = CofferSearchRadius * CofferSearchRadius;
        foreach (var o in Plugin.ObjectTable)
        {
            if (o.ObjectKind != ObjectKind.Treasure)
                continue;
            var d = Vector3.DistanceSquared(me, o.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = o;
            }
        }
        return best;
    }
}

// Live-client seams for treasure hunting. Kept tiny and isolated so the loop logic
// above stays testable.
internal static unsafe class TreasureHunt
{
    // Open the Decipher UI via the General "Decipher" action. The executor then walks
    // the resulting "which map?" selection list and the confirm prompt. (Using the item
    // directly via AgentInventoryContext did not decipher the relic Key-Item maps.)
    public static void Decipher()
    {
        var am = ActionManager.Instance();
        var dec = NovusData.DecipherGeneralActionId;
        if (am != null && dec != 0)
            am->UseAction(ActionType.GeneralAction, dec);
    }

    // Use the General "Dig" action at the current spot to spawn the coffer.
    public static void Dig()
    {
        var am = ActionManager.Instance();
        var dig = NovusData.DigGeneralActionId;
        if (am != null && dig != 0)
            am->UseAction(ActionType.GeneralAction, dig);
    }

    // Examine/open a treasure coffer.
    public static void Interact(IGameObject obj)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || obj.Address == nint.Zero)
            return;
        Plugin.TargetManager.Target = obj;
        ts->InteractWithObject((CSGameObject*)obj.Address, false);
    }
}
