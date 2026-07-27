using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameFunctions; // IGameObject.IsHostile() (nameplate-colour hostility)
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Relicable.Data;

// The targeting layer. KillTarget needs a correct world target before RSR will
// engage; RSR does not select world mobs by name. This class queries the game
// object table (via ECommons Svc.Objects in the real build) and returns the
// nearest valid enemy. This is the single most failure-prone piece, so the
// filtering is deliberately strict.
//
// The IObjectProvider seam keeps this class unit-testable against a synthetic
// table; the live implementation is DalamudObjectProvider.
public interface IObjectProvider
{
    System.Collections.Generic.IEnumerable<IGameObject> Objects { get; }
    Vector3 PlayerPosition { get; }
    void SetTarget(IGameObject obj);
}

public sealed class Targeting
{
    private readonly IObjectProvider _provider;

    public Targeting(IObjectProvider provider) => _provider = provider;

    // Find the nearest hostile, targetable, alive BattleNpc matching name. When
    // fateBound is set, only objects carrying a FATE id are eligible (FATE mobs).
    //
    // preferredId: when set and still a valid candidate, this exact mob is returned regardless of
    // distance -- the approach "lock" that stops KillTarget flip-flopping between two mobs whose
    // straight-line nearest swaps as it flies a winding, multi-level route. avoidId: a mob to skip
    // (one the caller judged unreachable), so a stalled approach can pick a different target.
    public IGameObject? FindNearestEnemy(string? name, uint dataId, bool fateBound,
        ulong preferredId = 0, ulong avoidId = 0)
    {
        var me = _provider.PlayerPosition;

        var candidates = _provider.Objects
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc)
            .Where(IsAttackable)
            .Where(o => MatchesIdentity(o, name, dataId))
            // fateBound: only FATE mobs. Otherwise (open-world relic kill): EXCLUDE FATE mobs so a
            // FATE spawn of the same enemy is not picked and dragged into the FATE.
            .Where(o => fateBound ? IsInFate(o) : MobFateId(o) == 0)
            .Where(o => avoidId == 0 || o.GameObjectId != avoidId)
            .ToList();

        if (preferredId != 0)
        {
            var locked = candidates.FirstOrDefault(o => o.GameObjectId == preferredId);
            if (locked != null)
                return locked;
        }

        return candidates
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }

    public bool Engage(string? name, uint dataId, bool fateBound)
    {
        var t = FindNearestEnemy(name, dataId, fateBound);
        if (t == null)
            return false;
        _provider.SetTarget(t);
        return true;
    }

    // Nearest attackable enemy regardless of name/dataId, or null if none is loaded.
    // Exposed (not just Engage...) so a caller can gate on its distance -- the escort
    // leve only stops to fight a hostile that is actually close, so a distant unrelated
    // mob does not pull it off the route.
    public IGameObject? NearestHostile(bool fateBound)
    {
        var me = _provider.PlayerPosition;
        return _provider.Objects
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc)
            .Where(IsAttackable)
            .Where(o => !fateBound || IsInFate(o))
            // HOSTILE only, for the same reason as NearestHostileInFate: FATE-ring membership does not
            // imply enemy, so a friendly allied guard standing in the ring must not be targeted. The
            // nameplate-colour check drops green friendlies while keeping unengaged enemies (yellow).
            .Where(o => o.IsHostile())
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }

    // Nearest attackable enemy regardless of name/dataId. Used inside a FATE ring
    // where any hostile is a valid target (the ring membership is the filter).
    public bool EngageNearestHostile(bool fateBound)
    {
        var t = NearestHostile(fateBound);
        if (t == null)
            return false;
        _provider.SetTarget(t);
        return true;
    }

    // Nearest attackable enemy belonging to a SPECIFIC fate, by the game's own per-object FateId
    // (MobFateId) rather than FATE-ring geometry. This is precise per-object membership that does
    // NOT depend on the player standing inside the ring (FateManager.CurrentFate) -- so a FATE's
    // mobs are found while we are still at the ring edge or landed just short of a slightly-off
    // authored spawn point. The IsInFate/CurrentFate path returned nothing there, which left the
    // character hard-targeting nothing and "not auto starting the attack" (the reported symptom).
    public IGameObject? NearestHostileInFate(ushort fateId)
    {
        if (fateId == 0)
            return null;
        var me = _provider.PlayerPosition;
        return _provider.Objects
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc)
            .Where(IsAttackable)
            .Where(o => MobFateId(o) == fateId)
            // HOSTILE only. FATE membership (MobFateId) is NOT the same as being an enemy: boss/defense
            // FATEs spawn FRIENDLY allied NPCs that the FATE director tags with the same FateId and that
            // pass IsAttackable (Combatant sub-kind, targetable, alive). "Tower of Power" (fate 486) is
            // exactly this -- its House Haillenarte guards stand in the ring, so the nearest-by-distance
            // pick hard-targeted a GUARD, then re-targeted it every tick, blocking the player from keeping
            // an enemy targeted after manually starting the FATE (the reported symptom). The nameplate
            // colour check keeps only attackable enemies (yellow/red/orange/purple): an unengaged FATE
            // enemy reads yellow and passes, while friendly (green) guards are dropped. Same guard the leve
            // path already applies for director-linked friendly combatants (see FindNearestLeveObjective).
            .Where(o => o.IsHostile())
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }

    // Hard-target the nearest mob belonging to a specific fate (see NearestHostileInFate).
    public bool EngageNearestHostileInFate(ushort fateId)
    {
        var t = NearestHostileInFate(fateId);
        if (t == null)
            return false;
        _provider.SetTarget(t);
        return true;
    }

    // Nearest object of ANY kind whose name matches (case-insensitive). Used to acquire
    // a leve escort NPC (e.g. the "Mine Hound"), which is friendly and so is deliberately
    // skipped by the hostile finders above. Returns null when it is not in the loaded
    // object table.
    public IGameObject? FindNamed(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var me = _provider.PlayerPosition;
        return _provider.Objects
            .Where(o => string.Equals(o.Name.TextValue, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }

    // The nearest interactable (targetable) object whose name matches (case-insensitive) -- e.g. a
    // "Parchment" page in a Necrologos battle leve, which must be READ to summon the objective
    // enemies. Targetable-only so a spent/inactive object is not chosen. Returns null when none is
    // loaded, so a normal leve (which has no such object) is unaffected.
    public IGameObject? FindNearestInteractable(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var me = _provider.PlayerPosition;
        return _provider.Objects
            .Where(o => o.IsTargetable)
            .Where(o => string.Equals(o.Name.TextValue, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }

    // Set the hard target directly (behind the provider so it stays testable). For the
    // escort flow, which targets the friendly hound to /beckon it rather than to fight.
    public void SetTarget(IGameObject obj) => _provider.SetTarget(obj);

    // Nearest attackable enemy currently targeting the player -- an add that has
    // aggroed onto us during a kill. RSR Manual mode only acts on the target we set, so
    // a non-targeted enemy that aggroes is otherwise ignored while we tunnel the (often
    // neutral) relic mob. excludeId skips the intended relic target, which is itself
    // "targeting us" once pulled, so it is not mistaken for an add. Returns null when
    // nothing is attacking us.
    public IGameObject? FindNearestAggressor(ulong playerId, ulong excludeId)
    {
        if (playerId == 0)
            return null;

        var me = _provider.PlayerPosition;
        return _provider.Objects
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc)
            .Where(IsAttackable)
            .Where(o => o.GameObjectId != excludeId)
            .Where(o => o.TargetObjectId == playerId)
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }

    // Target the nearest add attacking the player (excluding the relic target). Returns
    // true when an aggressor was found and set as the hard target, so the caller can
    // let RSR fight it before resuming the relic mob.
    public bool EngageAggressor(ulong playerId, ulong excludeId)
    {
        var a = FindNearestAggressor(playerId, excludeId);
        if (a == null)
            return false;
        _provider.SetTarget(a);
        return true;
    }

    // Authoritative path for Trials of the Braves monster slots: ask the game,
    // via RelicNote.IsMonsterNoteTarget, whether each candidate counts toward the
    // currently active relic note. This avoids name-matching entirely and is
    // robust to localization and same-name non-target mobs.
    // preferredId/avoidId: see FindNearestEnemy. The commitment lock matters most here -- U'Ghamaro
    // Mines (a Trials-of-the-Braves note grind spot) has note mobs on several stone tiers, so the
    // straight-line "nearest" swaps constantly while flying and a per-tick nearest pick shuttles the
    // character between two of them.
    public unsafe IGameObject? FindNearestMonsterNoteTarget(bool fateBound, bool allowFateNote = false,
        ulong preferredId = 0, ulong avoidId = 0)
    {
        var note = RelicNote.Instance();
        if (note == null)
            return null;

        var me = _provider.PlayerPosition;
        IGameObject? best = null;
        var bestDist = float.MaxValue;
        IGameObject? preferred = null;

        foreach (var o in _provider.Objects)
        {
            if (o.ObjectKind != ObjectKind.BattleNpc || !IsAttackable(o))
                continue;
            if (fateBound && !IsInFate(o))
                continue;
            // Open-world note grind: by default skip a FATE spawn of the note mob so we are not pulled
            // into the FATE. When allowFateNote is set (Configuration.AllowFateNoteKills), FATE spawns
            // ARE eligible -- they count as note targets too, and KillTargetExecutor level-syncs to the
            // FATE when it engages one so the backend actually attacks it.
            if (!fateBound && !allowFateNote && MobFateId(o) != 0)
                continue;
            if (avoidId != 0 && o.GameObjectId == avoidId)
                continue;
            if (o.Address == nint.Zero || !note->IsMonsterNoteTarget((CSCharacter*)o.Address))
                continue;

            // Keep the already-committed mob (if still a valid note target) regardless of distance.
            if (preferredId != 0 && o.GameObjectId == preferredId)
                preferred = o;

            var d = Vector3.DistanceSquared(me, o.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = o;
            }
        }

        return preferred ?? best;
    }

    public bool EngageMonsterNoteTarget(bool fateBound)
    {
        var best = FindNearestMonsterNoteTarget(fateBound);
        if (best == null)
            return false;
        _provider.SetTarget(best);
        return true;
    }

    // Acquires the kill target (note mob or by name/dataId), sets it as the game
    // target, and reports its world position and distance so the caller can decide
    // whether to engage (in range) or close the gap first. Returns false when no
    // valid target exists in the loaded object table.
    // preferredId: the mob to stay committed to for the approach (returned regardless of distance
    // while still valid); avoidId: a mob to skip (judged unreachable). acquiredId returns the mob we
    // actually locked onto, so the caller can carry the commitment forward. See FindNearestEnemy.
    public unsafe bool TryAcquireKillTarget(
        bool useNote, string? name, uint dataId, bool fateBound, bool allowFateNote,
        ulong preferredId, ulong avoidId,
        out Vector3 position, out float distance, out ulong acquiredId, out ushort targetFateId)
    {
        position = default;
        distance = float.MaxValue;
        acquiredId = 0;
        targetFateId = 0;

        var t = useNote
            ? FindNearestMonsterNoteTarget(fateBound, allowFateNote, preferredId, avoidId)
            : FindNearestEnemy(name, dataId, fateBound, preferredId, avoidId);
        if (t == null)
            return false;

        _provider.SetTarget(t);
        position = t.Position;
        distance = Vector3.Distance(_provider.PlayerPosition, position);
        acquiredId = t.GameObjectId;
        // The FATE the acquired mob belongs to (0 = not a FATE mob). Lets the caller level-sync when it
        // engages a FATE-spawned note mob, without which RSR would hard-target it but never cast.
        targetFateId = MobFateId(t);
        return true;
    }

    private static bool MatchesIdentity(IGameObject o, string? name, uint dataId)
    {
        if (dataId != 0)
            return o.BaseId == dataId;
        if (!string.IsNullOrEmpty(name))
            return string.Equals(o.Name.TextValue, name, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // Attackable = an alive, targetable ENEMY battle NPC. The BattleNpcKind == Combatant
    // check (Combatant is the enemy sub-kind) excludes the player's own chocobo companion
    // (Buddy) and pets (Pet), which are targetable, alive BattleNpcs too: without it
    // EngageNearestHostile locks onto the (nearest) chocobo once the real enemies are dead,
    // so the character sat "locked onto the chocobo" during a leve/FATE. Hostility beyond
    // kind is still enforced downstream (monster-note targets are validated by
    // RelicNote.IsMonsterNoteTarget, FATE/leve targeting is bounded to the ring/leve, and RSR
    // will not attack non-hostile targets).
    private static bool IsAttackable(IGameObject o)
        => o is IBattleNpc { IsTargetable: true, BattleNpcKind: BattleNpcSubKind.Combatant } npc
           && npc.CurrentHp > 0;

    // FATE membership: the object is within the currently active FATE's ring. Uses
    // FateManager's current FATE (center + radius) rather than a per-object flag,
    // which is sufficient for "only fight FATE mobs" while standing in the FATE.
    private static unsafe bool IsInFate(IGameObject o)
    {
        var fm = FateManager.Instance();
        if (fm == null)
            return false;
        var fate = fm->CurrentFate;
        if (fate == null)
            return false;
        var radius = fate->Radius;
        return Vector3.DistanceSquared(o.Position, fate->Location) <= radius * radius;
    }

    // The FATE this object belongs to, or 0 when it is not a FATE mob. Read straight off the game
    // object's client struct (GameObject.FateId, a ushort at +248) -- the exact field RSR reads
    // (Struct()->FateId) for its per-target "FateID" and its FATE-target filtering. This is precise
    // per-object membership: no FATE-ring geometry, and it does not depend on the player standing in
    // the FATE (so a North Shroud watchwolf FATE is caught before we ever enter its ring).
    private static unsafe ushort MobFateId(IGameObject o)
        => o.Address == nint.Zero ? (ushort)0 : ((CSCharacter*)o.Address)->GameObject.FateId;

    // A leve objective enemy, identified by the game's own event linkage rather than by aggression:
    // the object's EventId.ContentId is a leve director when one owns it. This is the aggro-INDEPENDENT
    // signal we need -- ARR leve enemies are far below a high-level player, so they never aggro and no
    // combat/hostility check would find them. (The leve analogue of the FATE FateId check.)
    //
    // BOTH director types must be matched: BattleLeveDirector (0x8001) owns BATTLECRAFT leve enemies,
    // and CompanyLeveDirector (0x8007) owns GRAND COMPANY leve enemies. Matching only Battle silently
    // returned null for every GC leve (Interception / Protection / Summon / Penetration), so their
    // fight loop found no objective, held at the anchor, and timed out.
    private static unsafe bool IsLeveObjective(IGameObject o)
    {
        if (o.Address == nint.Zero)
            return false;
        var content = ((CSCharacter*)o.Address)->GameObject.EventId.ContentId;
        return content is EventHandlerContent.BattleLeveDirector or EventHandlerContent.CompanyLeveDirector;
    }

    // The nearest attackable battlecraft-leve OBJECTIVE enemy, or null when none are loaded. Used by
    // the leve fight to acquire the leve mobs directly (they do not aggro, so RSR/aggression cannot
    // find them); the caller hard-targets it and pulls it in RSR Manual like a neutral relic mob.
    //
    // excludeName: skip an objective with this exact name (case-insensitive). Used by item-lure leves
    // so the "farm the roaming mobs" fallback never attacks the prime-location object (the "balor's
    // bell") -- which is a lure target you USE the key item on, not one you kill -- if that object
    // happens to read as an attackable leve objective too.
    public IGameObject? FindNearestLeveObjective(string? excludeName = null)
    {
        var me = _provider.PlayerPosition;
        return _provider.Objects
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc)
            .Where(IsAttackable)
            .Where(IsLeveObjective)
            // HOSTILE only. Some battlecraft leves (e.g. "Go Home to Mama" -- "secure the wreck")
            // spawn FRIENDLY allied combatants that are ALSO linked to the leve director and pass
            // IsAttackable (Combatant sub-kind, targetable, alive). Without this the nearest "leve
            // objective" could be an ally, which the run then hard-targets while RSR Manual has
            // nothing hostile to attack -- the reported "hard-targets the friendly defenses, does
            // not attack the enemies". The nameplate-colour check keeps only attackable-enemy
            // objects (yellow/red/orange); the unengaged, non-aggroing leve enemies read as yellow
            // and pass, while friendly (green) allies are dropped.
            .Where(o => o.IsHostile())
            .Where(o => string.IsNullOrEmpty(excludeName)
                        || !string.Equals(o.Name.TextValue, excludeName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.DistanceSquared(me, o.Position))
            .FirstOrDefault();
    }
}
