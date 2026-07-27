# RelicBurstRotations — implementation spec for the ten job rotations

Target: **RSR 7.5.1.17**. Scenario: **solo, unsynced, level 100, ARR relic weapon equipped,
Bowl of Embers (Extreme) (TerritoryType 295), farming Nexus light. Minimum wall-clock kill time.**

Read this whole file before writing anything. `Rotations/BLM_IfritEX.cs` is the canonical
reference implementation — copy its shape.

---

## HARD RULES

1. **Do NOT run `dotnet build`, `dotnet restore`, or any other build command.** A later serial
   phase compiles everything once. Concurrent builds corrupt `obj/`.
2. **Do NOT edit `RelicBurstRotations.csproj`, `nuget.config`, `IfritExBurst.cs`, `TEMPLATE.md`,
   or any other agent's rotation file.** If you believe the csproj or the shared helper is
   missing something you need, say so in your report — do not change it.
3. **Write exactly one file:** `Rotations/<ABBR>_IfritEX.cs`.
4. **Never invent an API name.** If a member is not listed in this file and not in
   `Rotations/BLM_IfritEX.cs`, confirm it first against the XML documentation shipped with your
   installed Rotation Solver Reborn:
   `%AppData%\XIVLauncher\installedPlugins\RotationSolver\<version>\RotationSolver.Basic.xml`
   (~7 MB — grep it, never read it whole). Absence from that XML is not proof of absence
   (undocumented members exist), but presence is proof.

---

## File / naming convention

| Job | File | Class | Base class | `[Rotation]` name |
|---|---|---|---|---|
| PLD | `Rotations/PLD_IfritEX.cs` | `PLD_IfritEX` | `PaladinRotation`    | `"Ifrit EX Burst (PLD)"` |
| WAR | `Rotations/WAR_IfritEX.cs` | `WAR_IfritEX` | `WarriorRotation`    | `"Ifrit EX Burst (WAR)"` |
| MNK | `Rotations/MNK_IfritEX.cs` | `MNK_IfritEX` | `MonkRotation`       | `"Ifrit EX Burst (MNK)"` |
| DRG | `Rotations/DRG_IfritEX.cs` | `DRG_IfritEX` | `DragoonRotation`    | `"Ifrit EX Burst (DRG)"` |
| NIN | `Rotations/NIN_IfritEX.cs` | `NIN_IfritEX` | `NinjaRotation`      | `"Ifrit EX Burst (NIN)"` |
| BRD | `Rotations/BRD_IfritEX.cs` | `BRD_IfritEX` | `BardRotation`       | `"Ifrit EX Burst (BRD)"` |
| WHM | `Rotations/WHM_IfritEX.cs` | `WHM_IfritEX` | `WhiteMageRotation`  | `"Ifrit EX Burst (WHM)"` |
| BLM | `Rotations/BLM_IfritEX.cs` | `BLM_IfritEX` | `BlackMageRotation`  | `"Ifrit EX Burst (BLM)"` (**done — do not touch**) |
| SMN | `Rotations/SMN_IfritEX.cs` | `SMN_IfritEX` | `SummonerRotation`   | `"Ifrit EX Burst (SMN)"` |
| SCH | `Rotations/SCH_IfritEX.cs` | `SCH_IfritEX` | `ScholarRotation`    | `"Ifrit EX Burst (SCH)"` |

All ten base classes live in `RotationSolver.Basic.Rotations.Basic` (already a global using).

**Namespace of every rotation file: `RelicBurstRotations.Rotations`** (file-scoped).

Resulting `Type.FullName` (what goes into RSR's `_rotationChoiceDict`):
`RelicBurstRotations.Rotations.<ABBR>_IfritEX`.

---

## Required file skeleton

No `using` directives — the csproj supplies every global using you need.

```csharp
namespace RelicBurstRotations.Rotations;

[Rotation("Ifrit EX Burst (XXX)", CombatType.PvE, GameVersion = "7.5",
    Description = "Solo unsynced Bowl of Embers (Extreme) relic-light farm. Frontloads everything.")]
[SourceCode(Path = "Rotations/XXX_IfritEX.cs")]
[ExtraRotation]
public sealed class XXX_IfritEX : XxxRotation
{
    #region Config Options
    #endregion

    #region Tracking Properties
    public override void DisplayRotationStatus() { }
    #endregion

    #region Extra Methods
    #endregion

    #region oGCD Logic
    #endregion

    #region GCD Logic
    #endregion
}
```

Hard requirements enforced by RSR's loader — violating any of these makes the rotation silently
vanish from the picker:

* `public sealed class`, **public parameterless constructor** (i.e. declare no constructor at all).
* **Never do any game / `Svc` / `Player` work in a constructor or a field initializer that reads
  game state.** Types are instantiated at load time off the game thread.
* `[Rotation(name, CombatType.PvE)]` is mandatory. `GameVersion` is a free-form string, never parsed.
* `[SourceCode]` and `[ExtraRotation]` are cosmetic but keep them for consistency.

---

## Which overrides to implement

Implement **only these four**, in this order. Everything else stays inherited.

```csharp
public override void DisplayRotationStatus()                                  // debug readout
protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)   // tincture only
protected override bool AttackAbility(IAction nextGCD, out IAction? act)      // burst oGCDs
protected override bool GeneralGCD(out IAction? act)                          // the damage loop
```

Optionally also:

```csharp
public override bool CanHealSingleSpell => false;   // solo: healing GCDs are pure DPS loss
public override bool CanHealAreaSpell   => false;   // MANDATORY on WHM and SCH
```

**Do NOT override** `CountDownAction` — there is no `/countdown` when you solo-pull a duty, so it
never runs. Put pull-time logic in `EmergencyAbility` gated on `IfritExBurst.InIfritOpener(...)`.

**Do NOT override** `HealSingleGCD`, `HealAreaGCD`, `DefenseSingleAbility`, `DefenseAreaAbility`,
`RaiseGCD`, `DispelGCD`, `ProvokeGCD`, or the interrupt stages. Leave RSR's defaults alone.

### Return semantics (get this wrong and RSR stalls)

* `return true` means "press `act` this frame" — **`act` must be non-null**. Always let the
  `CanUse(out act)` call itself be what assigns `act`; never `return true` from a branch that did
  not just succeed a `CanUse`.
* `return false` means "declined, keep going down the chain".
* **Every override must end with `return base.Xxx(...);`**, never a bare `return false;`.
  Omitting the base call kills role actions, mitigation, Sprint, True North, and more.

### Stage gotchas

* `AttackAbility` is skipped entirely when `HasHostilesInRange` is false (3 y for melee/tank,
  25 y for ranged/healer). An oGCD that must fire out of range belongs in `EmergencyAbility`.
* `GeneralGCD` runs last in the GCD chain and is where the whole damage rotation lives.

---

## Helpers you must use — `IfritExBurst` (file `IfritExBurst.cs`, namespace `RelicBurstRotations`)

`RelicBurstRotations.Rotations` sees `RelicBurstRotations` types without a using directive.
Exact shipped surface:

```csharp
public const  uint      IfritExBurst.BowlOfEmbersExtremeTerritoryId      // = 295
public const  ushort    IfritExBurst.BowlOfEmbersExtremeTerritoryIdU16   // = 295, for IsInTerritory
public static bool      IfritExBurst.InIfritEx                           // in territory 295
public static bool      IfritExBurst.InBurst                             // InIfritEx && InCombat
public static float     IfritExBurst.CombatSeconds                       // seconds since the pull, 0 if OOC
public const  float     IfritExBurst.DefaultOpenerWindowSeconds          // = 20f
public static bool      IfritExBurst.InOpenerWindow                      // CombatSeconds <= 20
public static bool      IfritExBurst.InOpenerWindowOf(float seconds)
public static bool      IfritExBurst.InIfritOpener(float seconds = 20f)  // InIfritEx && in opener
public static readonly NPCName[] IfritExBurst.InfernalNailNames
public static bool      IfritExBurst.IsInfernalNail(IBattleChara? chara)
public static bool      IfritExBurst.MultipleHostilesPresent             // InIfritEx && >1 hostile
public static bool      IfritExBurst.ShouldKillNails(IBattleChara? currentHostileTarget)
public static TargetType IfritExBurst.NailFirstTargeting(IBattleChara? currentHostileTarget)
```

`ShouldKillNails` / `NailFirstTargeting` take a parameter because `HostileTarget` is
`protected static` on `CustomRotation` and unreachable from a non-derived class. **From inside
your rotation, always pass `HostileTarget`.**

### The standard usage pattern

```csharp
    #region Extra Methods
    private TargetType KillOrder =>
        NailPriority ? IfritExBurst.NailFirstTargeting(HostileTarget) : default;
    #endregion
```

then pass `targetOverride: KillOrder` to **every damaging `CanUse` in `GeneralGCD`**. When no nails
are up this returns `default` (== `TargetType.Big`), which is exactly RSR's normal behaviour, so it
is a no-op outside the nail phase.

> **Fight rule:** while Infernal Nails are alive, damaging Ifrit is *strictly harmful* — since
> patch 4.56 he becomes temporarily invulnerable and the fight can lock into an unwinnable Hellfire
> loop. Never write a "burn Ifrit during nails to save time" heuristic.

Do **not** apply `targetOverride:` to actions whose own `Setting.TargetType` matters
(interrupts, provokes, tank-stance, self-targeted buffs) — the override *replaces* the action's
declared targeting.

---

## Rotation design rules for this specific scenario

The kill is expected to be **10-20 seconds**. Design accordingly.

1. **The rotation IS the opener.** Every 2-minute cooldown fires exactly once. Do not write
   cooldown-alignment / drift / hold logic (`Cooldown.ElapsedAfter(60)` and friends). Do not gate
   burst on `IsBurst`. Gate on `IfritExBurst.InBurst` and fire.
2. **Pass `skipTTKCheck: true` on every burst oGCD.** Short kills make RSR's time-to-kill gate
   reject exactly the cooldowns you want.
3. **Single-target only.** The nails are spread around the arena ring; AoE nets ~2 targets at best
   and travel time dominates. Do not build AoE burst windows. (An AoE line that `CanUse` naturally
   accepts is fine; don't design for it.)
4. **Optimize for the biggest single hit**, not for theoretical sustained DPS. The documented skip
   condition is a finishing blow worth 20-30% of Ifrit's HP. Where a job can choose between a
   smoother sequence and a bigger nuke, take the nuke.
5. **Melee uptime is free** — Ifrit is stationary, no untargetable windows, nothing happens in the
   first ~15 s. Never reposition, never drift for positionals.
6. **No healing, ever.** Set `CanHealSingleSpell`/`CanHealAreaSpell` to `false`. This is mandatory
   on WHM and SCH, whose defaults turn healing GCDs on when no other healer is alive — which, solo,
   is always.
7. **Tincture:** put `UseBurstMedicine(out act)` in `EmergencyAbility` behind a config flag that
   defaults to `false`, gated on `IfritExBurst.InIfritOpener(...)`. Note RSR's own tincture setting
   defaults to "high-end duty only" and territory 295 is **not** high-end, so it will often
   silently no-op. That is expected; do not work around it.
8. **Do not write level gates.** The player is level 100 unsynced. `CanUse` already checks level.

---

## `CanUse` reference (exact 7.5.1.17 signature)

```csharp
bool CanUse(
    out IAction act,
    bool skipStatusProvideCheck    = false,
    bool skipStatusNeed            = false,
    bool skipTargetStatusNeedCheck = false,
    bool skipComboCheck            = false,
    bool skipCastingCheck          = false,
    bool usedUp                    = false,
    bool skipAoeCheck              = false,
    bool skipTTKCheck              = false,
    byte gcdCountForAbility        = 0,
    bool checkActionManager        = false,
    TargetType targetOverride      = default);
```

Always use **named arguments**. `CanUseOption` does not exist as a parameter in this version.

`TargetType` lives in `RotationSolver.Basic.Actions` (not `.Data`). Members:
`Big, Small, HighHP, LowHP, HighMaxHP, LowMaxHP, Interrupt, Provoke, Death, Dispel, Move,
FriendMove, BeAttacked, Heal, Tank, Melee, Range, Physical, Magical, Self, DancePartner,
MimicryTarget, TheBalance, TheSpear, Kardia, Deployment, PhantomBell, PhantomRespite, DarkCannon,
ShockCannon, Farthest, Nearest, PvPHealers, PvPTanks, PvPDPS, HighHPPercent, LowHPPercent,
Tankbuster`.

---

## `[RotationConfig]` reference

```csharp
[RotationConfig(CombatType.PvE, Name = "Prioritise Infernal Nails over Ifrit")]
public bool NailPriority { get; set; } = true;

[Range(0, 60, ConfigUnitType.Seconds, 1)]
[RotationConfig(CombatType.PvE, Name = "Opener window length (seconds)")]
public float OpenerWindow { get; set; } = IfritExBurst.DefaultOpenerWindowSeconds;
```

* Must be a **property with both a getter and a setter**, with a default assigned by initializer.
* `[Range]`'s only public constructor is
  `(float min, float max, ConfigUnitType unit, float speed = 0.005f)` — the 4th argument is the
  UI drag speed and is optional, so `[Range(0, 1, ConfigUnitType.Percent)]` is also valid.
* `ConfigUnitType` members: `None, Seconds, Degree, Yalms, Percent, Pixels`.
* `[Range]` goes **above** `[RotationConfig]`.
* Enum configs need `[Description("...")]` on each member (`System.ComponentModel` is a global using).

---

## Members that DO NOT exist in 7.5.1.17 (do not copy them from older RSR code)

`[Api(n)]` · `DisplayStatus()` (it is `DisplayRotationStatus()`) · `public override string GameVersion`
· `public override string RotationName` · `CanUseOption` as a `CanUse` argument ·
`[RotationDesc(DescType.X)]` (that constructor is internal; only `[RotationDesc(ActionID...)]`,
`[RotationDesc(string, ActionID...)]`, and bare `[RotationDesc]` are public) · `ImGuiNET`
(it is `Dalamud.Bindings.ImGui`).

## Members that exist but are `internal` — unusable from this assembly

`DataCenter` (whole class) · `Service` / `Service.Config` · `OtherConfiguration` ·
`ObjectHelper.IsAttackable()` · `ObjectHelper.IsDummy()` · `ObjectHelper.GetTTK()` ·
`IBaseAction.TargetOverride` / `.ForceEnable` / `.IgnoreClipping`.

## `CustomRotation` statics — accessibility matters

**`public static` (usable anywhere, including `IfritExBurst`):**
`IsInTerritory(ushort)`, `InCombat`, `CombatTime`, `NumberOfHostilesInRange`,
`NumberOfHostilesInMaxRange`, `NumberOfAllHostilesInRange`, `NumberOfAllHostilesInMaxRange`,
`NumberOfHostilesInRangeOf(float)`, `HasHostilesInRange`, `HasHostilesInMaxRange`, `IsBurst`,
`MergedStatus`, `AverageTTK`, `CountDownAhead`, `IsInDuty`, `IsInHighEndDuty`,
`IsLastGCD(...)`, `IsLastAction(...)`, `UseBurstMedicine(out act, bool)`.

**`protected static` (usable only inside your rotation class, never from `IfritExBurst`):**
`HostileTarget`, `CurrentTarget`, `Target`, `Player`, `AllHostileTargets`, `AllTargets`,
`CombatElapsedLess(float)`, `CombatElapsedLessGCD(int)`.

**`public static` on `ObjectHelper` (extension methods on `IBattleChara`):**
`IsBossFromIcon`, `IsBossFromTTK`, `GetHealthRatio`, `GetEffectiveHp`, `GetEffectiveHpPercent`,
`DistanceToPlayer`, `IsNamed(NPCName)`, `IsDying`, `FindEnemyPositional`, `GetObjectShield`.

**`public static` on `StatusHelper`:** `PlayerHasStatus(bool, params StatusID[])`,
`PlayerStatusStack`, `PlayerStatusTime`, `PlayerWillStatusEnd`, `PlayerWillStatusEndGCD`,
`PlayerHasApplyStatus`.

---

## Checklist before you report done

- [ ] Exactly one new file, at `Rotations/<ABBR>_IfritEX.cs`.
- [ ] Namespace `RelicBurstRotations.Rotations`; class `public sealed`; no constructor.
- [ ] `[Rotation("Ifrit EX Burst (<ABBR>)", CombatType.PvE, GameVersion = "7.5", Description = ...)]`.
- [ ] Correct base class from the table above.
- [ ] Every override ends with `return base.Xxx(...)`.
- [ ] Every `return true` is immediately preceded by a `CanUse(out act)` that succeeded.
- [ ] `targetOverride: KillOrder` on every damaging `CanUse` in `GeneralGCD`.
- [ ] `skipTTKCheck: true` on burst oGCDs.
- [ ] `CanHealSingleSpell` / `CanHealAreaSpell` overridden to `false`.
- [ ] No `dotnet build`. No edits to any file other than your own.
