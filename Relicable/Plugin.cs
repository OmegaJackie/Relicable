using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using Relicable.Controllers;
using Relicable.Data;
using Relicable.External;
using Relicable.Model;
using Relicable.Steps;
using Relicable.Windows;

namespace Relicable;

// Plugin entry point. Wires up Dalamud services, builds the IPC wrappers and the
// executor set, constructs the controller, and registers commands and the tick.
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IFateTable FateTable { get; private set; } = null!;
    [PluginService] internal static IBuddyList Buddies { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration _config;
    private readonly RelicController _controller;
    private readonly WindowSystem _windowSystem = new("Relicable");
    private readonly MainWindow _mainWindow;
    private readonly ConfigWindow _configWindow;
    private readonly NovusWindow _novusWindow;
    private readonly BravesWindow _bravesWindow;
    private readonly External.UniversalisClient _universalis;
    private readonly External.UniversalisClient _bravesUniversalis;
    private readonly Novus.RetainerScanner _retainerScanner;
    private readonly Novus.NovusActionRunner _novusRunner;
    private readonly BaseRelic.PrerequisiteChecker _prereqChecker;
    private readonly BaseRelicWindow _baseRelicWindow;
    private readonly External.AutoDutyIpc _autoDuty;
    private readonly Braves.RelicNoteBookHook _bookHook;
    private readonly External.IfritBurstRotationSwap _burstRotationSwap;
    private readonly Licensing.AlphaGate _alphaGate;
    private readonly AlphaGateWindow _alphaGateWindow;
    // Latches the Early Alpha gate closing mid-session (a code expiring while the game is
    // open), so the running automation is stopped exactly once rather than every frame.
    private bool _stoppedForAlphaGate;

    public Plugin()
    {
        // Initialize ECommons before anything uses Svc / AddonMaster (the leve
        // accept + initiate flow, ported from Battlevest, relies on it).
        ECommonsMain.Init(PluginInterface, this);

        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // One-time: move the Zeta farm off the old Aurum Vale (172) default to Bowl of Embers
        // (Extreme) (295). Skipped once applied, so a later deliberate choice is preserved.
        if (!_config.ZetaFarmUpgradedToEmbers)
        {
            if (_config.ZetaFarmTerritoryType == 172)
                _config.ZetaFarmTerritoryType = 295;
            _config.ZetaFarmUpgradedToEmbers = true;
            PluginInterface.SavePluginConfig(_config);
        }

        // Early Alpha gate. Constructed before anything else so its state is known by the
        // time the windows are built; it re-verifies the stored code against the signing
        // public key on every load, so an expired or revoked code closes the gate again
        // rather than staying unlocked from a cached flag.
        _alphaGate = new Licensing.AlphaGate(_config, () => PluginInterface.SavePluginConfig(_config));

        Diagnostics.DebugLog.Enabled = _config.EnableDebugLog;
        Steps.LocationNavigator.Config = _config; // flight gate for the click-to-fly flow
        _prereqChecker = new BaseRelic.PrerequisiteChecker(_config);

        // IPC wrappers.
        var commands = new CommandHelper();
        var navmesh = new NavmeshIpc(PluginInterface);
        var rotation = new RotationSolverIpc(PluginInterface, commands, _config);
        var lifestream = new LifestreamIpc(PluginInterface);
        var textAdvance = new TextAdvanceIpc(PluginInterface);
        var autoDuty = new AutoDutyIpc(PluginInterface);
        _autoDuty = autoDuty;
        var bossModReborn = new BossModRebornIpc(PluginInterface);
        // Combat backend: RSR or BossMod Reborn's autorotation, selected live from
        // Configuration.Backend. BossModRebornCombatBackend lets BossMod Reborn drive the
        // rotation so RSR is not required; CombatRouter dispatches to the chosen one (or none).
        var bossModRebornCombat = new BossModRebornCombatBackend(PluginInterface, _config);
        var combat = new CombatRouter(_config, rotation, bossModRebornCombat);
        var autoRetainer = new AutoRetainerIpc(PluginInterface);
        // Croizat's Bundle of Tweaks (CBT) -- optional Atma FATE-farm backend (its Fate Tool Kit).
        var bundleOfTweaks = new BundleOfTweaksIpc(PluginInterface);

        // Bowl of Embers (Extreme) burst rotations: while in the farm duty, RSR runs the
        // job-specific "Ifrit EX Burst" rotation instead of the user's normal pick, and gets it
        // back on the way out. Ticked below; independent of the automation runner, so it also
        // applies to manual farm runs.
        _burstRotationSwap = new IfritBurstRotationSwap(
            _config,
            new RsrRotationOverride(_config, () => PluginInterface.SavePluginConfig(_config)),
            new RsrTargetingOverride());

        // Novus materia: live market prices, the planner that joins held + retainer
        // materia with the cheapest route, and a scanner that records retainer materia
        // from the native bell UI (AutoRetainer's IPC cannot supply item-level counts).
        _universalis = new External.UniversalisClient();
        _bravesUniversalis = new External.UniversalisClient();
        var planner = new Novus.MateriaPlanner(_config, _universalis);
        _retainerScanner = new Novus.RetainerScanner(_config, () => PluginInterface.SavePluginConfig(_config));
        _novusRunner = new Novus.NovusActionRunner(_config, planner, autoRetainer);

        // Targeting layer backed by the live object table.
        var targeting = new Targeting(new DalamudObjectProvider());

        var ctx = new ExecutionContext
        {
            Navmesh = navmesh,
            Rotation = combat,
            Lifestream = lifestream,
            TextAdvance = textAdvance,
            AutoDuty = autoDuty,
            BossModReborn = bossModReborn,
            Bot = bundleOfTweaks,
            Targeting = targeting,
            Commands = commands,
            Config = _config,
            MateriaPlanner = planner,
        };

        // Objective data: static files (Atma, Novus, upgrades) from Data/relics,
        // plus Animus entries generated from the RelicNote Excel sheet.
        var objectives = LoadObjectives();

        var executors = new List<ITaskExecutor>
        {
            new AetheryteTeleportExecutor(),
            new AethernetTravelExecutor(),
            new MoveToExecutor(),
            new MoveToFlagExecutor(),
            new KillTargetExecutor(),
            new ParticipateFateExecutor(),
            new StartLeveExecutor(),
            new TurnInLeveExecutor(),
            new EnterDutyExecutor(),
            new EnsureRelicEquippedExecutor(),
            new AttachMahatmaExecutor(),
            new AtmaUpgradeExecutor(),
            new AnimusUpgradeExecutor(),
            new NexusUpgradeExecutor(),
            new BravesReportExecutor(),
            new BuyRelicBookExecutor(),
            new BuyRadzOilExecutor(),
            new InteractNpcExecutor(),
            new InteractObjectExecutor(),
            new TurnInItemsExecutor(),
            new UseItemExecutor(),
            new UpgradeRelicExecutor(),
            new MeldMateriaExecutor(),
            new MeldNovusRouteExecutor(),
            new WaitForConditionExecutor(),
            new TreasureMapExecutor(),
        };

        var dependencies = new DependencyRegistry(
            PluginInterface, _config, navmesh, rotation, lifestream, textAdvance, autoDuty, bossModReborn, autoRetainer);

        _controller = new RelicController(ctx, objectives, executors, dependencies);

        _novusWindow = new NovusWindow(_config, planner, _novusRunner, () => PluginInterface.SavePluginConfig(_config));

        // Braves (il125) planner: its own Universalis client so its item set does not
        // contend with the Novus planner's price cache. Artisan is optional (best-effort).
        var bravesPlanner = new Braves.BravesPlanner(_config, _bravesUniversalis);
        var artisan = new ArtisanIpc(PluginInterface);
        _bravesWindow = new BravesWindow(_config, bravesPlanner, artisan, () => PluginInterface.SavePluginConfig(_config));

        // Artisan crafting LISTS (the base relic's class-weapon step). Artisan registers no
        // create-a-list IPC gate, so this drives its own public list API in the loaded assembly;
        // absent Artisan it simply reports unavailable and the button is hidden.
        var artisanLists = new ArtisanCraftingList();

        // Base-relic questmap: the Questionable-style journal for A Relic Reborn (prerequisites,
        // parts, materials), rendered live from the PrerequisiteChecker report.
        _baseRelicWindow = new BaseRelicWindow(_prereqChecker, _config, artisanLists);

        _mainWindow = new MainWindow(
            _controller, _config, navmesh, artisanLists, _alphaGate,
            _novusWindow.Toggle, _bravesWindow.Toggle, _baseRelicWindow.Toggle,
            () => PluginInterface.SavePluginConfig(_config),
            // Deferred: _configWindow is assigned on the next line, so a method group here would
            // capture the still-null field. The lambda reads it when the cog is actually clicked
            // (always assigned by then; ?. keeps the compiler's null-flow analysis happy).
            () => _configWindow?.Toggle());
        _configWindow = new ConfigWindow(_config, PluginInterface, dependencies, _burstRotationSwap);

        // Book click-to-travel helper: click a Trials of the Braves entry in the in-game book to flag it
        // and teleport. Hooks the RelicNoteBook addon; independent of the automation runner.
        _bookHook = new Braves.RelicNoteBookHook(_config);

        _alphaGateWindow = new AlphaGateWindow(_alphaGate, _mainWindow.Toggle);

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_novusWindow);
        _windowSystem.AddWindow(_bravesWindow);
        _windowSystem.AddWindow(_baseRelicWindow);
        _windowSystem.AddWindow(_alphaGateWindow);

        // Locked: open the gate on load rather than leaving a new user to discover why
        // nothing happens when they press Start.
        if (!_alphaGate.Unlocked)
            _alphaGateWindow.IsOpen = true;

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;

        Commands.AddHandler("/relic", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Relicable window. Subcommands: config, novus, braves, questmap, start, stop, reload.",
        });

        Framework.Update += OnUpdate;
    }

    private static List<Model.RelicObjective> LoadObjectives()
    {
        var objectives = new List<Model.RelicObjective>();

        // Static objectives (Data/relics/*.json) plus the generated Animus book objectives. The
        // generator reads the real RelicNote book data (correct zone + authored spawn coords), so
        // a static Animus monster/FATE/leve/dungeon sample for a slot the generator already produces
        // is a redundant -- and, as with the Amalj'aa Thaumaturge sample, wrong -- duplicate; drop it
        // in favour of the generated one. (The generator now emits DungeonSlot objectives too, from
        // MonsterNoteTargetNM via AutoDuty, so those are deduped by the Book:Slot:Kind key as well.) A
        // static the generator does NOT cover (a book/slot/kind it did not emit -- e.g. a dungeon slot
        // whose duty did not resolve) is kept, and if the generator produced nothing (it failed) no
        // keys exist so every static survives as a fallback.
        var generated = RelicNoteDataGenerator.Generate(DataManager);
        var genAnimusKeys = new HashSet<string>();
        foreach (var g in generated)
            if (g.Stage == Model.RelicStage.Animus)
                genAnimusKeys.Add($"{g.Completion.Book}:{g.Completion.Slot}:{g.Completion.Kind}");
        foreach (var o in DataLoader.LoadAll(PluginInterface))
        {
            if (o.Stage == Model.RelicStage.Animus
                && genAnimusKeys.Contains($"{o.Completion.Book}:{o.Completion.Slot}:{o.Completion.Kind}"))
                continue; // the generated book objective supersedes this static sample
            objectives.Add(o);
        }
        objectives.AddRange(generated);
        // Quest-path objectives (sequence-accurate, from Data/questpaths) supplement the
        // generated hunt/trial objectives: the controller runs a path step when one is
        // mapped for the live quest sequence, otherwise the generated objective. So both
        // are loaded; the generator is not skipped.
        var (questPaths, questPathJobs) = BaseRelic.QuestPathLoader.LoadAll(PluginInterface);
        objectives.AddRange(questPaths);
        // Pass the quest-path-covered jobs so the generator does not duplicate the start-of-line
        // (accept / broken weapon / report) sequences those files already author; Parts 3-10 are
        // still generated for every job.
        objectives.AddRange(BaseRelic.BaseRelicHuntGenerator.Generate(questPathJobs));
        // Braves (il125) material-quest dungeons: the controller runs the active quest's set.
        objectives.AddRange(Braves.BravesDungeonGenerator.Generate());
        return objectives;
    }

    // The one place the plugin's main window is opened, so a locked build always lands on
    // the gate instead of a window whose buttons do nothing.
    private void OpenMainUi()
    {
        if (_alphaGate.Unlocked)
            _mainWindow.Toggle();
        else
            _alphaGateWindow.IsOpen = true;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
            return;

        // ---- Early Alpha gate ----
        // The single choke point. Everything below this line is automation, and none of it
        // runs without a valid code. Enforcing it here rather than inside each executor
        // means there is exactly one place to read and one place to audit -- no window
        // button or slash command can quietly route around it.
        _alphaGate.Tick();
        if (!_alphaGate.Unlocked)
        {
            // A code that expired mid-session: stop the run once, tell the user why, and
            // put the gate back in front of them.
            if (!_stoppedForAlphaGate)
            {
                _stoppedForAlphaGate = true;
                _controller.Stop();
                _alphaGateWindow.IsOpen = true;
                if (!string.IsNullOrEmpty(_alphaGate.Status))
                    Log.Warning("Relicable: " + _alphaGate.Status);
            }
            return;
        }
        _stoppedForAlphaGate = false;

        // Drive the planner's click-to-fly (teleport, then /vnav flyflag once you arrive).
        Steps.LocationNavigator.Tick();
        // Record retainer materia whenever a retainer is open, independent of whether
        // automation is running, so the Novus planner always has fresh stock data.
        _retainerScanner.Tick();
        // The Novus popout actions (Infuse / Fetch) run independently of the main
        // controller, so tick them here too.
        _novusRunner.Tick();
        // Swap RSR to the Ifrit EX burst rotation while in the Bowl of Embers (Extreme), and
        // restore the user's own choice on the way out. Polled (not event-driven) so re-entries
        // and duplicate zone events converge instead of re-running a handler.
        _burstRotationSwap.Tick();
        // Global leve-completion handler (mirrors Battlevest's always-on Core.OnUpdate -> HandleYesno):
        // accept the "return to the levemete?" prompt the leve director raises AFTER a leve completes.
        // It can appear in the gap between the StartLeve step crediting and the next step -- or after the
        // LAST leve, once the controller has Stopped -- where no executor is alive to catch it (the
        // reported "not accepting the teleport back"). Driven unconditionally (NOT gated on the run
        // state, so it survives a Stop); it self-gates on a leve-activity window StartLeveExecutor keeps
        // warm, so it is inert -- and leaves YesAlready alone -- outside that brief window.
        Steps.Interaction.LeveReturn.Tick();
        _controller.Tick();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Locked build: every subcommand lands on the gate. Silently doing nothing (or
        // opening a window whose controls are inert) is the confusing alternative.
        if (!_alphaGate.Unlocked)
        {
            _alphaGateWindow.IsOpen = true;
            return;
        }

        // Undocumented diagnostic subcommands. Kept, because an alpha tester with a stuck step
        // gets asked to run one and paste /xllog -- but only reachable with "Enable debug log"
        // ticked in /relic config > Diagnostics, and never advertised in the /relic help text.
        // `adset` in particular writes live into ANOTHER plugin's configuration, which no normal
        // install should be able to reach by typing a word after /relic.
        if (IsDiagnosticCommand(lower))
        {
            if (!_config.EnableDebugLog)
            {
                Log.Warning("Relicable: diagnostic commands require \"Enable debug log\" " +
                            "(/relic config > Diagnostics).");
                return;
            }
            RunDiagnosticCommand(lower, trimmed);
            return;
        }

        switch (lower)
        {
            case "config": _configWindow.Toggle(); break;
            case "novus": _novusWindow.Toggle(); break;
            case "braves": _bravesWindow.Toggle(); break;
            case "questmap": _baseRelicWindow.Toggle(); break;
            case "start":
                if (!_controller.Start())
                {
                    var missing = _controller.MissingRequiredDependencies();
                    var msg = "Relicable: cannot start, missing required plugins: " + string.Join(", ", missing);
                    if (MissingIsCombatBackend(missing))
                        msg += ". Your combat backend requires that plugin -- switch it in /relic config > Settings " +
                               "(Combat backend) to a plugin you have installed (BossMod Reborn or RSR), or install the one shown.";
                    Log.Warning(msg);
                }
                break;
            case "stop": _controller.Stop(); break;
            case "reload": _controller.ReloadObjectives(LoadObjectives()); break;
            default: _mainWindow.Toggle(); break;
        }
    }

    // The set of undocumented diagnostic subcommands, matched before the user-facing switch.
    private static bool IsDiagnosticCommand(string lower)
        => lower is "adcfg" or "bravesseq" or "prereq" or "questwork" or "quests" or "mahatma"
           || lower.StartsWith("adset ");

    // Diagnostic dispatch. Only called once EnableDebugLog has been verified, so every report
    // here is an explicit, opt-in action rather than something a stray /relic argument can hit.
    private void RunDiagnosticCommand(string lower, string trimmed)
    {
        if (lower == "adcfg")
        {
            // Dump AutoDuty's candidate config fields (see AutoDutyIpc.ProbeConfig).
            _autoDuty.ProbeConfig();
            return;
        }
        if (lower.StartsWith("adset "))
        {
            // Set one AutoDuty config field live, so a key found by adcfg can be tested
            // without a rebuild. Writes into another plugin's configuration.
            var rest = trimmed.Substring("adset ".Length).Trim();
            var sp = rest.IndexOf(' ');
            if (sp <= 0)
            {
                Log.Warning("Relicable: usage /relic adset <key> <value>");
                return;
            }
            _autoDuty.SetConfigDebug(rest.Substring(0, sp).Trim(), rest.Substring(sp + 1).Trim());
            return;
        }

        switch (lower)
        {
            case "bravesseq":
                foreach (var line in Braves.BravesDungeonGenerator.CalibrationReport())
                    Log.Information("Relicable: " + line);
                break;
            case "prereq":
                RunPrereqReport();
                break;
            case "questwork":
            case "quests":
                foreach (var line in BaseRelic.ZodiacQuestState.LineReport())
                    Log.Information(line);
                break;
            case "mahatma":
                Steps.GameState.LogMahatmaDebug();
                break;
        }
    }

    // True when a missing required plugin is a combat backend (RSR or BossMod Reborn), so the
    // start-blocked message can point the user at the Combat backend setting instead of just
    // naming the plugin -- the usual cause of "it still wants RSR" is the backend being set to
    // RSR (the required backend gates on Configuration.Backend).
    private static bool MissingIsCombatBackend(IReadOnlyList<string> missing)
    {
        foreach (var m in missing)
            if (m.Contains("Rotation Solver") || m.Contains("BossMod"))
                return true;
        return false;
    }

    // Foundation testability hook: write the base-relic readiness report for the active
    // (or overridden) job to the Dalamud log (/xllog). The visual panel is a later pass.
    private void RunPrereqReport()
    {
        var report = _prereqChecker.Build();
        foreach (var line in BaseRelic.PrerequisiteReportFormatter.ToLines(report))
            Log.Information(line);
        var unresolved = BaseRelic.BaseRelicCatalog.UnresolvedNames();
        if (unresolved.Count > 0)
            Log.Warning($"Relicable: {unresolved.Count} base-relic name(s) did not resolve to ids: {string.Join(", ", unresolved)}");
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        _windowSystem.RemoveAllWindows();
        Commands.RemoveHandler("/relic");
        _bookHook.Dispose();
        // Hand RSR's rotation choice back BEFORE the config is saved below, so the cleared
        // breadcrumb is what gets persisted.
        _burstRotationSwap.Restore();
        _controller.Stop();
        // Drop our YesAlready stop-request so our name does not linger in the shared set after we are
        // gone (the leve-return handler adds it while running). Must run before ECommonsMain.Dispose,
        // which tears down the EzSharedData plumbing this reads.
        Steps.Interaction.LeveReturn.Release();
        _universalis.Dispose();
        _bravesUniversalis.Dispose();
        PluginInterface.SavePluginConfig(_config);
        ECommonsMain.Dispose();
    }

    private sealed class CommandHelper : ICommandHelper
    {
        public void Run(string command)
            => Commands.ProcessCommand(command);
    }
}
