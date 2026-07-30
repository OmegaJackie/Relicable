using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;         // Chat.ExecuteCommand (the /beckon game emote)
using ECommons.Automation.UIInput; // ClickAddonButton extension (GuildLeveDifficulty confirm)
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;         // ActionManager / ActionType (use the lure key item)
using FFXIVClientStructs.FFXIV.Client.Game.Control; // TargetSystem.InteractWithObject (read the Parchment page)
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Relicable.Data;
using Relicable.Diagnostics;
using Relicable.Model;
using Relicable.Steps.Combat;
using static ECommons.GenericHelpers;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Relicable.Steps.Interaction;

// Drives one accepted levequest from accepted -> initiated -> completed:
//   Travel   : navmesh to the leve's start position (Leve.LevelStart)
//   Initiate : open the quest journal for the leve, click Initiate, confirm the
//              difficulty window (ported from Battlevest Utils.Initiate) -> BoundByDuty
//   Fight    : clear objectives with the combat backend
//   done     : the leve leaves the accepted list (completed or expired)
//
// Stateful: Reset(leveId) when a leve is accepted, then Tick(ctx) each frame until it
// returns true. Completion is read from game state (the leve is no longer accepted),
// after first confirming the leve became active.
//
// NOTE: the Initiate phase drives live game addons (JournalDetail / GuildLeveDifficulty)
// and is offline-untestable; verify in-game.
internal sealed unsafe class LeveRunner
{
    private enum Phase { Resolve, Travel, Initiate, Fight, Terminal }

    // Outcome of one item-lure attempt (RunItemLure): Approaching = walking to the bell; Waiting =
    // at the bell, throttled after a use (letting the cast resolve); Fired = the item was used;
    // CannotUse = at the bell but the game will not let us use the item right now (needs more
    // bottles, or briefly unusable). Only CannotUse lets RunItemLure fall through to farm.
    private enum LureResult { Approaching, Waiting, Fired, CannotUse }

    private const float ArriveRange = 5.0f;

    // Combat distances live in Combat.EngageBand, shared with the kill grind and the FATE loop.
    // It keeps the hysteresis those needed -- once engaged, keep fighting until the mob is beyond a
    // looser band, because a single threshold made a mob wobbling across it flip between Disable()
    // and EnableManual() every crossing, the Off/On cycle that stops the backend ever settling into
    // its rotation (worse under BossMod Reborn, where Disable tears the active preset down and
    // EnableManual rebuilds it, so no GCD lands) -- and adds the target's hitbox, which is what a
    // target whose CENTRE never comes within 4y at all needs (a large-hitbox mob, or the leve-868
    // charge sitting above its floor anchor); that case used to re-send Disable() forever while it
    // hit us.
    private const long TimeoutMs = 300_000;

    // ---- Item-lure leves (e.g. "Don't Forget to Cry") ----
    // Close to within this of the prime-location object (the "balor's bell") before using the key
    // item on it. Kept tight so we are comfortably inside the item's use range when GetActionStatus
    // is checked (an over-large approach range would read "out of range" and never fire).
    private const float ItemUseRange = 3.5f;
    // Min gap between UseAction attempts. Set LONGER than the item's ~3s cast plus the target's
    // spawn-in, so after a use we do NOT re-fire the instant the cast completes (which, before the
    // lured enemy is targetable, would burn a second bottle): by the time the throttle expires the
    // enemy is up and RunItemLure's priority 1 has taken over. GetActionStatus also blocks a re-use
    // mid-cast; this is the belt to that suspenders and stops hammering the native call every tick.
    private const long ItemUseThrottleMs = 5000;
    // If we are AT the bell but the game will not let us use the item (CannotUse) for this long, stop
    // standing there and fall through to farm the roaming mobs (which drop more bottles / clear adds),
    // then retry the bell. Guards against a livelock when e.g. the bell needs more bottles than we
    // hold (datamined qty 3) or the item is otherwise persistently unusable.
    private const long LureStuckFarmMs = 6000;

    // ---- "Make the rounds" leves (e.g. "Circling the Ceruleum") ----
    // Travel to within this of a "Destination" marker to spring its ambush. Kept tight (well inside a
    // typical proximity trigger) so we are definitely close enough to spawn the enemy; the kill loop
    // then closes the rest of the gap to whatever spawned.
    private const float DestinationArriveRange = 3.0f;

    // ---- Necrologos "read a page to summon the wave" leves ----
    // These battle leves (e.g. Necrologos: Pale Oblation) start with NO enemies: a "Parchment" event
    // object in the area must be READ to spawn each wave of voidsent. RunFight reads one whenever there
    // is nothing to fight. A normal kill leve has no such object, so the behaviour is inert there.
    private const string ParchmentName = "Parchment";
    private const float ParchmentReadRange = 4.0f;
    private const long WaveGraceMs = 4000; // after a read, let the wave stream in before reading another page
    private const long ActionThrottleMs = 700;
    // Journal (re)open throttle in TryInitiate, applied ONLY AFTER we have managed at least one
    // Initiate click (_initiateClicked). Before that first click the journal is (re)opened on the
    // fast ActionThrottleMs instead: some leves' JournalDetail "will not stay open" (it opens and
    // closes within a frame or two), so opening it only every 5s means the 700ms initiate check
    // almost never catches it ready and the leve never starts -- the reported regression from when
    // this reopen was decoupled to 5s. Reopening fast until the click lands catches that flicker
    // (Battlevest avoids it entirely by standing on the live commence marker, where the journal
    // stays open). The 5s throttle still applies once we HAVE clicked, which is the only place the
    // visible open/close spam it was added for actually occurs (an accepted click that does not
    // commence, re-opening every cycle).
    private const long JournalOpenThrottleMs = 5000;
    // Grace after the leve leaves the active list before finishing, so a post-completion
    // "return to a nearby aetheryte?" prompt has time to appear and be accepted.
    private const long CompleteGraceMs = 2500;
    // Hard cap on how long we keep ticking (accepting the return prompt) after the grace when the
    // "return to a nearby aetheryte?" SelectYesno is still up, so a prompt that never clears cannot
    // hang the run -- we log the open menus and finish anyway.
    private const long CompleteHardCapMs = 15_000;

    // ---- Escort leves ----
    private const float EscortArrive = 4.0f;        // advance to the next waypoint within this
    private const float EscortEngageRange = 15.0f;  // only break to fight a hostile this close
    private const float HoundLagDistance = 8.0f;    // pause + re-beckon if the hound falls this far behind
    private const long BeckonThrottleMs = 3500;     // min gap between /beckon while it is keeping up

    private uint _leveId;
    private Phase _phase = Phase.Terminal;
    private long _startTicks;
    private long _actionThrottle;
    private long _completeDeadline;
    private Vector3 _pos;
    private bool _confirmedActive;
    private bool _sawBound;      // we entered the in-leve (BoundByDuty) state; robust completion signal
    private long _lastIdleLog;   // throttles the "leve run" diagnostic heartbeat
    private long _lastInitiateLog; // throttles the "leve initiate" branch diagnostic
    private long _lastJournalOpen;  // throttles ONLY the journal (re)open in TryInitiate (anti-spam)
    private bool _initiateClicked;  // an Initiate click has landed -> switch the reopen to the slow anti-flicker throttle

    // Assassination leves (e.g. "Someone's Got a Big Mouth" -> "Mimas"): the one named enemy whose
    // death completes the leve, guarded by optional adds. Resolved once in Phase.Resolve; null for a
    // normal kill / escort leve. When set, RunFight prefers this target over the nearest objective.
    private string? _targetName;

    // Item-lure leves (e.g. "Don't Forget to Cry"): use a quest key item on a fixed "prime location"
    // object to lure a hidden enemy out, then slay it. Resolved once in Phase.Resolve; null for a
    // normal leve. When set, RunItemLure replaces the default fight loop.
    private Data.LeveItemLures.ItemLure? _lure;
    private long _lastItemUse;    // throttles UseAction so the ~3s item cast is not re-issued every tick
    private long _lureStuckSince; // when we first reached the bell unable to use the item (0 = not stuck)

    // "Make the rounds" leves (e.g. "Circling the Ceruleum"): the name of the "Destination" marker to
    // travel to, whose proximity springs an ambush. Resolved once in Phase.Resolve; null for a normal
    // leve. When set, RunDestination replaces the default fight loop.
    private string? _destinationName;

    // "Defend the charge" leves (CompanyLeveProtection, e.g. "The Awry Salvages"): the object-table name
    // of the protected CHARGE the enemies attack. Resolved once in Phase.Resolve; null for a normal
    // leve. When set, RunProtection replaces the default fight loop -- holding ON the charge instead of
    // the (possibly far / floor-level) leve anchor so converging attackers come into range.
    private string? _protectionCharge;

    // Escort state (null for a normal kill leve). Resolved once in Phase.Resolve.
    private Data.EscortLevePaths.EscortRoute? _escort;
    private int _wpIndex;
    private long _beckonThrottle;
    private bool _resumeBeckon;   // hound stops while we fight; force a beckon on the next escort tick
    private bool _warnedNoHound;
    private ulong _engagedLeveId; // the leve objective mob we last re-armed RSR + marked (fire once per mob)
    private long _lastParchmentRead; // when we last read a Necrologos page (throttles the next read)

    public void Reset(uint leveId)
    {
        _leveId = leveId;
        _phase = Phase.Resolve;
        _startTicks = Environment.TickCount64;
        _actionThrottle = 0;
        _completeDeadline = 0;
        _confirmedActive = false;
        _sawBound = false;
        _lastIdleLog = 0;
        _lastInitiateLog = 0;
        _lastJournalOpen = 0;
        _initiateClicked = false;
        _targetName = null;
        _lure = null;
        _lastItemUse = 0;
        _lureStuckSince = 0;
        _destinationName = null;
        _protectionCharge = null;
        _escort = null;
        _wpIndex = 0;
        _beckonThrottle = 0;
        _resumeBeckon = false;
        _warnedNoHound = false;
        _engagedLeveId = 0;
        _engagingLeve = false;
        _defendArmedId = 0;
        _lastParchmentRead = 0;
    }

    // True while we are committed to a leve objective inside the hysteresis band (see
    // Combat.EngageBand). Cleared on Reset -- the runner is reused for every leve.
    private bool _engagingLeve;

    // CombatAssist.DefendSelf's per-caller latch: the id we last armed the backend for, so the
    // mode is re-sent only when the aggressor changes and never per tick.
    private ulong _defendArmedId;

    // Returns true when the accepted leve is finished (completed or given up).
    public bool Tick(ExecutionContext ctx)
    {
        if (_phase == Phase.Terminal)
            return true;

        if (Environment.TickCount64 - _startTicks > TimeoutMs)
        {
            DebugLog.Warn($"Leve {_leveId}: run timed out");
            return Finish(ctx);
        }

        // Accept any leve Yes/No prompt: the "commence?" confirm on initiate and, crucially, the
        // "return to a nearby aetheryte?" prompt that pops when a battle leve completes. Accepting
        // the latter teleports the character back to the settlement (near the levemete) instead of
        // stranding it at the leve site, and clears the modal prompt that would otherwise block the
        // flow. TextAdvance does not auto-confirm these.
        if (DialogueMenu.ConfirmYes())
            return false;

        // Track that we entered the in-leve state. A battle leve sets BoundByDuty from Initiate until
        // it ends, so this is a ROBUST completion anchor independent of the accepted-list bookkeeping.
        var bound = Plugin.Condition[ConditionFlag.BoundByDuty];
        if (bound)
            _sawBound = true;

        // Diagnostic heartbeat (every 5s). NOT gated on _sawBound any more: a leve that gets stuck
        // BEFORE it binds -- e.g. spamming the journal in Phase.Initiate and never starting -- was
        // previously invisible here (the gate meant the heartbeat only appeared after a successful
        // Initiate), which is exactly the "some leves spam the journal" case. Now it shows the phase
        // whether the leve has started or not, so a pre-bind stall is diagnosable.
        if (Environment.TickCount64 - _lastIdleLog > 5000)
        {
            _lastIdleLog = Environment.TickCount64;
            DebugLog.Info($"Leve {_leveId} run: phase={_phase} bound={bound} sawBound={_sawBound} " +
                $"accepted={GameState.IsLeveAccepted(_leveId)} levequestDone={GameState.IsLevequestComplete(_leveId)} " +
                $"confirmedActive={_confirmedActive} yesno={DialogueMenu.IsOpen("SelectYesno")} " +
                $"grace={(_completeDeadline == 0 ? 0 : _completeDeadline - Environment.TickCount64)}");
        }

        // Completion: EITHER the leve left the accepted list after being active (the original signal),
        // OR we were bound inside the leve and BoundByDuty has now dropped (the leve ended -- cleared or
        // failed). The second signal is the fix for "finishes one leve, then stands still forever": a
        // completed battle leve can LINGER in the accepted list (or IsLeveAccepted can read stale), so
        // relying only on the accepted-list transition left the runner sitting in Phase.Fight (which
        // bails while unbound) until the 5-minute timeout. Neither signal fires during Travel/Initiate
        // (the leve is not yet accepted-then-gone, and _sawBound is still false), so an un-started leve
        // is unaffected.
        if (GameState.IsLeveAccepted(_leveId))
            _confirmedActive = true;

        var leveEnded = (_confirmedActive && !GameState.IsLeveAccepted(_leveId)) || (_sawBound && !bound);
        if (leveEnded)
        {
            // Completed. Hold briefly before finishing so a post-completion "return to aetheryte?"
            // prompt has a chance to appear and be accepted by the ConfirmYes above, rather than
            // finishing the instant the leve leaves the active list and stranding the TP prompt.
            if (_completeDeadline == 0)
            {
                // Stop any leftover navigation to the leve anchor IMMEDIATELY, BEFORE the
                // return-to-aetheryte teleport is accepted. The last Fight tick left a
                // MoveCloseTo(_pos) destination active; if it is not cleared here it survives the
                // teleport and vnavmesh routes the character straight back to the leve site
                // afterwards ("teleported back, then the mesh runs me back to the leve").
                ctx.Navmesh.Stop();
                ctx.Rotation.Disable();
                _completeDeadline = Environment.TickCount64 + CompleteGraceMs;
            }
            if (Environment.TickCount64 < _completeDeadline)
                return false;
            // The leve left the active list, but the "return to a nearby aetheryte?" SelectYesno can
            // pop a beat AFTER the short grace. Do NOT finish while it is still up: Finish ends this
            // runner, so the top-of-Tick ConfirmYes stops firing and the prompt is stranded open, which
            // keeps us in the event and blocks the next leve's accept + travel (the reported "completes
            // the objective but then just stands there / never auto-returns"). Keep ticking -- the
            // ConfirmYes above accepts it -- until it clears, bounded by CompleteHardCapMs so a genuinely
            // stuck prompt cannot hang the run (we log which menus are open and finish anyway).
            if (DialogueMenu.IsOpen("SelectYesno"))
            {
                if (Environment.TickCount64 - _completeDeadline < CompleteHardCapMs)
                    return false;
                DialogueMenu.LogOpenMenus($"Leve {_leveId} completion: return prompt not clearing");
            }
            // "run ended", NOT "objective credited": this fires when the leve leaves the active
            // list or BoundByDuty drops, which is not the same as the RelicNote book slot crediting.
            // The executor checks IsLeveComplete(slot) for the real completion; log accurately so
            // this is not misread as "handed in" when the slot has not actually credited.
            DebugLog.Verbose($"Leve {_leveId}: run ended (left active list / BoundByDuty dropped); " +
                $"levequestDone={GameState.IsLevequestComplete(_leveId)}");
            return Finish(ctx);
        }

        switch (_phase)
        {
            case Phase.Resolve:
                var leveName = Sheets.LeveName(_leveId);
                // Anchor position: an authored override for leves whose sheet LevelStart resolves under
                // the walkable floor (the dismount then lands underground and shuttles back and forth),
                // else the sheet's LevelStart -> Level position.
                if ((Data.LeveStartOverrides.ForLeveName(leveName) ?? Sheets.LeveStartPosition(_leveId)) is not { } pos)
                {
                    DebugLog.Warn($"Leve {_leveId}: no objective position; skipping");
                    return Finish(ctx);
                }
                _pos = pos;
                // Escort leves (guide an NPC, not clear a spawn) run a different objective
                // loop; matched by the leve's name against the authored route table.
                _escort = Data.EscortLevePaths.ForLeveName(leveName);
                if (_escort != null)
                    DebugLog.Verbose($"Leve {_leveId}: escort route ({_escort.Waypoints.Count} points, NPC '{_escort.EscortNpcName}')");
                // Assassination leves: one named enemy's death completes the leve, guarded by optional
                // adds; prefer it over the nearest objective so respawning adds cannot stall the run.
                _targetName = Data.LeveNamedTargets.ForLeveName(leveName);
                if (_targetName != null)
                    DebugLog.Verbose($"Leve {_leveId}: priority kill-target '{_targetName}'");
                // Item-lure leves: use a quest key item on a prime-location object to spawn the hidden
                // target, then kill it. A separate objective loop (RunItemLure) from the plain fight.
                _lure = Data.LeveItemLures.ForLeveName(leveName);
                if (_lure != null)
                    DebugLog.Verbose($"Leve {_leveId}: item-lure (kill '{_lure.ItemSourceName ?? "?"}' for item {_lure.ItemId} -> use on '{_lure.PrimeTargetName}' -> slay '{_lure.EmergeTargetName}')");
                // "Make the rounds" leves: travel to "Destination" markers whose proximity springs an
                // ambush, then slay it (RunDestination); a separate loop from the plain fight.
                _destinationName = Data.LeveDestinations.ForLeveName(leveName);
                if (_destinationName != null)
                    DebugLog.Verbose($"Leve {_leveId}: rounds leve (travel to '{_destinationName}' markers to spring ambushes)");
                // Protection leves: defend a stationary charge (an allied object the enemies attack); the
                // leve FAILS if it dies. RunProtection holds ON the charge, not the leve anchor.
                _protectionCharge = Data.LeveProtection.ForLeveName(leveName);
                if (_protectionCharge != null)
                    DebugLog.Verbose($"Leve {_leveId}: protection leve (defend the charge '{_protectionCharge}')");
                _phase = Phase.Travel;
                break;

            case Phase.Travel:
                // If it somehow already started, go straight to combat.
                if (Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    _phase = Phase.Fight;
                    break;
                }

                var me = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                var travel = Vector3.Distance(me, _pos);
                if (travel <= ArriveRange)
                {
                    ctx.Navmesh.Stop();
                    _phase = Phase.Initiate;
                }
                else
                {
                    Combat.Mount.EnsureMounted(ctx, travel);
                    ctx.Navmesh.MoveCloseTo(_pos, Flight.Allowed(ctx), ArriveRange - 1.0f);
                }
                break;

            case Phase.Initiate:
                if (Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    _phase = Phase.Fight;
                    break;
                }
                // Land + dismount BEFORE trying to initiate, mirroring Phase.Fight (which already lands
                // before acting). A bare EnsureDismounted is a NO-OP mid-air (Mount.cs), so a character
                // that flew to the leve start would try to click Initiate while hovering. The game
                // accepts that click but the leve does not COMMENCE (no BoundByDuty), so the journal is
                // re-opened next cycle -- exactly the reported open/close spam. LandAndDismount routes
                // down to a landable floor point first, so we click Initiate properly grounded at the
                // start. A leve reached on foot is already grounded, so this guard is a no-op there.
                if (!Combat.Mount.IsGrounded())
                {
                    ctx.Rotation.Disable();
                    Combat.Mount.LandAndDismount(ctx, _pos);
                    break;
                }
                TryInitiate();
                break;

            case Phase.Fight:
                // The leve ended (cleared / failed) the moment BoundByDuty dropped -- but it can
                // LINGER in the accepted list through the "return to a nearby aetheryte" teleport that
                // pops on completion (so the IsLeveAccepted completion check above has not fired yet).
                // While unbound, do NOT run the land/fight/hold loop below: after the teleport we are
                // far from the anchor (_pos), and the hold-at-anchor branch would issue a fresh
                // MoveCloseTo(_pos) and path straight back to the just-finished leve (the reported
                // "still goes back to the objective after clicking yes to return to the settlement").
                // Stop navigation and wait -- the top-of-Tick ConfirmYes still accepts the TP prompt,
                // and completion is caught above once the leve leaves the accepted list, or by
                // StartLeveExecutor's IsLeveComplete(slot) check.
                if (!Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    ctx.Navmesh.Stop();
                    ctx.Rotation.Disable();
                    break;
                }

                // Leve combat is on the ground. If we flew in from Travel (still mounted / airborne),
                // LAND and dismount BEFORE running the fight loop. A bare EnsureDismounted cannot land
                // a flying character (it just presses dismount, a no-op mid-air), and the fight loop
                // would try to fly to the anchor at the same time -- vnav re-mounts to fly, the
                // dismount drops it, repeat: the character hangs "unable to dismount, running in
                // place". Hold here with RSR off until fully grounded; the loop below then moves on
                // foot only (no fly path anywhere in Fight).
                if (!Combat.Mount.IsGrounded())
                {
                    ctx.Rotation.Disable();
                    Combat.Mount.LandAndDismount(ctx, _pos);
                    break;
                }
                // The leve objective enemies are far below a high-level player, so they never aggro.
                // We therefore drive them like the neutral relic grind: acquire by the leve marker,
                // hard-target, and pull in RSR Manual (see RunFight / EngageLeveObjective) -- an
                // Auto / hostile-type approach can only fight enemies that are already engaged, which
                // these never are.
                if (_escort != null)
                    RunEscort(ctx);
                else if (_lure != null)
                    RunItemLure(ctx);
                else if (_destinationName != null)
                    RunDestination(ctx);
                else if (_protectionCharge != null)
                    RunProtection(ctx);
                else
                    RunFight(ctx);
                break;
        }

        return false;
    }

    // Clear the leve's objective enemies. They are far below a high-level player, so they NEVER
    // aggro -- combat/hostility detection finds nothing, which is why the run neither fought them
    // nor stayed put. Acquire them by their leve-objective marker (EventId.ContentId ==
    // BattleLeveDirector) and pull them exactly like the neutral relic-note grind: hard-target,
    // Attack1-mark, RSR Manual. Hold at the leve anchor when none are loaded (between waves / done).
    private void RunFight(ExecutionContext ctx)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;

        // Assassination leves: go straight for the one named target whose death completes the leve.
        // Its adds are optional and can respawn, so fighting the nearest objective could tunnel them
        // forever without ever finishing. Prefer the named target (found by name, independent of the
        // objective scan) whenever it is loaded; otherwise fall through to clear adds / approach while
        // it streams in. FindNearestEnemy already excludes non-hostile and FATE mobs.
        if (_targetName != null
            && ctx.Targeting.FindNearestEnemy(_targetName, 0, false) is { } primeTarget)
        {
            EngageLeveObjective(ctx, primeTarget);
            return;
        }

        // Fight the current wave first, whenever objective enemies are loaded.
        var objective = ctx.Targeting.FindNearestLeveObjective();
        if (objective != null)
        {
            EngageLeveObjective(ctx, objective);
            return;
        }

        // No leve objective loaded. "Nothing to fight" is not "nothing fighting US", though: an
        // ambient overworld hostile that aggroed is not owned by the leve director, so
        // FindNearestLeveObjective never returns it and this used to turn the rotation off and
        // wander the anchor while it hit us. Defend first; only idle when genuinely unthreatened.
        if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
            return;
        ctx.Rotation.Disable();

        // Some battle leves -- the "Necrologos" family, e.g. Necrologos: Pale Oblation -- start with NO
        // enemies: you must READ a "Parchment" event object to summon each wave, which is then fought as
        // a leve objective above. A normal kill leve has no Parchment, so FindNearestInteractable returns
        // null and this whole block is inert.
        if (ctx.Targeting.FindNearestInteractable(ParchmentName) is { } parchment)
        {
            // During the brief post-read grace, HOLD STILL at the page while the wave streams in. This is
            // the fix for "it does not interact with the Parchment": the read WAS firing, but the very
            // next tick fell through to the anchor wander below, which both cancels the read and walks us
            // off the page, so the wave never spawned. Once the grace elapses with still nothing to
            // fight, read again (self-heals a read that did not take). When the leve is done the page is
            // no longer targetable, so control drops to the anchor hold.
            if (Environment.TickCount64 - _lastParchmentRead < WaveGraceMs)
            {
                ctx.Navmesh.Stop();
                return;
            }
            ReadParchment(ctx, parchment, me);
            return;
        }

        // Nothing to fight and no page to read (a normal kill leve between waves, or the leve is done).
        // Hold at the anchor on foot; the outer Tick finishes when the leve leaves the accepted list.
        // Never fly: Phase.Fight has already grounded us and a fly-move would re-mount and fight the
        // dismount. The anchor is the leve start, so it is always close.
        if (Vector3.Distance(me, _pos) > ArriveRange)
            ctx.Navmesh.MoveCloseTo(_pos, false, ArriveRange - 1.0f);
        else
            ctx.Navmesh.Stop();
    }

    // Item-lure objective loop (e.g. "Don't Forget to Cry", leve 645). Its REAL mechanic is a
    // THREE-object chain (the earlier model was inverted and stalled -- see LeveItemLures): KILL an
    // "item source" enemy (the balor's bell) to obtain a key item, USE that item on a separate "prime
    // location" marker (an EObj), and SLAY the enemy that emerges (the balor). The plain RunFight only
    // clears loaded BattleLeveDirector mobs, so it never runs the lure chain. Each tick, in priority
    // order:
    //   1. An emerged target (the "balor") is loaded -> slay it first (it is what the leve counts).
    //   2. We HOLD the lure item AND a prime-location  -> approach the prime location and USE the item
    //      object is loaded                              on it, which spawns the next emerge target.
    //   3. We hold NO item AND an item-source enemy    -> kill the source (the "balor's bell") to
    //      is loaded (source != null)                    obtain the item. Pull it in Manual like a kill.
    //   4. Otherwise                                   -> clear any other leve objective as a fallback
    //                                                     (also degrades a not-yet-verified lure leve to
    //                                                     a plain fight); when none are loaded, hold at /
    //                                                     return to the anchor so the source + prime
    //                                                     stream in.
    //
    // SEAM (verify in-game): the object-table name strings, that the "prime location" EObj is
    // targetable and the key item is usable on it, and that ItemUseRange sits inside the item's real
    // use range. The outer Tick's 300s timeout is the backstop if any of these is off.
    private void RunItemLure(ExecutionContext ctx)
    {
        var lure = _lure!;
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;
        var haveItem = GameState.KeyItemCount(lure.ItemId) > 0;

        // 1) An emerged target is up: slay it before anything else. Found by name (independent of the
        //    objective scan), so a respawning filler cannot pull us off it. FindNearestEnemy matches an
        //    attackable BattleNpc by name and excludes FATE mobs.
        if (ctx.Targeting.FindNearestEnemy(lure.EmergeTargetName, 0, false) is { } emerged)
        {
            EngageLeveObjective(ctx, emerged);
            return;
        }

        // 2) We hold the lure item and a prime-location object is loaded: use the item ON THE PRIME
        //    LOCATION (an EObj -- NOT the source enemy) to spawn the next emerge target. The item is a
        //    key item (EventItem), so it lives in the Key Items container (KeyItemCount, not
        //    GetInventoryItemCount).
        if (haveItem && ctx.Targeting.FindNearestInteractable(lure.PrimeTargetName) is { } prime)
        {
            if (UseLeveItemOnTarget(ctx, prime, lure.ItemId) != LureResult.CannotUse)
            {
                _lureStuckSince = 0; // approaching / waiting / fired -> making progress; the lure owns this tick
                return;
            }
            // At the prime but the game will not let us use the item yet (out of range briefly, or the
            // marker is spent). Give the status a short window to clear; if it does not, fall through
            // (kill another source / clear objectives) rather than standing there until the leve times
            // out, then retry the prime.
            if (_lureStuckSince == 0)
                _lureStuckSince = Environment.TickCount64;
            if (Environment.TickCount64 - _lureStuckSince < LureStuckFarmMs)
                return;
            DebugLog.Verbose($"Leve {_leveId}: cannot use lure item on '{lure.PrimeTargetName}' for {LureStuckFarmMs / 1000}s; falling through");
        }

        // 3) No item in hand: kill the ITEM-SOURCE enemy (e.g. the "balor's bell") to obtain the lure
        //    item -- it is the enemy that DROPS the item, not a thing to use the item on. The source is
        //    a leve enemy that never aggros, so pull it in Manual exactly like RunFight.
        if (lure.ItemSourceName != null && !haveItem
            && ctx.Targeting.FindNearestEnemy(lure.ItemSourceName, 0, false) is { } source)
        {
            EngageLeveObjective(ctx, source);
            return;
        }

        // 4) Nothing to slay, no source to kill, and either no item or the prime is not loaded: clear
        //    any other leve objective as a fallback (harmless, and it degrades a lure leve whose item
        //    mechanic is not yet verified to a plain fight), excluding the item-source enemy so a held
        //    item is not spent re-killing sources. When none are loaded, hold at / return to the anchor
        //    on foot (Phase.Fight grounded us; never fly here) so the source + prime stream in. The
        //    outer Tick finishes when the leve leaves the accepted list.
        var filler = ctx.Targeting.FindNearestLeveObjective(lure.ItemSourceName);
        if (filler != null)
        {
            EngageLeveObjective(ctx, filler);
            return;
        }

        // An ambient hostile is not director-owned, so no finder above sees it; fight it rather
        // than holding at the anchor with the rotation off while it hits us.
        if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
            return;
        ctx.Rotation.Disable();
        if (Vector3.Distance(me, _pos) > ArriveRange)
            ctx.Navmesh.MoveCloseTo(_pos, false, ArriveRange - 1.0f);
        else
            ctx.Navmesh.Stop();
    }

    // Close to the prime-location object and use the lure key item ON it. Not combat -- RSR off. The
    // native call mirrors Umbra's key-item shortcut (GetActionStatus == 0 gate, then UseAction) with
    // ActionType.EventItem and the EventItem row id, targeted at the object so the game applies it
    // there. Returns the outcome so RunItemLure can tell "making progress" from "stuck, cannot use"
    // (see LureResult); only CannotUse lets it fall through.
    private LureResult UseLeveItemOnTarget(ExecutionContext ctx, IGameObject target, uint itemId)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;
        ctx.Rotation.Disable();
        ctx.Targeting.SetTarget(target);

        if (Vector3.Distance(me, target.Position) > ItemUseRange)
        {
            ctx.Navmesh.MoveCloseTo(target.Position, false, ItemUseRange - 1.0f);
            return LureResult.Approaching;
        }
        ctx.Navmesh.Stop();

        // Recently fired: hold off (let the cast + spawn resolve) rather than re-firing and burning a
        // second bottle. This is normal waiting, not a stall.
        if (Environment.TickCount64 - _lastItemUse < ItemUseThrottleMs)
            return LureResult.Waiting;

        var am = ActionManager.Instance();
        if (am == null)
            return LureResult.Waiting; // transient; try again next tick
        // 0 = usable right now (not on cooldown, not mid-cast, in range) for this target. A non-zero
        // status in range means the game will not let us use it now (spent marker, or wrong target).
        if (am->GetActionStatus(ActionType.EventItem, itemId, target.GameObjectId) != 0)
            return LureResult.CannotUse;

        am->UseAction(ActionType.EventItem, itemId, target.GameObjectId);
        _lastItemUse = Environment.TickCount64;
        DebugLog.Verbose($"Leve {_leveId}: used lure item {itemId} on '{target.Name.TextValue}'");
        return LureResult.Fired;
    }

    // "Make the rounds" objective loop (e.g. "Circling the Ceruleum", the game's BattleLeveRound rule):
    // the objective enemies are HIDDEN until you get close to a "Destination" marker, which springs an
    // ambush, so the plain RunFight -- which only clears loaded BattleLeveDirector mobs -- just holds at
    // the anchor and the leve times out. Each tick, in priority order:
    //   1. An objective enemy is loaded (an ambush sprang) -> slay it.
    //   2. Otherwise                                       -> travel to the nearest "Destination"
    //                                                         marker; getting close springs the next
    //                                                         ambush.
    //   3. Nothing loaded (between rounds / done)          -> hold / return to the anchor.
    //
    // SEAM (verify in-game): the marker is named _destinationName and is a targetable object, and
    // getting within DestinationArriveRange springs the ambush. The outer Tick's 300s timeout is the
    // backstop if either is off.
    private void RunDestination(ExecutionContext ctx)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;

        // 1) An ambush is up: clear the objective enemy first (pull it in Manual like RunFight).
        var objective = ctx.Targeting.FindNearestLeveObjective();
        if (objective != null)
        {
            EngageLeveObjective(ctx, objective);
            return;
        }

        // 2) No enemy loaded: travel to the nearest Destination marker on foot (Phase.Fight has
        //    grounded us; never fly). Getting within range springs the next ambush, which the kill
        //    branch above then clears next tick. An ambient hostile is not director-owned and so is
        //    invisible to the finder above -- fight it instead of walking the route while it hits us.
        if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
            return;
        ctx.Rotation.Disable();
        if (ctx.Targeting.FindNearestInteractable(_destinationName) is { } dest)
        {
            if (Vector3.Distance(me, dest.Position) > DestinationArriveRange)
                ctx.Navmesh.MoveCloseTo(dest.Position, false, DestinationArriveRange - 1.0f);
            else
                ctx.Navmesh.Stop(); // on the marker; the ambush should spring now
            return;
        }

        // 3) No enemy and no Destination loaded (between rounds, or the leve is done): hold at / return
        //    to the anchor on foot so the next marker / mobs stream in. The outer Tick finishes when the
        //    leve leaves the accepted list.
        if (Vector3.Distance(me, _pos) > ArriveRange)
            ctx.Navmesh.MoveCloseTo(_pos, false, ArriveRange - 1.0f);
        else
            ctx.Navmesh.Stop();
    }

    // "Defend the charge" objective loop (CompanyLeveProtection, e.g. "The Awry Salvages" 868, "The
    // Bloodhounds of Coerthas" 855, "Go Home to Mama" 865). The leve is "Defeat the enemies while
    // protecting your charge" and FAILS the instant the protected charge -- a stationary allied OBJECT
    // the enemies converge on and attack -- is destroyed; there is no "clear everything at the anchor"
    // completion. The plain RunFight held at the leve start anchor, which for these leves is NOT where
    // the charge sits (868's anchor was deliberately dropped to the floor to fix a wreck-geometry
    // dismount, well below and away from the charge up on the Agrius wreck), so it never intercepted the
    // attackers, the charge died in ~50s, and the leve failed + looped (re-accepted, re-run, never
    // credited). This holds ON the charge -- acquired LIVE by name so no per-leve deck position is
    // needed -- mirroring the dev's anchor-on-the-artifact fix for the sibling defend leve 875. Each
    // tick, in priority order:
    //   1. An attacker is loaded (a hostile leve-director objective) -> engage the nearest. Held at the
    //      charge, "nearest to me" is the mob actually striking the charge, so we intercept the threat.
    //   2. No attacker loaded -> HOLD ON THE CHARGE (move to it, on foot), so each converging wave spawns
    //      into melee + line-of-sight range instead of hitting an undefended charge.
    //   3. The charge is not loaded (name mismatch / not yet streamed) -> fall back to the plain anchor
    //      hold, degrading no worse than the old RunFight.
    //
    // SEAM (verify in-game): that the charge's object name matches LeveProtection and that vnavmesh can
    // path onto the charge's spot (868's charge sits ~6y above the floor landing anchor on the wreck
    // deck; if the deck is not meshed, the hold closes only as far as vnav allows and may still need a
    // captured deck anchor). The 300s outer-Tick timeout is the backstop.
    private void RunProtection(ExecutionContext ctx)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;

        // 1) Clear the attackers. They carry the (Company)LeveDirector marker and read hostile, so the
        //    standard objective finder sees them; the friendly charge (green, director-linked) is
        //    excluded by that finder's IsHostile filter. Pull in Manual exactly like RunFight.
        var attacker = ctx.Targeting.FindNearestLeveObjective();
        if (attacker != null)
        {
            EngageLeveObjective(ctx, attacker);
            return;
        }

        // 2) No attacker up (between waves): hold ON the charge so the next wave converges onto us. The
        //    charge is friendly, so it is found by name (like the escort hound), not by the hostile
        //    finders. On foot only -- Phase.Fight has grounded us; never fly here.
        //    An ambient hostile is not director-owned either, so fight it first; DefendSelf stands and
        //    fights where we are, so this does not abandon the charge.
        if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
            return;
        ctx.Rotation.Disable();
        if (ctx.Targeting.FindNamed(_protectionCharge) is { } charge)
        {
            // Deliberately the MELEE band even on a ranged job: the point of standing on the charge is
            // to be where the next wave converges, so its attackers come to us. A caster standoff here
            // would park us away from the thing we are meant to be body-blocking for.
            if (Vector3.Distance(me, charge.Position) > Combat.EngageBand.Melee(charge))
                ctx.Navmesh.MoveCloseTo(charge.Position, false, Combat.EngageBand.MeleeStop(charge));
            else
                ctx.Navmesh.Stop();
            return;
        }

        // 3) Charge not loaded (name mismatch, or it has not streamed in yet): hold at / return to the
        //    leve anchor so it and the attackers stream in. No worse than the old RunFight.
        if (Vector3.Distance(me, _pos) > ArriveRange)
            ctx.Navmesh.MoveCloseTo(_pos, false, ArriveRange - 1.0f);
        else
            ctx.Navmesh.Stop();
    }

    // Walk to a Necrologos "Parchment" page and read it (the game's object interaction) to summon the
    // next wave. On foot only -- Phase.Fight has already grounded us, and a fly-move would re-mount.
    // The post-read grace (WaveGraceMs, gated in RunFight) throttles this so the page is read once, not
    // every frame.
    private void ReadParchment(ExecutionContext ctx, IGameObject parchment, Vector3 me)
    {
        if (Vector3.Distance(me, parchment.Position) > ParchmentReadRange)
        {
            ctx.Navmesh.MoveCloseTo(parchment.Position, false, ParchmentReadRange - 1.0f);
            return;
        }
        // Halt, and be grounded, before the read: InteractWithObject does nothing while mounted or
        // airborne (the same rule NpcInteractor enforces), so a residual mount/flight would silently
        // no-op the read. The grace hold in RunFight then keeps us on the page afterwards so the read
        // is not cancelled by the next tick moving off.
        ctx.Navmesh.Stop();
        if (!Combat.Mount.IsGrounded())
        {
            Combat.Mount.EnsureDismounted();
            return;
        }
        InteractObject(parchment);
        _lastParchmentRead = Environment.TickCount64;
        DebugLog.Verbose($"Leve {_leveId}: read a Parchment to summon the next wave");
    }

    // Fire the game's object interaction (the read / RMB) on a world object. Same verified call
    // NpcInteractor uses: TargetSystem.Instance()->InteractWithObject(GameObject*, checkLineOfSight).
    private static void InteractObject(IGameObject obj)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || obj.Address == nint.Zero)
            return;
        Plugin.TargetManager.Target = obj;
        ts->InteractWithObject((CSGameObject*)obj.Address, false);
    }

    // Drive one neutral leve objective mob: close to melee (RSR off while moving), then hard-target
    // it, and on a NEW mob re-arm RSR (it may have auto-off'd after the last kill) and Attack1-mark
    // it, then pull it in RSR MANUAL. Manual attacks the hard target even though the mob never aggros
    // (Auto / TargetsHaveTarget would not). The /enemysign mark goes through the game chat box
    // (ECommons.Chat), not ctx.Commands, which drops native game commands.
    private void EngageLeveObjective(ExecutionContext ctx, IGameObject target)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;
        ctx.Targeting.SetTarget(target);

        // Hysteresis band (see LeveDisengageRange): only a mob that genuinely moved away re-enters
        // the travel branch and turns the backend off. The band itself comes from Combat.EngageBand,
        // which sizes it to the TARGET'S HITBOX (the flat 4y was centre-to-centre, so a large mob's
        // hull kept us permanently "not in range" of something we were standing on) and holds a
        // ranged job at its own standoff instead of walking it into melee.
        var dist = Vector3.Distance(me, target.Position);
        var engage = Combat.EngageBand.Engage(target);
        var stop = Combat.EngageBand.Stop(target);
        if (dist > (_engagingLeve ? Combat.EngageBand.Disengage(target) : engage))
        {
            _engagingLeve = false;
            ctx.Rotation.Disable();
            ctx.Navmesh.MoveCloseTo(target.Position, false, stop);
            return;
        }

        _engagingLeve = true;
        // Inside the band but past melee: close ON FOOT with the rotation left ON. Disabling for a
        // small drift correction is exactly the thrash this band exists to prevent.
        if (dist > engage)
            ctx.Navmesh.MoveCloseTo(target.Position, false, stop);
        else
            ctx.Navmesh.Stop();
        if (target.GameObjectId != _engagedLeveId)
        {
            _engagedLeveId = target.GameObjectId;
            ctx.Rotation.ResyncNextDispatch();
            try { Chat.ExecuteCommand("/enemysign attack1 <t>"); }
            catch (Exception ex) { DebugLog.Warn($"Leve: /enemysign failed: {ex.Message}"); }
        }
        ctx.Rotation.EnableManual();
        CombatAssist.Engage(ctx);
    }

    // Escort-leve objective loop: guide the leve's NPC (the "Mine Hound") along the
    // authored route while clearing ambushes. Each tick, in priority order:
    //   1. A hostile is close      -> target + fight it (the hound waits; re-beckon after).
    //   2. The hound is lagging    -> stop, target + /beckon it, let it catch up.
    //   3. Otherwise               -> keep it following (/beckon on a timer) and walk to
    //                                 the next waypoint on foot.
    // Completion is not detected here: reaching the last point holds position and keeps
    // beckoning until the game marks the leve done, which the outer Tick catches when the
    // leve leaves the accepted list.
    //
    // SEAM (verify in-game): the escort NPC name / the leve name match, and that a single
    // targeted /beckon is what makes the hound advance (cadence in BeckonThrottleMs).
    private void RunEscort(ExecutionContext ctx)
    {
        var me = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;
        var route = _escort!;

        // 1) Clear a nearby ambush before dragging the hound onward. The ambushers are leve
        //    objective mobs and, like the fight-leve enemies, do not aggro at level -- so find them
        //    by the leve marker (not by aggression), gated on distance so a far one does not pull us
        //    off the route, and pull them in Manual just like RunFight.
        var threat = ctx.Targeting.FindNearestLeveObjective();
        if (threat != null && Vector3.Distance(me, threat.Position) <= EscortEngageRange)
        {
            _resumeBeckon = true; // the hound stops while we fight -> force a beckon on resume
            EngageLeveObjective(ctx, threat);
            return;
        }

        // No nearby threat BY THE LEVE MARKER -- but that finder only matches director-owned objects,
        // so an AoE-splash pull, a live FATE mob on the route, or a ranged ambusher holding beyond
        // EscortEngageRange is invisible to it at any distance. Fight whatever is actually on us
        // before dropping the rotation; the hound waits, and _resumeBeckon re-beckons it afterwards.
        if (Combat.CombatAssist.DefendSelf(ctx, ref _defendArmedId))
        {
            _resumeBeckon = true;
            return;
        }

        // No threat at all: guide the hound. RSR off so it does not lock onto the (friendly)
        // hound once we target it.
        ctx.Rotation.Disable();

        var hound = ctx.Targeting.FindNamed(route.EscortNpcName);
        if (hound == null)
        {
            // Not loaded / name mismatch: walk the route anyway (it may auto-follow), but say so once.
            if (!_warnedNoHound)
            {
                DebugLog.Warn($"Escort: '{route.EscortNpcName}' not found near the player; walking the route without beckoning");
                _warnedNoHound = true;
            }
        }
        else
        {
            _warnedNoHound = false;
            if (Vector3.Distance(me, hound.Position) > HoundLagDistance)
            {
                // Falling behind: stop, beckon, and wait for it to close the gap.
                ctx.Navmesh.Stop();
                Beckon(ctx, hound, force: _resumeBeckon);
                _resumeBeckon = false;
                return;
            }

            // Keeping up: nudge it along on a timer (forced right after combat).
            Beckon(ctx, hound, force: _resumeBeckon);
            _resumeBeckon = false;
        }

        // Walk the authored route on foot (mounting would outrun the hound).
        if (_wpIndex >= route.Waypoints.Count)
        {
            // At the final point: hold and keep beckoning until the leve completes.
            ctx.Navmesh.Stop();
            return;
        }

        var wp = route.Waypoints[_wpIndex];
        if (Vector3.Distance(me, wp) <= EscortArrive)
        {
            _wpIndex++;
            ctx.Navmesh.Stop();
            return;
        }
        ctx.Navmesh.MoveCloseTo(wp, false, EscortArrive - 1.0f);
    }

    // Target the escort NPC and perform the /beckon emote so it follows. Throttled unless
    // forced (e.g. resuming from combat, where the hound has stopped). "motion" keeps it to
    // the animation with no chat line. Sent through the game chat box (ECommons.Chat), NOT
    // ctx.Commands: Dalamud's ICommandManager.ProcessCommand only dispatches Dalamud-
    // registered commands and would silently drop a game emote like /beckon.
    private void Beckon(ExecutionContext ctx, IGameObject hound, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _beckonThrottle < BeckonThrottleMs)
            return;
        _beckonThrottle = now;
        ctx.Targeting.SetTarget(hound);
        try { Chat.ExecuteCommand("/beckon motion"); }
        catch (Exception ex) { DebugLog.Warn($"Escort: /beckon failed: {ex.Message}"); }
    }

    // Port of Battlevest Utils.Initiate: open the journal detail for this leve, click
    // Initiate, then confirm the GuildLeveDifficulty window (button id 7). Throttled so
    // each native action has a frame to register. A "commence?" Yes/No is confirmed too.
    private void TryInitiate()
    {
        // Diagnostic (throttled ~2s): which initiate branch are we in, and why is Initiate not taking?
        // This is the decisive line for the "journal spams open/close and the leve never starts" report:
        //   journalReady=false repeatedly => OpenForQuest opens JournalDetail but it does not stay
        //     open/ready to the next tick (so branch d re-opens it -> the spam).
        //   journalReady=true, canInitiate=false => at the journal but the game refuses to Initiate;
        //     distToStart shows whether we are actually AT the leve start (a far value = wrong _pos).
        if (Environment.TickCount64 - _lastInitiateLog > 2000)
        {
            _lastInitiateLog = Environment.TickCount64;
            var diffUp = TryGetAddonByName<AtkUnitBase>("GuildLeveDifficulty", out var d0) && IsAddonReady(d0);
            var jdUp = TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var jd0) && jd0.IsAddonReady;
            var canInit = jdUp && jd0.CanInitiate;
            var here = Plugin.ObjectTable.LocalPlayer?.Position ?? _pos;
            DebugLog.Info($"Leve {_leveId} initiate: diffWindow={diffUp} journalReady={jdUp} canInitiate={canInit} " +
                $"yesno={DialogueMenu.IsOpen("SelectYesno")} distToStart={Vector3.Distance(here, _pos):0.0} " +
                $"grounded={Combat.Mount.IsGrounded()} inFlight={Plugin.Condition[ConditionFlag.InFlight]}");
        }

        if (Environment.TickCount64 - _actionThrottle < ActionThrottleMs)
            return;
        _actionThrottle = Environment.TickCount64;

        // Confirm the difficulty / allowance window if it is up (button id 7 = confirm).
        if (TryGetAddonByName<AtkUnitBase>("GuildLeveDifficulty", out var diff) && IsAddonReady(diff))
        {
            var btn = diff->GetComponentButtonById(7);
            if (btn != null && btn->IsEnabled)
                btn->ClickAddonButton(diff);
            return;
        }

        // Confirm a "Commence levequest?" Yes/No if TextAdvance did not.
        if (DialogueMenu.ConfirmYes())
            return;

        // Initiate from the journal detail once it is showing and enabled.
        if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var jd) && jd.IsAddonReady)
        {
            if (jd.CanInitiate)
            {
                jd.Initiate();
                // A click has landed: from here the fast reopen is no longer needed (the journal was
                // caught open), and the slow throttle now suppresses the post-click flicker if this
                // Initiate does not commence.
                _initiateClicked = true;
            }
            return;
        }

        // Journal not open yet: open it for this leve (type 2 = leve/quest detail; type 2 is correct
        // for EVERY leve, GC and regional -- do NOT change it).
        //
        // This is the ONLY branch that (re)opens the journal, and it is gated on its OWN longer
        // throttle, separate from the 700ms action throttle above. WHY: when an accepted Initiate
        // click does not commence the leve (the leve does not go BoundByDuty -- the real defect, most
        // likely because the character was not properly grounded/positioned at the start), branch (c)
        // above dismisses the journal, and re-opening it every 700ms is the visible open/close spam.
        // Slowing ONLY the re-open caps the flicker WITHOUT ever removing the path (so it can never
        // become a permanent stall: the confirm/initiate branches above still run at 700ms and catch a
        // journal that does become ready). This is symptom relief; the cure is the ground guard in
        // Phase.Initiate plus, if the log shows canInitiate reachable from a far distToStart, a
        // live-marker / per-leve start position. Matches Battlevest's separate open throttle.
        // Reopen fast (ActionThrottleMs) until an Initiate click has landed, so a JournalDetail that
        // will not stay open is caught; slow (JournalOpenThrottleMs) afterwards to avoid the
        // post-click open/close flicker. See the JournalOpenThrottleMs comment.
        var openThrottle = _initiateClicked ? JournalOpenThrottleMs : ActionThrottleMs;
        if (Environment.TickCount64 - _lastJournalOpen < openThrottle)
            return;
        _lastJournalOpen = Environment.TickCount64;
        var agent = AgentQuestJournal.Instance();
        if (agent != null)
            agent->OpenForQuest(_leveId, 2, keepOpen: true);
    }

    private bool Finish(ExecutionContext ctx)
    {
        _phase = Phase.Terminal;
        ctx.Navmesh.Stop();
        ctx.Rotation.Disable();
        CombatAssist.Disengage(ctx);
        return true;
    }
}
