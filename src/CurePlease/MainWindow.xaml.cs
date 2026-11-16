using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using CurePlease.Core;
using CurePlease.Core.Modules;
using CurePlease.Settings;

namespace CurePlease
{
    /// <summary>
    /// Minimal test shell for the engine vertical slice. Wires the services together,
    /// shows the party snapshot and the engine decision log. Will be replaced by the
    /// real MVVM UI later — nothing here is load-bearing for the core.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly GameStateService _state = new GameStateService();
        private readonly CastingService _casting;
        private readonly SettingsService _settings = new SettingsService();
        private readonly GlobalSettingsService _globals = new GlobalSettingsService();
        private readonly ProfileService _profiles;
        private readonly Engine _engine;
        private readonly AddonListener _addon = new AddonListener();

        /// <summary>Session-only per-member buff assignments (never saved to profiles).</summary>
        private readonly PlayerBuffAssignments _playerBuffs = new PlayerBuffAssignments();

        /// <summary>Open Buffs popups by member name — one per member.</summary>
        private readonly Dictionary<string, PlayerBuffsPopup> _buffPopups = new Dictionary<string, PlayerBuffsPopup>(StringComparer.OrdinalIgnoreCase);
        private string _lastPlayerName = "";

        public MainWindow()
        {
            InitializeComponent();

            _casting = new CastingService(_state);
            _engine = new Engine(_state, _casting, _settings) { Addon = _addon };

            _engine.RegisterModule(new CureModule(_state));
            _engine.RegisterModule(new CuragaModule(_state));
            _engine.RegisterModule(new DebuffModule(_state));
            _engine.RegisterModule(new BuffUpkeepModule(_state));
            _engine.RegisterModule(new PlayerBuffModule(_state, _playerBuffs));
            BardModule bard = new BardModule(_state);
            bard.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.RegisterModule(bard);
            GeoModule geo = new GeoModule(_state);
            geo.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.RegisterModule(geo);
            _engine.RegisterModule(new SchModule(_state));
            _engine.RegisterModule(new WhmModule(_state));
            _engine.RegisterModule(new RdmModule(_state));

            // Profiles live next to the exe (<exe>\profiles), app-level UI prefs in
            // <exe>\global_settings.json. Startup: load the default profile; on first
            // run migrate the old %appdata% settings file into it so existing setups
            // keep working. Job-combo profiles (WHM_SCH.json, Alternate_BRD_NIN.json,
            // ...) auto-switch on context changes unless the global sticky-profile
            // preference is off.
            _globals.Load();
            _profiles = new ProfileService(_settings);
            _profiles.AutoSwitchEnabled = _globals.Current.AutoSwitchProfiles;
            AutoSwitchBox.IsChecked = _profiles.AutoSwitchEnabled;
            _profiles.ProfileSwitched += name => Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateActiveProfileLabel();
                AppendLog("Profile active: " + name);
                Serilog.Log.Information("Profile active: {Profile}", name);
            }));
            _profiles.SwitchSuppressed += candidate => Dispatcher.BeginInvoke(new Action(() =>
                AppendLog("PROFILE AUTO-SWITCH OFF: staying on '" + (_profiles.ActiveName ?? "(none)")
                    + "'; job/character change matched '" + candidate + "'")));

            try
            {
                string defaultProfile = Path.Combine(_profiles.DirectoryPath, ProfileService.DefaultProfileName + ".json");
                if (File.Exists(defaultProfile))
                {
                    _profiles.SwitchTo(ProfileService.DefaultProfileName);
                }
                else if (File.Exists(SettingsService.DefaultPath))
                {
                    // One-time migration: the pre-profiles settings file becomes default.json.
                    _settings.Load(SettingsService.DefaultPath);
                    _profiles.SaveAs(ProfileService.DefaultProfileName);
                    AppendLog("Migrated " + SettingsService.DefaultPath + " to profile 'default'");
                }
                else
                {
                    // TEMPORARY: seed a default profile so there is something to edit.
                    // Later: nothing is preloaded or auto-generated — the user picks and
                    // loads a profile explicitly.
                    _profiles.SaveAs(ProfileService.DefaultProfileName);
                    AppendLog("Created default profile at " + defaultProfile);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Profile load failed (using defaults): " + ex.Message);
            }

            UpdateActiveProfileLabel();
            _settings.SettingsChanged += _ => Dispatcher.BeginInvoke(new Action(UpdatePauseButton));
            UpdatePauseButton();

            _addon.CastEvent += (caster, kind) =>
            {
                // v2 addon events are caster-name-keyed; ignore events from other attached
                // instances so a multi-boxed setup cannot cross-trigger cast states.
                if (caster == null || caster.Equals(_lastPlayerName, StringComparison.OrdinalIgnoreCase))
                    _casting.NotifyCastEvent(kind);
            };
            _addon.SpellLanded += (spellId, recipients) => bard.OnSpellLanded(recipients);
            _addon.Handshake += name => Dispatcher.BeginInvoke(new Action(() =>
                AppendLog("Addon handshake confirmed" + (name != null ? " (" + name + ")" : ""))));
            _addon.Error += message => Dispatcher.BeginInvoke(new Action(() => AppendLog("Addon listener: " + message)));
            _addon.Start(_settings.Current.Addon.Port);

            _engine.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.ZonePauseChanged += paused => Dispatcher.BeginInvoke(new Action(() =>
                StartButton.Content = paused ? "Resume" : (_engine.IsRunning ? "Stop" : "Start")));
            _engine.SnapshotReady += snapshot => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (snapshot != null)
                {
                    _profiles.ConsiderSnapshot(snapshot);
                }

                UpdateSnapshot(snapshot);
            }));

            // File log gets the outcome detail the UI line omits (elapsed time).
            _engine.ActionExecuted += result => Serilog.Log.Information(
                "Cast {Spell} -> {Target}: {Outcome} in {ElapsedMs:0} ms{Detail}",
                result.SpellName, result.TargetName, result.Outcome, result.Elapsed.TotalMilliseconds,
                result.Detail.Length > 0 ? " [" + result.Detail + "]" : string.Empty);

            _state.StateChanged += state => Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = state.ToString();
                Serilog.Log.Information("Connection state: {State}{Error}", state,
                    state == ConnectionState.Lost && _state.LastError != null ? " (" + _state.LastError + ")" : string.Empty);
                StartButton.IsEnabled = state == ConnectionState.Connected && !_engine.IsRunning;

                if (state == ConnectionState.Connected)
                {
                    // Ask the addon to confirm itself (harmless when not loaded).
                    _state.SendCommand("//cpnext verify");
                }
            }));

            // Display polling when the engine is not running: keeps status/party view
            // live after Attach without requiring Start. The engine owns snapshots
            // once started, so this timer skips those periods to avoid double reads.
            System.Windows.Threading.DispatcherTimer previewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            previewTimer.Tick += (s, e) =>
            {
                if (!_engine.IsRunning && _state.IsAttached)
                {
                    GameSnapshot snapshot = _state.TakeSnapshot();
                    if (snapshot != null)
                    {
                        _profiles.ConsiderSnapshot(snapshot);
                    }

                    UpdateSnapshot(snapshot);
                }
            };
            previewTimer.Start();

            RefreshProcessLists();

            Closing += OnClosing;
        }

        /// <summary>
        /// Repopulates the process combos with running FFXI instances, preserving the
        /// current selections by PID. Each combo gets its own list copy so their
        /// selections stay independent.
        /// </summary>
        private void RefreshProcessLists()
        {
            var processes = GameProcessLocator.FindGameProcesses();
            int? plPid = (PlPidBox.SelectedItem as GameProcessInfo)?.Pid;
            int? monitoredPid = (MonitoredPidBox.SelectedItem as GameProcessInfo)?.Pid;

            PlPidBox.ItemsSource = processes.ToList();
            MonitoredPidBox.ItemsSource = processes.ToList();

            PlPidBox.SelectedItem = processes.FirstOrDefault(p => p.Pid == plPid) ?? processes.FirstOrDefault();
            MonitoredPidBox.SelectedItem = processes.FirstOrDefault(p => p.Pid == monitoredPid) ?? processes.FirstOrDefault();
        }

        private void PidCombo_DropDownOpened(object sender, EventArgs e)
        {
            RefreshProcessLists();
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(PlPidBox.SelectedItem is GameProcessInfo pl))
            {
                AppendLog("Select the PL character first");
                return;
            }

            try
            {
                _state.Attach(pl.Pid);
                _playerBuffs.Clear(); // session assignments belong to the previous party
                AppendLog("Attached to " + pl.DisplayName);

                // Perpetuance multiplier pre-set from owned gear; a manual Scholar-page
                // choice wins (covers bracers on a storage slip, invisible to the scan).
                PerpetuanceGear.Tier detected = PerpetuanceGear.Detect(_state);
                if (detected != null && !_settings.Current.Sch.PerpetuanceMultiplierManual
                    && detected.Multiplier != _settings.Current.Sch.PerpetuanceMultiplier)
                {
                    CurePleaseSettings updated = _settings.Current.DeepClone();
                    updated.Sch.PerpetuanceMultiplier = detected.Multiplier;
                    _settings.Adopt(updated);
                    AppendLog("Perpetuance multiplier pre-set to " + detected.Multiplier.ToString("0.00")
                        + "x (found " + detected.Label + ")");
                }

                if (MonitoredPidBox.SelectedItem is GameProcessInfo monitored)
                {
                    _state.AttachMonitored(monitored.Pid);
                    AppendLog("Monitored " + monitored.DisplayName);
                }

                StartButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AppendLog("Attach failed: " + ex.Message);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow(_settings, _profiles, _globals) { Owner = this };
            if (window.ShowDialog() == true)
            {
                UpdateActiveProfileLabel();
                AppendLog("Settings saved to profile " + (_profiles.ActiveName ?? "(none)"));
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Legacy CurePlease settings (*.xml)|*.xml",
                Title = "Import legacy settings",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                LegacySettingsImporter.ImportResult result = _settings.ImportLegacy(dialog.FileName);
                AppendLog(string.Format("Imported {0}: {1} settings mapped, {2} legacy fields ignored.",
                    System.IO.Path.GetFileName(dialog.FileName), result.MappedCount, result.IgnoredCount));
                foreach (string warning in result.Warnings)
                {
                    AppendLog("  note: " + warning);
                }

                // Persist immediately — an import that only lives in memory is lost on exit.
                if (!_profiles.SaveActive())
                {
                    _profiles.SaveAs(ProfileService.DefaultProfileName);
                }

                UpdateActiveProfileLabel();
                AppendLog("Saved to " + _profiles.ActivePath);
            }
            catch (Exception ex)
            {
                AppendLog("Import failed: " + ex.Message);
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            CurePleaseSettings updated = _settings.Current.DeepClone();
            updated.General.PauseActions = !updated.General.PauseActions;
            _settings.Adopt(updated);

            if (!_profiles.SaveActive())
            {
                _profiles.SaveAs(ProfileService.DefaultProfileName);
            }

            UpdateActiveProfileLabel();
            AppendLog(updated.General.PauseActions ? "All actions paused" : "All actions resumed");
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.PausedByZone)
            {
                _engine.ResumeFromZone(); // ZonePauseChanged restores the Stop label
                return;
            }

            if (_engine.IsRunning)
            {
                _engine.Stop();
                StartButton.Content = "Start";
            }
            else
            {
                _engine.Start();
                StartButton.Content = "Stop";
            }
        }

        private void UpdateSnapshot(GameSnapshot snapshot)
        {
            if (snapshot == null)
            {
                PlayerText.Text = "—";
                PartyGrid.ItemsSource = null;
                return;
            }

            PlayerState pl = snapshot.Player;
            _lastPlayerName = pl.Name ?? "";
            PlayerText.Text = string.Format(
                "{0}  HP {1}/{2} ({3}%)  MP {4}/{5}  TP {6}  Job {7}/{8}  Zone {9}  Buffs: {10}",
                pl.Name, pl.HP, pl.HPMax, pl.HPP, pl.MP, pl.MPMax, pl.TP,
                pl.MainJob, pl.SubJob, pl.ZoneId,
                string.Join(",", pl.Buffs.Where(b => b > 0 && b != 255)));

            PartyGrid.ItemsSource = snapshot.PartyMembers
                .Where(m => m.Active && m.Name.Length > 0)
                .GroupBy(m => m.ServerId) // stale duplicate slots can carry the same character twice
                .Select(g => g.First())
                .OrderBy(m => m.MemberNumber)
                .Select(m => new PartyRow(m, pl.Name, _playerBuffs))
                .ToList();
        }

        /// <summary>
        /// Per-row Buffs button: the PL row deep-links to Settings → Buff Upkeep
        /// (self-buffs are profile settings; one owner per target), everyone else
        /// gets the session-assignment popup.
        /// </summary>
        private void PlayerBuffsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PartyRow row))
            {
                return;
            }

            if (row.IsSelf)
            {
                var window = new SettingsWindow(_settings, _profiles, _globals, "Buff Upkeep") { Owner = this };
                window.ShowDialog();
                return;
            }

            if (_buffPopups.TryGetValue(row.Name, out PlayerBuffsPopup open) && open.IsLoaded)
            {
                open.Activate();
                return;
            }

            var popup = new PlayerBuffsPopup(row.Name, _playerBuffs) { Owner = this };
            popup.Closed += (s, args) => _buffPopups.Remove(row.Name);
            _buffPopups[row.Name] = popup;
            popup.Show();
        }

        private void AutoSwitchBox_Toggled(object sender, RoutedEventArgs e)
        {
            _profiles.AutoSwitchEnabled = AutoSwitchBox.IsChecked == true;
            _globals.Current.AutoSwitchProfiles = _profiles.AutoSwitchEnabled;
            _globals.Save();
        }

        private void UpdateActiveProfileLabel()
        {
            ActiveProfileText.Text = _profiles.ActiveName ?? "(none)";
        }

        private void UpdatePauseButton()
        {
            bool paused = _settings.Current.General.PauseActions;
            PauseButton.Content = paused ? "Resume" : "Pause";
            PauseButton.Foreground = paused
                ? System.Windows.Media.Brushes.Red
                : System.Windows.SystemColors.ControlTextBrush;
            PauseButton.ToolTip = paused
                ? "Resume all actions."
                : "Pause all new actions without stopping status and party updates.";
        }

        private void AppendLog(string message)
        {
            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            LogBox.ScrollToEnd();

            // Mirror everything the UI log shows to the rolling file (quoted — log
            // lines are plain text, not message templates).
            Serilog.Log.Information("{LogLine}", message);
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            await _engine.StopAsync();
            _addon.Dispose();
            _state.Detach();
        }

        private sealed class PartyRow
        {
            public PartyRow(PartyMemberState m, string plName, PlayerBuffAssignments assignments)
            {
                MemberNumber = m.MemberNumber;
                PartyDisplay = "P" + (m.PartyIndex + 1);
                Name = m.Name;
                IsSelf = m.Name.Equals(plName, StringComparison.OrdinalIgnoreCase);
                CurrentHP = m.CurrentHP;
                CurrentHPP = m.CurrentHPP + "%";
                CurrentMP = m.CurrentMP;
                CurrentTP = m.CurrentTP;
                JobDisplay = m.MainJob + "/" + m.SubJob;
                BuffSummary = IsSelf
                    ? "via Settings → Buff Upkeep"
                    : string.Join(", ", assignments.GetSpells(m.Name));
                BuffButtonTooltip = IsSelf
                    ? "Self-buffs are profile settings — opens Settings → Buff Upkeep"
                    : "Session buffs for " + m.Name + " (not saved)";
            }

            public byte MemberNumber { get; }
            public string PartyDisplay { get; }
            public string Name { get; }
            public bool IsSelf { get; }
            public uint CurrentHP { get; }
            public string CurrentHPP { get; }
            public uint CurrentMP { get; }
            public uint CurrentTP { get; }
            public string JobDisplay { get; }
            public string BuffSummary { get; }
            public string BuffButtonTooltip { get; }
        }
    }
}
