using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ImGuiNET;
using LMeter.ACT;
using LMeter.Config;
using LMeter.Helpers;
using LMeter.Meter;
using LMeter.Windows;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace LMeter
{
    public class PluginManager : IPluginDisposable
    {
        private readonly Vector2 _origin = ImGui.GetMainViewport().Size / 2f;
        private readonly Vector2 _configSize = new Vector2(550, 550);

        private ClientState _clientState;
        private DalamudPluginInterface _pluginInterface;
        private CommandManager _commandManager;
        private WindowSystem _windowSystem;
        private ConfigWindow _configRoot;
        private LMeterConfig _config;

        private readonly ImGuiWindowFlags _mainWindowFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoSavedSettings;

        public PluginManager(
            ClientState clientState,
            CommandManager commandManager,
            DalamudPluginInterface pluginInterface,
            LMeterConfig config)
        {
            _clientState = clientState;
            _commandManager = commandManager;
            _pluginInterface = pluginInterface;
            _config = config;

            _configRoot = new ConfigWindow("ConfigRoot", _origin, _configSize);
            _windowSystem = new WindowSystem("LMeter");
            _windowSystem.AddWindow(_configRoot);

            _commandManager.AddHandler("/lm", new CommandInfo(PluginCommand)
            {
                HelpMessage = "Opens the LMeter configuration window.\n"
                            + "/lm end → Ends current ACT Encounter.\n"
                            + "/lm clear → Clears all ACT encounter log data.\n"
                            + "/lm ct <number> → Toggles clickthrough status for the given profile.\n"
                            + "/lm toggle <number> [on|off] → Toggles visibility for the given profile.",
                ShowInHelp = true
            });

            _commandManager.AddHandler("/lmdps", new CommandInfo(DPSReportCommand)
            {
                HelpMessage = "Output current/selected encounter details via echo.\n"
                            + "/lmdps <channel> → Output current/selected encounter details to channel. (party/fc only)",
                ShowInHelp = true
            });

            _clientState.Logout += OnLogout;
            _pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            _pluginInterface.UiBuilder.Draw += Draw;
        }

        private void Draw()
        {
            if (_clientState.LocalPlayer == null || CharacterState.IsCharacterBusy())
            {
                return;
            }

            _windowSystem.Draw();

            _config.ACTConfig.TryReconnect();
            _config.ACTConfig.TryEndEncounter();

            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(ImGui.GetMainViewport().Size);
            if (ImGui.Begin("LMeter_Root", _mainWindowFlags))
            {
                foreach (var meter in _config.MeterList.Meters)
                {
                    meter.Draw(_origin);
                }
            }

            ImGui.End();
        }

        public void Clear()
        {
            Singletons.Get<ACTClient>().Clear();
            foreach (var meter in _config.MeterList.Meters)
            {
                meter.Clear();
            }
        }

        public void Edit(IConfigurable configItem)
        {
            _configRoot.PushConfig(configItem);
        }

        public void ConfigureMeter(MeterWindow meter)
        {
            if (!_configRoot.IsOpen)
            {
                this.OpenConfigUi();
                this.Edit(meter);
            }
        }

        private void OpenConfigUi()
        {
            if (!_configRoot.IsOpen)
            {
                _configRoot.PushConfig(_config);
            }
        }

        private void OnLogout(object? sender, EventArgs? args)
        {
            ConfigHelpers.SaveConfig();
        }

        private void PluginCommand(string command, string arguments)
        {
            switch (arguments)
            {
                case "end":
                    ACTClient.EndEncounter();
                    break;
                case "clear":
                    this.Clear();
                    break;
                case { } argument when argument.StartsWith("toggle"):
                    _config.MeterList.ToggleMeter(GetIntArg(argument) - 1, GetBoolArg(argument, 2));
                    break;
                case { } argument when argument.StartsWith("ct"):
                    _config.MeterList.ToggleClickThrough(GetIntArg(argument) - 1);
                    break;
                default:
                    this.ToggleWindow();
                    break;
            }
        }

        private void DPSReportCommand(string command, string arguments)
        {
            Dalamud.Logging.PluginLog.Debug("Running dps report");
            DPSReport(channel(arguments.Trim().Split(" ")[0]));
        }

        /// Return a supported channel from the given input, or null if the channel is not supported
        private string? channel(string input) => input switch
        {
            "fc" => "fc",
            "p" or "party" => "p",
            _ => null
        };

        private void DPSReport(string? channel)
        {
            // Get the first meter that is for DPS and visible (to support selecting different encounters),
            // or get the first meter that is for DPS if they're all hidden
            MeterWindow? meter =
                _config.MeterList.Meters.Find(
                    m => m.VisibilityConfig.IsVisible() && m.GeneralConfig.DataType == MeterDataType.Damage
                )
                ?? _config.MeterList.Meters.Find(m => m.GeneralConfig.DataType == MeterDataType.Damage);

            // Exit if no valid meter is found
            if (meter == null) return;

            // Get the selected event/encounter (current event if meter is hidden)
            ACTEvent? actEvent = ACTClient.GetEvent(meter.GetSelectedIndex());
            Encounter? encounter = actEvent?.Encounter;

            // Exit if no event/encounter is found
            if (actEvent == null || encounter == null) return;

            // Build channel string, prepare output string, prepare original macro content strings
            string channelCommand = channel != null ? $"/{channel}" : "/echo";
            string output = "";
            string original = "";

            // Build encounter info string
            string encounterInfo = encounter.GetFormattedString(
                "Encounter:   [title]   |   [duration]   |   [dps] group dps   |   [deaths] total deaths",
                "N"
            );

            // Append encounter info to output
            output += $"{channelCommand} {encounterInfo}\n";

            // Get the sorted combatants from the meter
            List<Combatant> combatants = meter.GetSortedCombatants(actEvent, meter.GeneralConfig.DataType);
            List<Combatant> limitedCombatants = combatants.GetRange(0, Math.Min(combatants.Count, 10));

            // Append combatant info to output for the first 10 combatants
            foreach ((Combatant combatant, int i) in limitedCombatants.Select((c, i) => (c, i)))
            {
                // Build combatant info string
                string combatantInfo = combatant.GetFormattedString(
                    $"{i + 1}. [[job]] [name]:   [damagetotal] damage   |   [dps] dps   |   [damagepct]   |   [deaths] deaths",
                    "N"
                );

                // Remove unknown job (easier than bothering to check job before adding it to info and I'm laaaaazy)
                if (combatantInfo.Contains(" [UKN]"))
                    combatantInfo = combatantInfo.Replace(" [UKN]", "");

                // Append combatant info to output
                output += $"{channelCommand} {combatantInfo}\n";
            }

            // Dalamud.Logging.PluginLog.Debug(output);

            try
            {
                unsafe {
                    // Get macro 99 from individual macros
                    RaptureMacroModule.Macro* macro = RaptureMacroModule.Instance->GetMacro(0, 99);

                    // Rebuild original macro text
                    for (int i = 0; i <= 14; i++)
                    {
                        string linebreak = i < 14 ? "\n" : "";
                        Utf8String* line = macro->Line[i];

                        if (line == null) break;

                        // Dalamud.Logging.PluginLog.Debug($"Original macro line {i + 1} content: \"{line->ToString()}\"");

                        original += $"{line->ToString()}{linebreak}";
                    }

                    // Dalamud.Logging.PluginLog.Debug(original);

                    // Set macro text and execute it
                    RaptureMacroModule.Instance->ReplaceMacroLines(macro, Utf8String.FromString(output));
                    RaptureShellModule.Instance->ExecuteMacro(macro);

                    // Restore original macro text
                    RaptureMacroModule.Instance->ReplaceMacroLines(macro, Utf8String.FromString(original));
                }
            }
            catch (Exception ex)
            {
                Dalamud.Logging.PluginLog.Error(ex.ToString());
            }
        }

        private static int GetIntArg(string argument)
        {
            string[] args = argument.Split(" ");
            return args.Length > 1 && int.TryParse(args[1], out int num) ? num : 0;
        }

        private static bool? GetBoolArg(string argument, int index = 1)
        {
            string[] args = argument.Split(" ");
            if (args.Length > index)
            {
                string arg = args[index].ToLower();
                return arg.Equals("on") ? true : (arg.Equals("off") ? false : null);
            }

            return null;
        }

        private void ToggleWindow()
        {
            if (_configRoot.IsOpen)
            {
                _configRoot.IsOpen = false;
            }
            else
            {
                _configRoot.PushConfig(_config);
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Don't modify order
                _pluginInterface.UiBuilder.Draw -= Draw;
                _pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
                _clientState.Logout -= OnLogout;
                _commandManager.RemoveHandler("/lm");
                _commandManager.RemoveHandler("/lmdps");
                _windowSystem.RemoveAllWindows();
            }
        }
    }
}
