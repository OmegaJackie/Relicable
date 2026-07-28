using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Relicable.Diagnostics;
using Relicable.Model;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Relicable.Steps.Interaction;

internal enum InteractionPhase
{
    Locating,     // NPC not yet in the object table; move toward its area
    Approaching,  // walking into interaction range
    Interacting,  // in range, firing InteractWithObject
    InDialogue,   // conversation open; TextAdvance is carrying it
    Done,
    Failed,
}

// Reusable approach-target-interact phase machine shared by the interaction
// executors. One instance lives per executor; Reset is called when a step begins
// (only one step runs at a time, so the shared instance is safe).
//
// Verified against current FFXIVClientStructs:
//   TargetSystem.Instance()->InteractWithObject(GameObject*, checkLineOfSight)
internal sealed class NpcInteractor
{
    private const float InteractRange = 4.0f;
    // Start landing once this close HORIZONTALLY to the NPC: you cannot interact while mounted
    // or airborne, and flying to a ground NPC otherwise hovers above it so the 3D distance never
    // reaches InteractRange -- the "flies too high above the levemete to accept" symptom.
    private const float LandHorizontal = 8.0f;
    // Only mount/fly to close a gap longer than this; short hops stay on foot so a brief takeoff
    // cannot restart a land/fly oscillation on top of the NPC.
    private const float FlyMinDistance = 30.0f;
    // Close enough HORIZONTALLY to a map-derived (height-guessed) approach anchor to stop moving
    // and let the NPC stream in, rather than commit to the guessed floor. Generous, because the
    // point is only to be inside streaming range with the right XZ -- see the multi-storey note
    // in the Locating phase.
    private const float AnchorHorizontalArrive = 15.0f;
    private const long OverallTimeoutMs = 60000;
    private const long InteractCooldownMs = 600;

    private InteractionPhase _phase = InteractionPhase.Locating;
    private long _startTicks;
    private long _lastInteractTicks;
    private bool _nearApproach;
    private bool _landing; // committed to landing+dismounting for this NPC; sticky until grounded
    private Vector3? _resolvedApproach; // floor-snapped approach point (cached per Reset)
    private Vector3? _frontApproach;    // stall-vendor front standing point (cached per Reset)

    public InteractionPhase Phase => _phase;

    public void Reset()
    {
        _phase = InteractionPhase.Locating;
        _startTicks = Environment.TickCount64;
        _lastInteractTicks = 0;
        _nearApproach = false;
        _landing = false;
        _resolvedApproach = null;
        _frontApproach = null;
    }

    public InteractionPhase Tick(uint dataId, Vector3? approachPos, ExecutionContext ctx, bool approachFromPlayerSide = false)
    {
        if (_phase is InteractionPhase.Done or InteractionPhase.Failed)
            return _phase;

        // While the zone navmesh is still building we cannot path to the NPC; hold and
        // keep the build time from counting against the timeout. A freshly
        // teleported-to zone can take a while to build on first visit, and otherwise
        // the whole budget is spent before navigation can even start.
        if ((_phase is InteractionPhase.Locating or InteractionPhase.Approaching)
            && !ctx.Navmesh.IsReady())
        {
            _startTicks = Environment.TickCount64;
            return _phase;
        }

        if (Environment.TickCount64 - _startTicks > OverallTimeoutMs)
        {
            var detail = _phase != InteractionPhase.Locating ? string.Empty
                : approachPos is null
                    ? $" (npc {dataId} not loaded and no approach position was provided)"
                    : $" (npc {dataId} never loaded; reached its position: {_nearApproach}) - check the data id/position";
            DebugLog.Warn($"Interaction with {dataId} timed out in phase {_phase}{detail}");
            ctx.Navmesh.Stop();
            return _phase = InteractionPhase.Failed;
        }

        var npc = Find(dataId);

        switch (_phase)
        {
            case InteractionPhase.Locating:
                if (npc != null)
                {
                    _phase = InteractionPhase.Approaching;
                    break;
                }
                // Not loaded yet: travel toward the known position so it streams in.
                // Mount for the haul (EnsureMounted no-ops under its own threshold) so
                // a far levemete is reached well within the timeout.
                if (approachPos is { } rawAp)
                {
                    // A map-derived approach point (e.g. ZetaData.RemonPosition) carries
                    // Y = 0, which PathfindAndMoveCloseTo does NOT floor-resolve -- the
                    // same underground-snap hazard fixed for Kill/FATE in 1.4.50.1.
                    // Snap it through the high-Y floor probe once; authored positions
                    // with a real Y are used as-is.
                    var ap = rawAp;
                    var snappedFromMap = false;
                    if (rawAp.Y == 0f)
                    {
                        _resolvedApproach ??= ctx.Navmesh.FloorForMapPoint(rawAp);
                        if (_resolvedApproach is { } snapped)
                        {
                            ap = snapped;
                            snappedFromMap = true;
                        }
                    }
                    var meLoc = Plugin.ObjectTable.LocalPlayer?.Position ?? ap;
                    var apDist = Vector3.Distance(meLoc, ap);
                    _nearApproach |= apDist <= 6.0f;

                    // A map coordinate has no HEIGHT, and both floor probes cast DOWNWARD from
                    // high above -- so inside a multi-storey building they resolve to the TOP
                    // floor, not the one the NPC stands on. Rowena's House of Splendors is the
                    // case that showed it: the run climbed to the second storey, stood there
                    // until she streamed in, then walked back down to her. The climb is pure
                    // waste, because the anchor's only job is to get the NPC to stream in, and
                    // its XZ is already right -- only its Y is a guess.
                    //
                    // So once we are at the anchor HORIZONTALLY, stop and let her load instead of
                    // committing to the guessed floor. Approaching then homes on her live
                    // position, which is authoritative. Gated on a map-derived Y: an authored
                    // real-Y position is a deliberate spot and is still walked onto exactly.
                    // If the NPC never streams in, the existing timeout still fails honestly.
                    var apHoriz = Vector2.Distance(new(meLoc.X, meLoc.Z), new(ap.X, ap.Z));
                    if (snappedFromMap && apHoriz <= AnchorHorizontalArrive)
                    {
                        _nearApproach = true;
                        ctx.Navmesh.Stop();
                        break;
                    }

                    Combat.Mount.EnsureMounted(ctx, apDist);
                    ctx.Navmesh.MoveCloseTo(ap, Steps.Flight.Allowed(ctx), 3.0f);
                }
                break;

            case InteractionPhase.Approaching:
                if (npc == null)
                {
                    _phase = InteractionPhase.Locating;
                    break;
                }
                var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                var npcDist = Vector3.Distance(me, npc.Position);
                var horizontal = Vector2.Distance(new(me.X, me.Z), new(npc.Position.X, npc.Position.Z));

                // Land + dismount before interacting. Decide on HORIZONTAL distance, because while
                // airborne the 3D distance stays large (altitude) and would keep us flying toward
                // the NPC and fighting the descent -- "flies too high to accept". Sticky until
                // grounded so an altitude wobble cannot flip back to flying.
                if (!Combat.Mount.IsGrounded() && (_landing || horizontal <= LandHorizontal))
                {
                    _landing = true;
                    Combat.Mount.LandAndDismount(ctx, npc.Position);
                    break;
                }
                _landing = false;

                // For a counter/stall vendor (opt-in), do NOT home on the NPC's EXACT position -- that
                // routes AROUND behind the shop. Resolve a front standing point once (the caller's
                // authored approach coord if given, floor-snapped; else a player-side offset) and walk
                // ONTO it, interacting once we are on it or already within range of the NPC.
                Vector3? front = null;
                if (approachFromPlayerSide)
                    front = _frontApproach ??= ResolveFront(approachPos, npc.Position, me, ctx);

                var atFront = front is { } fp
                    && Vector2.Distance(new(me.X, me.Z), new(fp.X, fp.Z)) <= 2.0f;

                if (npcDist <= InteractRange || atFront)
                {
                    ctx.Navmesh.Stop();
                    _phase = InteractionPhase.Interacting;
                }
                else
                {
                    // Fly ONLY when already airborne (keep flying; the land block above descends
                    // when close). Do NOT pick the fly mode from the NPC distance: a threshold pick
                    // flipped the fly flag on/off each tick and made vnav jitter / hover in place.
                    // On foot we close on the ground, mounting for a long haul for speed.
                    if (npcDist > FlyMinDistance)
                        Combat.Mount.EnsureMounted(ctx, npcDist);
                    // Walk right ONTO the authored front point (small stop) so we do not halt ~3y short
                    // of it -- possibly still behind the counter. Otherwise close on the NPC as usual.
                    var goal = front ?? npc.Position;
                    var stop = front is null ? InteractRange - 1.0f : 1.0f;
                    ctx.Navmesh.MoveCloseTo(goal, Plugin.Condition[ConditionFlag.InFlight], stop);
                }
                break;

            case InteractionPhase.Interacting:
                // A dialogue is open if the game's event flag is set OR a known menu/dialog addon
                // is visible. Some service NPCs (e.g. Remon's Mahatma attach) open a list menu
                // WITHOUT setting any Occupied* flag, so checking only the flag left us re-firing
                // Interact (RMB spam) over an already-open menu, never reaching the selection.
                if (EventConditions.InEvent || DialogueMenu.AnyOpen())
                {
                    _phase = InteractionPhase.InDialogue;
                    break;
                }
                if (npc == null)
                {
                    _phase = InteractionPhase.Locating;
                    break;
                }
                // Interaction is blocked while mounted or airborne (the RMB does nothing), so a
                // residual mount/flight here must be cleared before firing InteractWithObject --
                // otherwise we spam Interact against a mounted state and never open the dialogue.
                if (!Combat.Mount.IsGrounded())
                {
                    Combat.Mount.EnsureDismounted();
                    break;
                }
                if (Environment.TickCount64 - _lastInteractTicks >= InteractCooldownMs)
                {
                    _lastInteractTicks = Environment.TickCount64;
                    Interact(npc);
                }
                break;

            case InteractionPhase.InDialogue:
                // Stay until TextAdvance (and any menu selection by the executor) closes the
                // conversation: the event flag clear AND no menu/dialog addon still open.
                if (!EventConditions.InEvent && !DialogueMenu.AnyOpen())
                    _phase = InteractionPhase.Done;
                break;
        }

        return _phase;
    }

    // The front standing point for a counter/stall vendor: the caller's authored approach coordinate
    // (floor-snapped when it carries no height, Y==0) if one was provided, else a computed point just
    // inside interact range on the side the player is approaching from.
    private static Vector3 ResolveFront(Vector3? approachPos, Vector3 npc, Vector3 player, ExecutionContext ctx)
    {
        if (approachPos is { } ap)
            return ap.Y == 0f ? (ctx.Navmesh.FloorForMapPoint(ap) ?? ap) : ap;
        return FrontApproachPoint(npc, player);
    }

    // A standing point just inside interact range of the NPC, on the side the player is approaching
    // from. Used for counter/stall vendors (e.g. Auriana) where homing on the NPC's exact position
    // paths behind the shop. Horizontal offset only; keeps the NPC's height (a real floor Y).
    private static Vector3 FrontApproachPoint(Vector3 npc, Vector3 player)
    {
        var dx = player.X - npc.X;
        var dz = player.Z - npc.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.1f)
            return npc; // player essentially on top of the NPC; nothing to offset
        var f = (InteractRange - 0.5f) / len;
        return new Vector3(npc.X + dx * f, npc.Y, npc.Z + dz * f);
    }

    private static unsafe void Interact(IGameObject npc)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || npc.Address == nint.Zero)
            return;

        Plugin.TargetManager.Target = npc;
        ts->InteractWithObject((CSGameObject*)npc.Address, false);
        DebugLog.Verbose($"Interact -> {npc.Name.TextValue} ({npc.BaseId})");
    }

    private static IGameObject? Find(uint dataId)
    {
        IGameObject? best = null;
        var bestDist = float.MaxValue;
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        foreach (var o in Plugin.ObjectTable)
        {
            if (o.BaseId != dataId)
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
