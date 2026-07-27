using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Relicable.External;
using Relicable.Model;

namespace Relicable.Windows;

// Configuration surface mirroring Questionable: a Settings tab (combat backend,
// companion toggles, stop conditions, diagnostics) and a Dependencies tab that
// shows the live install/load/IPC status of every companion plugin with links to
// install anything missing.
public sealed class ConfigWindow : Window
{
    private static readonly Vector4 Green = new(0.40f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Grey = new(0.70f, 0.70f, 0.70f, 1f);

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly DependencyRegistry _dependencies;
    private readonly External.IfritBurstRotationSwap _burstRotationSwap;

    // Save-on-change (debounced): previously nothing persisted unless the Save button
    // was pressed, so toggles applied live silently reverted on reload -- and the
    // sibling windows (Novus/Braves) already save their shared settings on change.
    // The dirty flag is flushed once no widget is active, so slider drags and typing
    // do not write the config JSON every frame.
    private bool _dirty;

    public ConfigWindow(Configuration config, IDalamudPluginInterface pi, DependencyRegistry dependencies,
        External.IfritBurstRotationSwap burstRotationSwap)
        : base("Relicable Configuration")
    {
        Size = new Vector2(460, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        _config = config;
        _pi = pi;
        _dependencies = dependencies;
        _burstRotationSwap = burstRotationSwap;
    }

    // Flush a pending debounced save if the window closes mid-edit, so nothing is lost.
    public override void OnClose()
    {
        if (!_dirty)
            return;
        _pi.SavePluginConfig(_config);
        _dirty = false;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("relicable_tabs"))
        {
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Dependencies"))
            {
                DrawDependencies();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawSettings()
    {
        ImGui.TextDisabled("Atma farm");
        var atmaBackend = (int)_config.AtmaBackend;
        if (ImGui.Combo("Atma backend", ref atmaBackend, "Built-in\0CBT Fate Tool Kit\0"))
        {
            _config.AtmaBackend = (Configuration.AtmaFarmBackend)atmaBackend;
            _dirty = true;
        }
        Ui.Tooltip("Who runs the Atma FATE farm.\n\n" +
            "Built-in: Relicable's own zone-by-zone farm.\n\n" +
            "CBT Fate Tool Kit: hand the grind to Bundle of Tweaks. Select its 'Atma (Zodiac)' mode " +
            "once in CBT's own window; Relicable starts and stops the grind and resumes once the atmas are collected.");
        if (_config.AtmaBackend == Configuration.AtmaFarmBackend.CbtFateToolKit)
            Ui.Note("Requires Bundle of Tweaks. Select 'Atma (Zodiac)' in its Fate Tool Kit window once; " +
                "the Zenith enhancement itself is left to you, with a prompt when it is ready.");
        else
        {
            var atmaPerZone = _config.AtmaPerZone;
            if (ImGui.InputInt("Atmas per zone, then move on", ref atmaPerZone))
            {
                _config.AtmaPerZone = atmaPerZone < 1 ? 1 : atmaPerZone;
                _dirty = true;
            }
            Ui.Tooltip("How many of a zone's atma to collect before the farm moves to the next zone.\n\n" +
                "A single relic only needs one of each (the default). Raise it to bank spare sets for " +
                "repeat relics in the same trip; the enhancement waits until every zone reaches this target.");
        }
        ImGui.Separator();

        ImGui.TextDisabled("Combat");
        var backend = (int)_config.Backend;
        if (ImGui.Combo("Combat backend", ref backend, "None\0Rotation Solver Reborn\0BossMod Reborn\0Wrath Combo\0"))
        {
            _config.Backend = (Configuration.CombatBackend)backend;
            _dirty = true;
        }
        if (_config.Backend == Configuration.CombatBackend.BossModReborn)
        {
            Ui.Note("BossMod Reborn drives the rotation; Rotation Solver is not required. Relicable installs and uses " +
                "its own \"" + BossModRebornRelicPreset.Name + "\" preset by default — leave the field below blank.");
            var combatPreset = _config.BossModRebornCombatPreset;
            if (ImGui.InputText("Combat preset (blank = built-in)", ref combatPreset, 64))
            {
                _config.BossModRebornCombatPreset = combatPreset;
                _dirty = true;
            }
            Ui.Tooltip("Leave blank (recommended): Relicable's built-in rotation-only preset makes BossMod Reborn " +
                "attack the current hard target, including neutral book enemies.\n\n" +
                "Only name a preset if you built a rotation-only one yourself (Targeting set to Manual, " +
                "no AI or movement modules). AI presets such as 'VBM Multibox' pick their own targets " +
                "and skip neutral enemies.");

            // Guard the exact misconfiguration that stops the BossMod Reborn backend attacking neutral
            // relic mobs during the beastmen hunt: pointing the COMBAT preset at an AI/movement
            // preset ("VBM Multibox", or the same name as the avoidance preset). Those let BMR
            // pick its own target (skipping non-aggroed mobs) and drive movement against vnavmesh.
            if (LooksLikeAiPreset(_config.BossModRebornCombatPreset))
                Ui.Wrapped(Red,
                    "This looks like an AI/movement preset. Use a rotation-only preset here, or BossMod Reborn " +
                    "will pick its own targets (skipping neutral enemies) and fight the navigation for movement.");
        }
        else if (_config.Backend == Configuration.CombatBackend.RotationSolverReborn)
        {
            Ui.Note("Inside FATEs, Rotation Solver selects and attacks FATE enemies itself; Relicable " +
                "navigates into the ring and level-syncs. These options tune that targeting.");

            var hostile = (int)_config.RsrFateHostileType;
            if (ImGui.Combo("FATE targeting", ref hostile,
                    "All FATE mobs when solo\0All targets in range\0Only already-engaged\0"))
            {
                _config.RsrFateHostileType = (Configuration.FateHostility)hostile;
                _dirty = true;
            }
            Ui.Tooltip("How Rotation Solver picks targets inside a FATE.\n\n" +
                "'All FATE mobs when solo' (recommended) attacks every valid FATE enemy. " +
                "'Only already-engaged' waits for enemies to aggro first — for grouped play.");

            Checkbox("Ignore non-FATE mobs while in a FATE", _config.RsrFateIgnoreNonFateTargets,
                v => _config.RsrFateIgnoreNonFateTargets = v);
            Ui.Tooltip("Only target FATE enemies while inside a FATE, so combat never wanders onto an " +
                "unrelated overworld enemy. Recommended on.");

            Checkbox("Prioritise FATE mobs", _config.RsrFateTargetFatePriority,
                v => _config.RsrFateTargetFatePriority = v);
            Ui.Tooltip("Prefer FATE enemies when choosing a target.");

            Checkbox("Auto-grab the nearest FATE mob", _config.RsrFateTargetFreely,
                v => _config.RsrFateTargetFreely = v);
            Ui.Tooltip("Automatically pick up the nearest FATE enemy. Applied only inside the FATE and " +
                "never written to your saved Rotation Solver settings. Requires a recent Rotation Solver; " +
                "older builds ignore it.");
        }
        else if (_config.Backend == Configuration.CombatBackend.WrathCombo)
        {
            Ui.Note("Wrath Combo is lease-based: Relicable registers for control while it runs and hands it back " +
                "when it unloads. Wrath's own window marks the settings Relicable is driving.");

            Checkbox("Let Relicable configure Wrath's Auto-Rotation", _config.WrathManageAutoRotationConfig,
                v => _config.WrathManageAutoRotationConfig = v);
            Ui.Tooltip("Recommended on. Relicable clears Wrath's in-combat gating so the rotation will open on a " +
                "NEUTRAL relic-note enemy, and sets FATE targeting while in a FATE.\n\n" +
                "Turn it off to have Relicable only switch Auto-Rotation on and off, leaving the rest of your Wrath " +
                "configuration exactly as you set it.");

            if (!_config.WrathManageAutoRotationConfig)
                Ui.Wrapped(Yellow,
                    "With this off, Wrath will not open on a neutral book enemy unless you have already turned off " +
                    "'In combat only' and 'Only attack in combat' in Wrath yourself — the relic grind will stall.");

            var wrathTargeting = (int)_config.WrathFateTargeting;
            if (ImGui.Combo("FATE targeting", ref wrathTargeting,
                    "Manual (Relicable picks)\0Highest max HP\0Lowest max HP\0Highest current HP\0Lowest current HP\0Tank's target\0Nearest\0Furthest\0"))
            {
                _config.WrathFateTargeting = (Configuration.WrathDpsTargeting)wrathTargeting;
                _dirty = true;
            }
            Ui.Tooltip("How Wrath picks targets inside a FATE.\n\n" +
                "'Highest max HP' favours the FATE boss; 'Nearest' is steadier for FATEs with many adds. " +
                "'Manual' hands targeting back to Relicable, which then hard-targets each enemy itself.");

            Ui.Note("The relic-note grind always pins Wrath to Manual targeting regardless of this setting — those " +
                "enemies are neutral and have to be pulled off a hard target.");
        }

        Checkbox("Target Ifrit's Infernal Nails first in the Bowl of Embers (Extreme)",
            _config.PrioritiseIfritNailTargeting, v => _config.PrioritiseIfritNailTargeting = v);
        Ui.Tooltip("Attack the Infernal Nails before Ifrit — he is invulnerable until every nail is destroyed, " +
            "and the default target order picks him first.\n\n" +
            "Applies only while nails are up, is released the moment you leave, and never touches your saved " +
            "Rotation Solver settings. Works with the standard Rotation Solver plugin.");

        Checkbox("Use the Ifrit EX burst rotation in the Bowl of Embers (Extreme)",
            _config.AutoSwapIfritBurstRotation, v => _config.AutoSwapIfritBurstRotation = v);
        Ui.Tooltip("Requires a custom Rotation Solver build that bundles the 'Ifrit EX Burst' rotations; " +
            "the official plugin ignores this setting.\n\n" +
            "When active, entering the duty swaps to your job's burst rotation (tuned for solo unsynced " +
            "kills on a relic weapon) and your own rotation choice is restored on the way out. If you pick " +
            "a different rotation inside the duty, Relicable leaves it alone for that visit.");

        // Surface the live state. Without this the only signal that the feature is doing nothing is
        // a single debug-log line per session, while the checkbox still reads as enabled.
        var burstStatus = _burstRotationSwap.LastStatus;
        if (!string.IsNullOrEmpty(burstStatus))
        {
            ImGui.Indent();
            Ui.Wrapped(_burstRotationSwap.LastStatusIsError ? Red : Grey, burstStatus);
            ImGui.Unindent();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Companions");
        Checkbox("Navigation (vnavmesh)", _config.EnableNavmesh, v => _config.EnableNavmesh = v);
        Checkbox("Allow flight", _config.AllowFlight, v => _config.AllowFlight = v);
        Checkbox("Use mount for travel", _config.UseMount, v => _config.UseMount = v);
        Checkbox("Interaction (TextAdvance)", _config.EnableTextAdvance, v => _config.EnableTextAdvance = v);
        Checkbox("Run duties via AutoDuty", _config.EnableAutoDuty, v => _config.EnableAutoDuty = v);
        Ui.Tooltip("Relic trials and dungeons are queued and cleared by AutoDuty.\n\n" +
            "With this off, duty steps cannot run and the relic cannot progress past them.");
        Checkbox("Auto-equip relic before duties", _config.AutoEquipRelicInDuty, v => _config.AutoEquipRelicInDuty = v);
        Ui.Tooltip("Equip the relic from your armoury or bags before each relic duty or hunt, so the drops " +
            "count. If it cannot be equipped — or with this off — the run pauses and asks you.");
        Checkbox("City travel (Lifestream)", _config.EnableLifestream, v => _config.EnableLifestream = v);
        Checkbox("Kill book monsters that are in FATEs", _config.AllowFateNoteKills, v => _config.AllowFateNoteKills = v);
        Ui.Tooltip("Also kill book enemies that are part of a FATE, level-syncing so combat engages them. " +
            "FATE kills credit the book the same as open-world kills.\n\n" +
            "Turn off to skip FATE spawns entirely.");

        ImGui.Separator();
        ImGui.TextDisabled("Manual helpers");
        Checkbox("Click a Trials of the Braves book entry to flag + teleport", _config.BookClickNavigate, v => _config.BookClickNavigate = v);
        Ui.Tooltip("With the in-game book open, click an enemy, FATE, or leve entry to flag it on the map " +
            "and teleport to its zone. Dungeon entries open the Duty Finder. Works even while automation is stopped.");

        ImGui.Separator();
        ImGui.TextDisabled("Death handling");
        Checkbox("Recover on death (return and resume)", _config.RecoverOnDeath, v => _config.RecoverOnDeath = v);

        ImGui.Separator();
        ImGui.TextDisabled("Combat assist");
        Checkbox("Auto-summon chocobo", _config.AutoSummonChocobo, v => _config.AutoSummonChocobo = v);
        Checkbox("Set chocobo to healer stance", _config.ChocoboHealerStance, v => _config.ChocoboHealerStance = v);
        Checkbox("Use BossMod Reborn AoE avoidance", _config.UseBossModRebornAvoidance, v => _config.UseBossModRebornAvoidance = v);
        var preset = _config.BossModRebornAvoidancePreset;
        if (ImGui.InputText("BossMod Reborn avoidance preset", ref preset, 64))
        {
            _config.BossModRebornAvoidancePreset = preset;
            _dirty = true;
        }
        if (_config.Backend == Configuration.CombatBackend.BossModReborn)
            ImGui.TextDisabled("(Not used under the BossMod Reborn backend: it would clobber the rotation preset.)");

        ImGui.Separator();
        ImGui.TextDisabled("Farming");
        Checkbox("Farm Alexandrite endlessly (do not stop at the target)",
            _config.EndlessTreasureMapFarm, v => _config.EndlessTreasureMapFarm = v);
        Ui.Tooltip("Keep the Mysterious Map farm running and restocking past the Alexandrite target until you press Stop.");

        ImGui.Separator();
        ImGui.TextDisabled("Animus (Books / Trials of the Braves)");
        var fateRotate = _config.FateRotateSeconds;
        if (ImGui.InputInt("FATE rotate after (seconds)", ref fateRotate))
        {
            _config.FateRotateSeconds = fateRotate;
            _dirty = true;
        }
        Ui.Tooltip("How long to wait at an unspawned book FATE before rotating to the next one.\n\n" +
            "The first pass just glances at each FATE; later passes wait this many seconds. " +
            "0 waits indefinitely. Default 120.");

        Checkbox("Grab co-located FATEs while in the zone",
            _config.PreferCoLocatedFates, v => _config.PreferCoLocatedFates = v);
        Ui.Tooltip("When a book FATE is active in a zone where you also have enemy work, run it while you are " +
            "there instead of saving all FATEs for last. Only triggers when the FATE has enough time left.");

        ImGui.Separator();
        ImGui.TextDisabled("Nexus (Light farm)");
        var nexusTt = (int)_config.NexusFarmTerritoryType;
        if (ImGui.InputInt("Farm duty (territory id)", ref nexusTt))
        {
            _config.NexusFarmTerritoryType = (uint)(nexusTt < 0 ? 0 : nexusTt);
            _dirty = true;
        }
        Ui.Tooltip("The duty AutoDuty farms for Light. 295 = the Bowl of Embers (Extreme), the standard fast farm.\n\n" +
            "Use AutoDuty's '/autoduty tt <duty name>' to look up another duty's id.");
        var nexusLoops = _config.NexusFarmLoops;
        if (ImGui.InputInt("Loop cap", ref nexusLoops))
        {
            _config.NexusFarmLoops = nexusLoops < 1 ? 1 : nexusLoops;
            _dirty = true;
        }
        Ui.Tooltip("Upper bound on AutoDuty loops per hand-off. Farming stops automatically at full Light regardless.");
        Checkbox("Run unsynced / unrestricted (solo at max level)", _config.NexusFarmUnsynced,
            v => _config.NexusFarmUnsynced = v);
        Ui.Tooltip("Run the duty unsynced so a single max-level character can solo it. Turn off for a synced party.");

        ImGui.Separator();
        ImGui.TextDisabled("Zeta (Mahatma farm)");
        var zetaTt = (int)_config.ZetaFarmTerritoryType;
        if (ImGui.InputInt("Farm duty (territory id)##zeta", ref zetaTt))
        {
            _config.ZetaFarmTerritoryType = (uint)(zetaTt < 0 ? 0 : zetaTt);
            _dirty = true;
        }
        Ui.Tooltip("The duty AutoDuty farms to charge each Mahatma. 295 = the Bowl of Embers (Extreme), " +
            "the fastest farm. 172 = the Aurum Vale, a dungeon alternative. AutoDuty needs a path for the chosen duty.");
        var zetaLoops = _config.ZetaFarmLoops;
        if (ImGui.InputInt("Clears per hand-off##zeta", ref zetaLoops))
        {
            _config.ZetaFarmLoops = zetaLoops < 1 ? 1 : zetaLoops;
            _dirty = true;
        }
        Ui.Tooltip("Duties per AutoDuty hand-off. 1 keeps the loop tight; farming stops the moment a Mahatma awakens regardless.");
        Checkbox("Run unsynced / unrestricted (solo at max level)##zeta", _config.ZetaFarmUnsynced,
            v => _config.ZetaFarmUnsynced = v);
        Ui.Tooltip("Run the duty unsynced so a single max-level character can solo it. Each Mahatma is attached " +
            "at Remon automatically; the final step at Jalzahn is left to you (it needs the relic unequipped).");

        Checkbox("Abandon Ifrit EX when the Infernal Nails spawn", _config.AbandonOnIfritNails,
            v => _config.AbandonOnIfritNails = v);
        Ui.Tooltip("When farming the Bowl of Embers (Extreme), leave and re-enter if the Infernal Nails spawn " +
            "instead of waiting out Ifrit's invulnerability. AutoDuty re-queues automatically.\n\n" +
            "Turn off to fight the nails instead — for a job or gear that cannot burst him down first.");

        ImGui.Separator();
        ImGui.TextDisabled("Novus materia");
        var weapon = (int)_config.NovusWeapon;
        if (ImGui.Combo("Novus weapon", ref weapon, "Standard\0Healer\0Paladin (Curtana + Holy Shield)\0"))
        {
            _config.NovusWeapon = (NovusWeaponProfile)weapon;
            // A different weapon is a different scroll (or two) with different caps; drop all
            // per-scroll progress so it does not carry across (it is persisted otherwise).
            _config.ScrollProgressByScroll.Clear();
            _dirty = true;
        }
        var scope = (int)_config.MarketScope;
        if (ImGui.Combo("Universalis scope", ref scope, "World\0Data Center\0Region\0"))
        {
            _config.MarketScope = (UniversalisScope)scope;
            _dirty = true;
        }
        var maxStats = _config.MaxMateriaStats;
        if (ImGui.SliderInt("Max materia stats", ref maxStats, 2, 7))
        {
            _config.MaxMateriaStats = maxStats;
            _dirty = true;
        }
        Checkbox("Pull materia from retainers", _config.AutoWithdrawFromRetainers,
            v => _config.AutoWithdrawFromRetainers = v);
        Ui.Tooltip("'Fetch from Retainer' in the Novus planner drives the summoning bell itself, cycling " +
            "through every retainer and pulling the route's materia into your bags.\n\n" +
            "Turn off to only list what to withdraw.");

        ImGui.Separator();
        ImGui.TextDisabled("Diagnostics");
        Checkbox("Enable debug log", _config.EnableDebugLog, v =>
        {
            _config.EnableDebugLog = v;
            Diagnostics.DebugLog.Enabled = v; // apply live
        });

        ImGui.Separator();
        if (ImGui.Button("Save"))
        {
            _pi.SavePluginConfig(_config);
            _dirty = false;
        }
        ImGui.SameLine();
        ImGui.TextDisabled(_dirty ? "changes pending..." : "settings save automatically");

        // Debounced auto-save: flush once the user is done interacting (no widget
        // active), so drags/typing do not write the JSON every frame.
        if (_dirty && !ImGui.IsAnyItemActive())
        {
            _pi.SavePluginConfig(_config);
            _dirty = false;
        }
    }

    // True when a preset name looks like an AI/movement preset rather than a rotation-only one
    // -- the known-bad choice for the BossMod Reborn COMBAT preset (it makes BMR pick its own
    // targets and drive movement, so neutral relic mobs are never pulled). "VBM Multibox" is
    // BMR's shipped AI preset; reusing the avoidance preset name here is the same mistake.
    private bool LooksLikeAiPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return false;
        if (presetName.Contains("Multibox", System.StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(presetName, _config.BossModRebornAvoidancePreset, System.StringComparison.OrdinalIgnoreCase);
    }

    private void DrawDependencies()
    {
        ImGui.TextWrapped("Status of the plugins Relicable drives. Required items must be installed AND expose their IPC before automation will start.");
        ImGui.Spacing();

        if (ImGui.BeginTable("relicable_deps", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Install", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var dep in _dependencies.Evaluate())
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(dep.Name);
                ImGui.SameLine();
                ImGui.TextColored(dep.Required ? Yellow : Grey, dep.Required ? "(required)" : "(optional)");

                ImGui.TableNextColumn();
                DrawStatusCell(dep);

                ImGui.TableNextColumn();
                DrawInstallCell(dep);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Repo links open the project page. Copy adds the Dalamud custom-repository URL to your clipboard; paste it in /xlsettings > Experimental > Custom Plugin Repositories.");
    }

    private static void DrawStatusCell(DependencyStatus dep)
    {
        // The live IPC gate is the source of truth (internal names vary between
        // forks), so a live gate means Ready regardless of the installed-name match.
        if (dep.GateLive)
            ImGui.TextColored(Green, dep.Loaded ? $"Ready ({dep.Version})" : "Ready (IPC)");
        else if (dep.Loaded)
            ImGui.TextColored(Green, $"Loaded ({dep.Version})");
        else if (dep.Installed)
            ImGui.TextColored(Yellow, "Installed, off");
        else
            ImGui.TextColored(dep.Required ? Red : Grey, "Not found");
    }

    private void DrawInstallCell(DependencyStatus dep)
    {
        // Only offer install links when the plugin is neither live nor loaded.
        if (dep.GateLive || dep.Loaded)
        {
            ImGui.TextColored(Grey, "-");
            return;
        }

        if (ImGui.SmallButton($"GitHub##{dep.Name}"))
            Util.OpenLink(dep.GitHubUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy repo##{dep.Name}"))
            ImGui.SetClipboardText(dep.RepoUrl);
    }

    // Auto-properties cannot be passed to ImGui.Checkbox by ref, so use a local
    // and write back through the setter only when the value changes. Instance
    // method so a change also marks the config dirty for the debounced auto-save.
    private void Checkbox(string label, bool current, System.Action<bool> setter)
    {
        var value = current;
        if (ImGui.Checkbox(label, ref value) && value != current)
        {
            setter(value);
            _dirty = true;
        }
    }
}
