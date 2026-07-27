# Feature request (draft): IPC to drive the Fate Tool Kit grind

**Target:** Croizat's Bundle of Tweaks (CBT) — https://github.com/Jaksuhn/ffxiv-bundleoftweaks
**From:** Relicable (an ARR Zodiac relic-weapon automation plugin)
**Status:** draft for a later issue + PR

## What I'm building

Relicable automates the ARR Zodiac relic line. For the **Atma** stage I'd like to delegate the
FATE farming to CBT's **Fate Tool Kit**, which already has a purpose-built **"Atma (Zodiac)"**
grind mode (all 12 atma zones + item targets, gated on a Zenith weapon). CBT does this far better
than a bespoke farm, so delegating is the right call.

## The gap

CBT's current IPC provider (`ComplexTweaks.IPC.Provider`) only exposes:

```csharp
[EzIPC] public bool IsTweakEnabled(string className);
[EzIPC] public void SetTweakState(string className, bool state);
```

That lets an external plugin *enable* the Fate Tool Kit tweak, but everything needed to actually
run a specific grind is UI-only (`internal`) on `FateToolKit`:

- **Mode selection** — `SelectedModeId` (the grind mode's `DisplayName`) is `internal set`, changed
  only from `FateToolKitWindow`. There is no command or IPC to pick `"Atma (Zodiac)"`.
- **Start / stop** — `Running` / `RunUntil(int)` / `ToggleRunning()` are driven by the window or the
  `/dwd` command (`/dwd run <count>`, `/dwd stop`). `/dwd` requires the tweak enabled, and `run`
  needs a count; there is no "run until the selected mode completes".
- **Progress / state** — `Running`, `CompletedCount`, `RemainingUntilCompleted`,
  `RelicsCompletedForStep` are all readable internally but not exposed.

So today an external plugin can only: enable the tweak (IPC) and fire `/dwd run <count>` (chat) —
which grinds *whatever mode the user last picked in the window*. To farm atmas the user still has
to open CBT and select `"Atma (Zodiac)"` by hand, and the caller can't tell whether a run is active
or how far along it is.

## Proposed IPC (additive, matches the existing EzIPC provider style)

```csharp
// Modes
[EzIPC] public List<string> FateToolKit_GetModes();              // mode DisplayNames
[EzIPC] public string       FateToolKit_GetSelectedMode();
[EzIPC] public bool         FateToolKit_SetSelectedMode(string displayName); // false if unknown

// Run control
[EzIPC] public bool FateToolKit_Start(int runUntilCount);        // <=0 => run until mode.IsComplete
[EzIPC] public void FateToolKit_Stop();
[EzIPC] public bool FateToolKit_IsRunning();

// Progress (optional but ideal for hand-off)
[EzIPC] public int  FateToolKit_GetCompletedCount();
[EzIPC] public int  FateToolKit_GetRemainingUntilCompleted();    // -1 if no count target
```

These map 1:1 onto members that already exist on `FateToolKit`
(`GetCurrentMode()`/`FateGrindModes.All`, `SelectedModeId`, `Running`, `RunUntil`, `CompletedCount`,
`RemainingUntilCompleted`), so the provider is a thin forwarding layer. It's fully backward
compatible — purely additive, no behaviour change to the tweak or the window.

With `SetSelectedMode("Atma (Zodiac)")` + `Start(0)` + `IsRunning()`, a caller like Relicable can
drive the atma farm end-to-end with zero manual setup, and stop cleanly when its own completion
signal fires (all 12 atmas held / the Zodiac weapon forged).

## Offer

Happy to open the issue and submit the PR (the provider additions + wiring) if this is welcome.
If you'd prefer a narrower surface (say just `SetSelectedMode` + `Start`/`Stop`/`IsRunning`), that
alone removes the manual mode-pick, which is the main friction.
