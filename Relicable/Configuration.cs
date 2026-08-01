using Dalamud.Configuration;
using Newtonsoft.Json;
using Relicable.Model;

namespace Relicable;

// Persisted per character. Mirrors Questionable's configuration surface: a combat
// backend selector, companion toggles, stage preferences, and stop conditions.
// Progress is never stored here; it is always re-derived from game memory on
// start so a stale cache cannot trigger an incorrect action.
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public enum CombatBackend
    {
        None,
        RotationSolverReborn,
        // Persisted as the int 2 (formerly named BossMod, when the integration targeted a
        // different BossMod fork); now drives BossMod Reborn, so saved configs carry over.
        BossModReborn,
        // Wrath Combo (PunishXIV). Lease-based: Relicable registers for control, drives
        // Wrath's Auto-Rotation, and hands control back when it unloads.
        WrathCombo,
    }

    // Mirror of Wrath Combo's DPSRotationMode BY VALUE (sent as the int). Used for how
    // Wrath picks targets during FATE / open-world auto combat. Manual means "only ever
    // act on the player's hard target", which is what the neutral relic-note grind needs
    // and is therefore forced there regardless of this setting.
    public enum WrathDpsTargeting
    {
        Manual = 0,
        HighestMaxHp = 1,
        LowestMaxHp = 2,
        HighestCurrentHp = 3,
        LowestCurrentHp = 4,
        TankTarget = 5,
        Nearest = 6,
        Furthest = 7,
    }

    // ---- Wrath Combo options (only used when Backend == WrathCombo) ----

    // Let Relicable configure Wrath's Auto-Rotation settings while it runs (in-combat
    // gating, DPS targeting mode, FATE priority/bypass). ON by default, because without
    // it Wrath will not open on a neutral relic-note mob and the grind stalls.
    //
    // Turn it OFF to have Relicable only switch Auto-Rotation on and off and otherwise
    // leave your Wrath configuration exactly as you tuned it. Wrath shows which settings
    // are IPC-controlled in its own window, and they are released when Relicable unloads.
    public bool WrathManageAutoRotationConfig { get; set; } = true;

    // How Wrath chooses targets during FATE combat. HighestMaxHp favours the FATE boss;
    // Nearest is the steadier pick for add-heavy FATEs. Setting this to Manual makes
    // Relicable keep hard-targeting each mob itself instead of handing targeting over.
    public WrathDpsTargeting WrathFateTargeting { get; set; } = WrathDpsTargeting.HighestMaxHp;

    // Mirror of RSR's TargetHostileType by NAME (the value sent to RSR's "HostileType" setting
    // is this enum member's name), for the RSR-FATE hostile-type selector below. Only the
    // members meaningful to a solo FATE grind are surfaced.
    public enum FateHostility
    {
        // All valid FATE mobs when solo, else previously engaged (the recommended FATE default).
        AllTargetsWhenSolo,
        // Every target in ability range (RSR's tank/AutoDuty setting).
        AllTargetsCanAttack,
        // Only already-engaged mobs (grouped play -- RSR waits for aggro before attacking).
        TargetsHaveTarget,
    }

    // Combat. Defaults to BossMod Reborn so a FRESH install does not require Rotation Solver
    // Reborn: BossMod Reborn already drives AoE avoidance by default
    // (UseBossModRebornAvoidance), and under this backend it also drives the rotation via
    // Relicable's shipped preset, so the only combat plugin a new user needs is BossMod
    // Reborn. Switch to RotationSolverReborn to use RSR instead (RSR then becomes the
    // required combat plugin). NOTE: an existing config that already saved a Backend value
    // keeps it -- change it in /relic config > Settings > Combat backend.
    public CombatBackend Backend { get; set; } = CombatBackend.BossModReborn;

    // ---- RSR FATE options (only used when Backend == RotationSolverReborn) ----
    // Under RSR, Relicable hands FATE targeting to RSR itself: it navigates into the ring,
    // level-syncs, and grounds, then puts RSR in Auto mode with these settings so RSR auto-
    // selects and attacks FATE mobs (rather than Relicable hard-targeting each one). These
    // mirror RSR's own FATE-relevant settings and are applied via its Settings command / IPC
    // when a FATE step engages. They do nothing under the BossMod Reborn / None backends.

    // RSR's hostile-target type while in a FATE (RSR "HostileType"). AllTargetsWhenSolo (the
    // default) makes solo RSR attack every valid FATE mob; TargetsHaveTarget restricts it to
    // already-engaged mobs (for grouped play). Sent as the enum NAME, so the values match
    // RSR's TargetHostileType exactly.
    public FateHostility RsrFateHostileType { get; set; } = FateHostility.AllTargetsWhenSolo;

    // RSR "IgnoreNonFateInFate": while inside a FATE only FATE mobs are considered (and FATE
    // mobs are ignored when NOT in a FATE). On by default -- keeps RSR from wandering onto an
    // unrelated overworld mob mid-FATE. This is RSR's own default too.
    public bool RsrFateIgnoreNonFateTargets { get; set; } = true;

    // RSR "TargetFatePriority": prioritise FATE mobs when choosing a target. On by default.
    public bool RsrFateTargetFatePriority { get; set; } = true;

    // RSR "TargetFreely": let RSR grab the closest targetable FATE mob when it has no target
    // set. Applied for the FATE only via RSR's temporary EnableTargetFreelyOverride IPC (never
    // the persistent setting), so it cannot bleed into the neutral relic-note grind, which
    // relies on Relicable tunnelling one hard target in Manual mode. On by default; needs a
    // recent RSR (older builds without the override IPC simply skip it).
    public bool RsrFateTargetFreely { get; set; } = true;

    // ---- RSR Ifrit EX burst rotation (Bowl of Embers (Extreme), TerritoryType 295) ----
    // On entering the farm duty, point RSR at that job's purpose-built "Ifrit EX Burst (<ABBR>)"
    // rotation (RelicBurstRotations), and put your own choice back on the way out. Only the ten
    // ARR Zodiac relic jobs have one; every other job (and every base class) is left alone.
    //
    // OFF by default, and it must stay that way. RSR 7.5.1.17 does NOT load rotations from disk:
    // RotationUpdater.LoadBuiltInRotations() is
    //     List<Assembly> assemblies = [typeof(RotationUpdater).Assembly];
    // and there is no other loader, which is why the shipped rotations live in namespaces INSIDE
    // RotationSolver.dll (RotationSolver.RebornRotations.*, RotationSolver.ExtraRotations.*) and
    // why pluginConfigs\RotationSolver\Rotations\*.dll is never read. On the official plugin the
    // "Ifrit EX Burst" rotations therefore cannot exist, and this setting can only ever be a no-op.
    // It is useful only to someone running a RotationSolver build with RelicBurstRotations compiled
    // in -- see the deployment note at the top of RelicBurstRotations\IfritExBurst.cs. Turning it on
    // without that build changes nothing and reports why in /relic config.
    public bool AutoSwapIfritBurstRotation { get; set; } = false;

    // Pin RSR's hostile targeting to LowMaxHP while inside the Bowl of Embers (Extreme), so Ifrit's
    // Infernal Nails (a tiny fraction of his max HP) are targeted before Ifrit himself. Unlike the
    // rotation swap above, this needs NO custom RotationSolver build -- it drives
    // DataCenter.TargetingTypeOverride, which is the field RSR's own hostile picker
    // (ActionTargetInfo.FindHostileRaw) sorts on. Without it RSR's default arm sorts by hitbox
    // radius descending, i.e. Ifrit forever, and a run that reaches the nail phase can never finish
    // it: Ifrit is invulnerable until every nail dies. In-memory only; RSR never persists it.
    // ON by default: it is a strict no-op before nails spawn (Ifrit is then the only hostile), and
    // it is what makes the "clear through the nails" fallback in EnterDutyExecutor winnable.
    public bool PrioritiseIfritNailTargeting { get; set; } = true;

    // Crash-safety breadcrumb for the swap above. Relicable never writes the override into RSR's
    // OWN config file, but RSR saves its config unconditionally when the game exits, so an exit
    // (or a crash) while the override is live could otherwise leave it baked in permanently.
    // These three record what to put back, are written the instant the override is applied, and
    // are replayed on the next plugin load. Managed by RsrRotationOverride; do not edit by hand.
    public bool RsrRotationOverrideActive { get; set; }
    public uint RsrRotationOverrideJobId { get; set; }
    public string RsrRotationOverridePrevious { get; set; } = string.Empty;

    // The RotationSolver assembly version the override was taken against. Dalamud installs each
    // plugin update into a NEW version-stamped directory, so an RSR update silently replaces a
    // custom build with the official one and the burst rotations vanish. Recording the version lets
    // the swap notice that and warn again instead of staying quiet about a feature that stopped
    // working. Managed by RsrRotationOverride; do not edit by hand.
    public string RsrRotationOverrideRsrVersion { get; set; } = string.Empty;

    // ---- Stage selection ----
    // Auto: the controller works the lowest incomplete stage (original behaviour).
    // Manual: it pins work to ManualStage, so a farmable stage that was already
    // passed (Atma, Novus materia, Nexus light, Zeta) can be revisited at will.
    public StageSelectionMode StageMode { get; set; } = StageSelectionMode.Auto;

    // The user-inserted stage used when StageMode is Manual.
    public RelicStage ManualStage { get; set; } = RelicStage.Atma;

    // ---- Atma farm backend ----
    // Who runs the Atma FATE farm. Builtin = Relicable's own Atma objectives (currently a
    // single-zone stub). CbtFateToolKit = delegate to Croizat's Bundle of Tweaks (CBT) "Fate
    // Tool Kit", which ships a self-contained "Atma (Zodiac)" grind mode covering all 12 atma
    // zones and requiring a Zenith weapon equipped. CBT exposes no IPC to SELECT that mode, so
    // with this set you pick "Atma (Zodiac)" once in CBT's Fate Tool Kit window; Relicable then
    // starts/stops the grind (CBT's /dwd command) and advances once the Zodiac weapon is forged.
    public enum AtmaFarmBackend
    {
        Builtin,
        CbtFateToolKit,
    }

    public AtmaFarmBackend AtmaBackend { get; set; } = AtmaFarmBackend.Builtin;

    // Built-in Atma farm: how many of a zone's atma to hold before moving on to the next zone.
    // Each of the twelve zones drops only its OWN atma, so the farm works one zone at a time and
    // moves on when that zone's atma is in the bag. The Zenith -> Atma enhancement consumes ONE of
    // each, so 1 (the default) is all a single relic needs; raise it to bank spare sets for repeat
    // relics in the same trip, since every extra atma of a zone must be farmed IN that zone.
    // Applied to the Atma-stage ItemCount completion check, so the whole farm (and the Jalzahn
    // enhancement that waits on it) uses the same target. Clamped to at least 1.
    public int AtmaPerZone { get; set; } = 1;

    // ---- Animus (Trials of the Braves / "Books") ----
    // A book FATE only progresses while that specific FATE is up, and ARR FATEs can
    // sit dormant for a long time. The engine visits a book's FATEs in consecutive
    // order (1, 2, 3): on the first pass it only glances (skips an unspawned FATE fast
    // and moves to the next), and on every later pass it waits THIS many seconds at each
    // FATE for it to spawn before rotating to the next. 0 or less disables the rotation
    // (wait indefinitely at the first FATE, the old behaviour).
    public int FateRotateSeconds { get; set; } = 120;

    // Opportunistic FATE grab, in priority order:
    //
    //   1. A book FATE that is up RIGHT NOW in the zone the character is STANDING IN. Taking it
    //      costs no travel at all, and the FATE will not be up later -- teleporting away from a
    //      live FATE to go do ordered work elsewhere is pure loss. This covers the reported
    //      "a FATE was up in my zone and it teleported away anyway": the engine had already
    //      settled on an objective in another zone, and nothing looked at where we actually were.
    //   2. A book FATE up in a zone where we also have enemy (monster) work, so one teleport
    //      covers both.
    //
    // Both only fire when the FATE has more than ~3 minutes left, so there is time to reach and
    // clear it. On by default; turn off for the strict enemies-then-leves-then-dungeons-then-FATEs
    // order regardless of what is live.
    public bool PreferCoLocatedFates { get; set; } = true;

    // How book work is chosen. Auto = every kind, in the authored order. Manual = only the kinds
    // ticked in BookWorkKinds (main window > Book work), which is also where a specific slot can
    // be pushed to the front with "Run next".
    public BookWorkSelectionMode BookWorkMode { get; set; } = BookWorkSelectionMode.Auto;

    // Which book-slot kinds Manual mode is allowed to work. Ignored entirely in Auto.
    // Persisted as an int flag set; All is the starting point so switching to Manual changes
    // nothing until something is unticked.
    public BookWorkKinds BookWorkKinds { get; set; } = BookWorkKinds.All;

    // ---- Teleporting ----
    // Spend an Aetheryte Ticket instead of gil when the destination is expensive. The engine
    // teleports constantly (twelve atma zones, book slots scattered across ARR), so on a long
    // unattended run this is the difference between a rounding error and a real gil drain.
    // Tickets are only spent when the destination's own gil cost is at least
    // AetheryteTicketMinGil, so cheap intra-region hops stay on gil and the ticket stack is
    // saved for the long jumps. Falls back to gil automatically when no ticket is held.
    public bool UseAetheryteTickets { get; set; } = true;

    // The gil cost at or above which a ticket is spent. The game's own per-destination cost is
    // read live from the teleport list, so this compares against the real price including any
    // favoured/free-destination discount. Clamped to at least 1.
    public int AetheryteTicketMinGil { get; set; } = 300;

    // ---- Base relic (A Relic Reborn) ----
    // The job whose base-relic requirements the prerequisite checker reports. None
    // means "auto-detect from the equipped job"; set it to pin a specific job (e.g.
    // to plan Scholar while playing on Summoner, since both share Arcanist).
    public RelicJob BaseRelicJobOverride { get; set; } = RelicJob.None;

    // ---- Novus materia melding ----
    // Which Novus weapon is being melded; selects the stat caps, grade tiers, and
    // success curve used by the route optimizer.
    public NovusWeaponProfile NovusWeapon { get; set; } = NovusWeaponProfile.Standard;

    // Maximum distinct stats the optimizer may spread across (wiki: up to 5; the
    // summary page says 7). Clamped to [2, 7] by the optimizer.
    public int MaxMateriaStats { get; set; } = 5;

    // Auto-withdraw matching materia from retainers before melding (best effort; the
    // native retainer-retrieve step is a live-UI seam with a manual fallback).
    public bool AutoWithdrawFromRetainers { get; set; } = true;

    // LEGACY (single-scroll) infused progress. Superseded by ScrollProgressByScroll, which supports
    // Paladin's TWO Sphere Scrolls (Curtana + Holy Shield). Kept for one-time migration on load:
    // MateriaPlanner.ComputeRoute moves it into ScrollProgressByScroll under the profile's first scroll,
    // then clears it. Do not add new reads of this field.
    public System.Collections.Generic.Dictionary<MateriaType, int> ScrollProgressByStat { get; set; } = new();

    // Points already infused, keyed by the scroll's spec name (MateriaCatalog ScrollSpec.Name: "Novus",
    // "Novus (healer)", "Curtana Novus", "Holy Shield Novus"), then per stat. Paladin has TWO scrolls,
    // so a single per-stat dict conflated them; this stores each scroll's progress separately. The
    // in-game per-stat bar is not readable, so you set these as you infuse (or open each scroll's window
    // to reconcile them live). The route continues each stat from its current grade and skips maxed stats.
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<MateriaType, int>> ScrollProgressByScroll { get; set; } = new();

    // Target number of Alexandrite to farm for the Novus stage. The treasure-map farm
    // runs until you hold this many, and the farm objective re-arms whenever you hold
    // fewer (so you can raise it to farm more). 75 is the amount one Sphere Scroll
    // consumes; raise it to keep a buffer for failed melds.
    public int AlexandriteTarget { get; set; } = 75;

    // Experimental: drive the live materia-meld window to infuse the planned route
    // automatically. Off by default because the meld callback layout cannot be
    // verified outside the game and a wrong confirm could shatter materia. When off,
    // the meld step computes and sources the route but leaves the actual infusing to
    // you. See RelicMeld / DESIGN Appendix J.5.
    public bool EnableAutoMeld { get; set; }

    // ---- Nexus (Light farming) ----
    // The duty AutoDuty farms for Light, by TerritoryType. Default 295 = the Bowl of
    // Embers (Extreme) (Ifrit), the community-standard fast farm (a few-second unsynced
    // clear; ~65 fill 2000 Light worst-case). Point it at another duty to farm there
    // instead -- e.g. whichever currently has the rotating Light bonus active.
    public uint NexusFarmTerritoryType { get; set; } = 295;

    // Upper bound on AutoDuty's loop count for the Light farm. Farming auto-stops the
    // instant the relic's Light reaches 2000 regardless, so this is only a safety cap.
    public int NexusFarmLoops { get; set; } = 65;

    // Enter the farm duty unsynced / unrestricted (level sync and party-size limits
    // off) so it can be soloed at max level. Drives AutoDuty's DutyMode=Trial and
    // Unsynced before each run; required to solo a level-50 Extreme trial.
    public bool NexusFarmUnsynced { get; set; } = true;

    // ---- Zeta (Mahatma farming) ----
    // The duty AutoDuty farms to charge each Mahatma, by TerritoryType. Default 295 = Bowl of
    // Embers (Extreme), trivial to clear unsynced at max level (seconds per run). A Mahatma only
    // gains credit at the LAST BOSS of a CLEARED duty (relic equipped), so the farm duty must be
    // one AutoDuty actually clears end-to-end with its combat/rotation engaged. The AutoDuty
    // DutyMode auto-resolves from content type (dungeon -> Regular, trial -> Trial); a dungeon
    // like 172 (Aurum Vale) also works. NOTE: FATEs are a poor Zeta farm (4 points).
    public uint ZetaFarmTerritoryType { get; set; } = 295;

    // One-time migration marker: moves the old Aurum Vale (172) Zeta-farm default to Bowl of
    // Embers (Extreme) (295). Applied once on load so a deliberate later choice is kept.
    public bool ZetaFarmUpgradedToEmbers { get; set; }

    // Duties per AutoDuty hand-off for the Zeta farm. 1 keeps each hand-off to a single
    // clear (clean re-attach loop); farming auto-stops the instant a Mahatma awakens
    // regardless, so a higher value only batches clears between progress checks.
    public int ZetaFarmLoops { get; set; } = 1;

    // Enter the Zeta farm duty unsynced / unrestricted so it can be soloed at max level.
    public bool ZetaFarmUnsynced { get; set; } = true;

    // ---- Ifrit EX Infernal Nail bail-out (shared by the Light and Mahatma farms) ----
    // Both farms default to the Bowl of Embers (Extreme). If Ifrit's Infernal Nails spawn (his nail
    // phase), he is invulnerable until they are all destroyed -- a long detour on an otherwise
    // few-second unsynced clear. When on (default), the farm abandons the run the instant a nail is
    // seen and lets AutoDuty re-queue a fresh burst attempt. Turn off to fight the nails out instead
    // (e.g. a job/gear that cannot burst Ifrit before the nails and would otherwise abandon every run).
    public bool AbandonOnIfritNails { get; set; } = true;

    // ---- Universalis pricing ----
    public UniversalisScope MarketScope { get; set; } = UniversalisScope.DataCenter;

    // Explicit world/DC/region name to price against. Empty = auto-detect from the
    // logged-in character's home world and data centre.
    public string MarketNameOverride { get; set; } = string.Empty;

    // Persisted materia counts scanned from each retainer's inventory (AutoRetainer's
    // IPC does not expose item-level retainer contents, so they are read from the
    // native retainer UI when open and cached here). See RetainerMateriaCache.
    public RetainerMateriaCache RetainerMateria { get; set; } = new();

    // Base-relic materials (crafting mats, meld materia, vendor consumables) scanned
    // from each retainer's inventory, by the same native-bell scan as RetainerMateria.
    // Lets the base-relic checker report "available from retainers" while offline.
    public RetainerItemCache RetainerBaseRelicItems { get; set; } = new();

    // The twelve Atma items scanned from each retainer's inventory (same native-bell scan),
    // so the Atma tracker can show which atmas sit on a retainer as well as in your bags.
    public RetainerItemCache RetainerAtmas { get; set; } = new();

    // Braves (il125) shopping-list materials scanned from each retainer's inventory, by the
    // same native-bell scan. Lets the Braves planner show a "Retainer" count per row offline
    // and tell you which retainer to open before a fetch. Key-item dungeon drops are never in
    // here -- they cannot be entrusted to a retainer at all.
    public RetainerItemCache RetainerBravesItems { get; set; } = new();

    // Every relic upgrade swaps the weapon for a new item id, which leaves the gear set you use
    // naming a weapon that no longer exists -- so the next "/gearset change" comes up with an empty
    // main hand (and no Holy Shield on a Paladin). When on, Relicable rewrites the ACTIVE gear set
    // after an upgrade so it names the current relic. It only ever does so when the set is for the
    // job you are on and every non-weapon slot already matches, so the write can change nothing but
    // the weapon. Turn off to manage your own sets. See RelicGearsetSync.
    public bool SyncGearsetToLatestRelic { get; set; } = true;

    // Companion toggles
    public bool EnableNavmesh { get; set; } = true;
    public bool AllowFlight { get; set; } = true;
    public bool UseMount { get; set; } = true;

    // Let the relic-note MONSTER grind also kill note enemies that are part of a FATE (e.g. a
    // shelfscale-reaver FATE), level-syncing to that FATE so the combat backend will engage them
    // (RSR drops a FATE mob whose FateId != the player's synced fate, so without the sync it would
    // hard-target one but never cast). FATE-spawned note mobs credit the book the same as open-world
    // ones, so this clears a slot faster when a matching FATE is up. Off restores the old behaviour:
    // skip FATE spawns so the grind is never pulled into a FATE.
    public bool AllowFateNoteKills { get; set; } = true;
    public bool EnableTextAdvance { get; set; } = true;
    public bool EnableAutoDuty { get; set; } = true;
    public bool EnableLifestream { get; set; } = true;

    // Manual helper: clicking an entry in the in-game Trials of the Braves
    // book (RelicNoteBook addon) flags that target and teleports to its zone (dungeons open the Duty
    // Finder). Independent of the automation runner. See Braves.RelicNoteBookHook.
    public bool BookClickNavigate { get; set; } = true;

    // Best-effort equip the in-progress relic weapon before a duty so its drops credit. If the
    // equip fails (or this is off), the duty step pauses and asks you to equip it manually.
    public bool AutoEquipRelicInDuty { get; set; } = true;

    // Procedural objectives (AllStepsDone) that have been completed, persisted so
    // Novus/Nexus/Zeta steps are not re-run after a plugin reload.
    public System.Collections.Generic.List<string> CompletedProceduralObjectives { get; set; } = new();

    // Death handling: on death, return to a home point and resume the current
    // objective from its start (rather than stopping the run).
    public bool RecoverOnDeath { get; set; } = true;

    // Stop conditions left intentionally unwired (default off).
    public bool StopWhenOutOfLeveAllowances { get; set; }
    public int StopAfterNRelics { get; set; }
    public bool StopOnInventoryFull { get; set; }

    // Treasure-map farm: when true, RunTreasureMaps keeps farming and never auto-
    // completes at the step's target count (e.g. stockpiling Alexandrite past 75). It
    // restocks maps from Auriana and runs until you press Stop (or it can no longer get
    // maps). Turn off to stop at the target so the relic line can advance.
    public bool EndlessTreasureMapFarm { get; set; } = true;

    // Safety pauses left intentionally unwired (default off).
    public bool PauseIfTargetedByPlayer { get; set; }
    public bool PauseOnTell { get; set; }

    // ---- Auto-discard (see Steps/AutoDiscard.cs) ----
    //
    // Discarding is PERMANENT and deliberately silent -- no confirmation is shown, which is the
    // whole point of the feature (a long unattended grind fills the bags with mob drops). So the
    // toggle is off by default and every gate below is on the cautious side.
    public bool AutoDiscardDrops { get; set; }

    public enum DiscardMode
    {
        // Only the ids in DiscardItemIds. Predictable; nothing is deleted that was not named.
        ListOnly,
        // Rule-driven: common (white), stackable, non-usable, non-equippable, tradeable materials
        // whose vendor price is at or under AutoDiscardMaxVendorPrice -- i.e. ordinary mob-drop
        // clutter. Every relic material the plugin knows about is protected regardless.
        LowValueMaterials,
    }

    // Default LowValueMaterials so switching the toggle on does the hands-off thing that was asked
    // for; the configuration window shows a live list of exactly what would go before you enable it.
    // Persisted as an int like every other enum here (this file is written by Newtonsoft).
    public DiscardMode AutoDiscardMode { get; set; } = DiscardMode.LowValueMaterials;

    // Only discard while the automation is actually running, so nothing is deleted out from under
    // you during normal manual play. On by default.
    public bool AutoDiscardOnlyWhileRunning { get; set; } = true;

    // LowValueMaterials only: the vendor (sell) price at or below which a material counts as
    // clutter. ARR mob drops sit in the single/double digits; 100 keeps anything worth carrying.
    public int AutoDiscardMaxVendorPrice { get; set; } = 100;

    // Always discard these item ids (both modes). Never discard these ones (wins over everything).
    public System.Collections.Generic.List<uint> DiscardItemIds { get; set; } = new();
    public System.Collections.Generic.List<uint> NeverDiscardItemIds { get; set; } = new();

    // FATE survival fallback (Steps/Combat/FateSyncGuard.cs). Level sync is what makes FATE combat
    // work, and also what makes a boss FATE able to kill a relic-geared character -- and dying costs
    // far more than one FATE (the death recovery Returns home and restarts the objective). Below
    // FateUnsyncHpPercent, drop the sync and forfeit that FATE's credit rather than die; re-sync
    // once health is back above FateResyncHpPercent. Skipped when the mob will die first.
    public bool FateUnsyncOnLowHp { get; set; } = true;
    public int FateUnsyncHpPercent { get; set; } = 10;
    public int FateResyncHpPercent { get; set; } = 60;

    // The Atma weapon whose Trials of the Braves books this install actually drove to completion
    // (0 = none). PERSISTED, and that is the whole point of it.
    //
    // The game keeps the last bought Relic Note active forever, so "note 9, complete, on an Atma
    // weapon" is two different situations wearing the same face: a repeat relic's fresh Atma that
    // inherited the PREVIOUS relic's finished note (buy book 1 and grind again), or a weapon that
    // just finished its own nine books (go do the Atma -> Animus enhancement at Jalzahn). Nothing
    // in game memory tells them apart, so the only witness is whether WE drove the books.
    //
    // That witness used to live in a field, which meant a plugin reload or a game restart between
    // finishing book 9 and pressing Start erased it -- and the run then read a freshly-finished book
    // run as a stale note and bought book 1, restarting a nine-book grind for 500 poetics. Cleared
    // again the moment the weapon reaches Animus, so the NEXT relic on the same job (same item id)
    // starts its books properly instead of inheriting this one's answer.
    public uint AnimusBooksDrivenWeaponId { get; set; }

    // Global aggro backstop (Steps/Combat/AggroWatchdog.cs). Every executor that fights already
    // defends itself, but only the branches that opted in -- and the branches that got missed are
    // the travel and wait legs, which is where an ambient mob actually lands on you. This watches
    // from outside the executors: when something is engaged with us, nothing is engaged with IT, and
    // that has held for AggroWatchdogSeconds (x3 while still moving, since a mob picked up in
    // passing usually drops on its own), it stops and fights back. Stands down inside a FATE, where
    // being surrounded is normal and the FATE executor owns targeting and level sync.
    public bool AggroWatchdog { get; set; } = true;
    public int AggroWatchdogSeconds { get; set; } = 5;

    // Combat assist
    public bool AutoSummonChocobo { get; set; } = true;
    public bool ChocoboHealerStance { get; set; } = true;
    public bool UseBossModRebornAvoidance { get; set; } = true;
    // Name of a BossMod Reborn autorotation preset configured for AoE avoidance, activated
    // via BossMod.Presets.SetActive (BMR keeps the "BossMod." IPC prefix). The preset must
    // exist in BMR (create one named exactly this and set its strategy tracks to
    // avoidance-only so it does not run the rotation and fight RSR). Default is BMR's
    // built-in AI preset "VBM Multibox"; empty disables BMR control.
    // Only used when Backend != BossModReborn. Under the BossMod Reborn backend this
    // separate avoidance preset is not activated, because SetActive is exclusive and would
    // clobber the combat rotation preset (that backend keeps vnavmesh in control of
    // movement instead).
    public string BossModRebornAvoidancePreset { get; set; } = string.Empty;

    // One-time migration marker for the avoidance-preset default. Builds up to 1.5.1.0
    // shipped "VBM Multibox" here, which contains MiscAI.AutoTarget [Retarget=Always] --
    // it writes TargetSystem->Target every frame and so hijacked the hard target from
    // whichever plugin owned the rotation (RSR, and now Wrath Combo) -- plus
    // MiscAI.FollowSlot, which walked the character into melee against vnavmesh. Saved
    // configs carry that value forward, so the default change alone would not reach
    // anyone who already ran the plugin. Applied once, so a deliberate later choice of
    // "VBM Multibox" is preserved. See External/BossModRebornAvoidancePreset.
    public bool AvoidancePresetMigratedOffMultibox { get; set; }

    // Name of the BossMod Reborn autorotation preset used when Backend == BossModReborn
    // (BMR drives the rotation instead of RSR). Activated via BossMod.Presets.SetActive
    // when Relicable engages and cleared when it disengages.
    //
    // EMPTY (the default) = use Relicable's OWN shipped preset, "Relicable Combat", which
    // Relicable auto-installs into BMR (BossModRebornRelicPreset /
    // BossModRebornCombatBackend). That preset is rotation-only with every job's Targeting
    // = "Manual", so BMR casts on the player's current hard target -- including a NEUTRAL,
    // un-aggroed relic-note mob -- and never moves the character (vnavmesh keeps full
    // navigation control).
    //
    // BMR's shipped "VBM Default" preset uses Targeting = "Auto", which auto-selects a
    // target from the priority list and scores a neutral out-of-combat mob at 0 -> the
    // rotation never fires ("hard-targets it but never attacks"). "VBM Default" here is
    // treated the same as empty (use the shipped preset).
    //
    // Set this to a custom preset NAME only if you built your own rotation-only preset in
    // BMR (job modules, Targeting = Manual, NO movement/AI modules); it is honored when it
    // exists, else Relicable falls back to its shipped preset. Do NOT name a movement
    // preset like "VBM Multibox" -- it adds movement modules and BMR fights vnavmesh.
    public string BossModRebornCombatPreset { get; set; } = string.Empty;

    // Legacy keys from before the BossMod Reborn port (1.4.173.0 renamed the three
    // properties above). Set-only + [JsonProperty] so Newtonsoft READS an old config's
    // keys into the renamed properties on load but never writes them back -- after the
    // first save only the new keys exist. Without these, the rename would silently
    // reset saved values to defaults.
    [JsonProperty("UseBossModAvoidance")]
    private bool LegacyUseBossModAvoidance { set => UseBossModRebornAvoidance = value; }
    [JsonProperty("BossModAvoidancePreset")]
    private string LegacyBossModAvoidancePreset { set => BossModRebornAvoidancePreset = value; }
    [JsonProperty("BossModCombatPreset")]
    private string LegacyBossModCombatPreset { set => BossModRebornCombatPreset = value; }

    // ---- Early Alpha access ----
    // The redeemed access code, stored verbatim so it can be re-verified on every load
    // (rather than caching an "unlocked" boolean, which would survive the code expiring).
    // Validated by Licensing.AlphaCode against the signing public key compiled into the
    // build; see Licensing/AlphaGate.cs.
    public string AlphaAccessCode { get; set; } = string.Empty;

    // Diagnostics
    public bool EnableDebugLog { get; set; }
}
