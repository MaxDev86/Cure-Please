using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CurePlease.Core;
using CurePlease.Core.Modules;
using CurePlease.Settings;

namespace CurePlease
{
    /// <summary>
    /// One attached game instance: the attach bar, profile selector, player state,
    /// party grid and engine log for a single PL character. MainWindow hosts one of
    /// these per tab; the AddonListener, GlobalSettingsService and PidRegistry are
    /// shared with the other tabs and injected by the shell.
    /// </summary>
    public partial class InstanceView : UserControl
    {
        private readonly GameStateService _state = new GameStateService();
        private readonly CastingService _casting;
        private readonly SettingsService _settings = new SettingsService();
        private readonly GlobalSettingsService _globals;
        private readonly ProfileService _profiles;
        private readonly Engine _engine;
        private readonly AddonListener _addon;
        private readonly PidRegistry _pids;
        private readonly BardModule _bard;

        /// <summary>Session-only per-member buff assignments (never saved to profiles).</summary>
        private readonly PlayerBuffAssignments _playerBuffs = new PlayerBuffAssignments();

        /// <summary>Session-only per-member enable selections (checked by default).</summary>
        private readonly PartyMemberSelections _partySelections = new PartyMemberSelections();

        /// <summary>Open Buffs popups by member name — one per member.</summary>
        private readonly Dictionary<string, PlayerBuffsPopup> _buffPopups = new Dictionary<string, PlayerBuffsPopup>(StringComparer.OrdinalIgnoreCase);
        private string _lastPlayerName = "";
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer;
        private PlayerJobsWindow _jobsWindow;
        private bool _profileComboGuard;
        private bool _addonLoadPending;
        private bool _attachmentStopHandled;
        private int _claimedPlPid;
        private byte _lastMainJob;
        private byte _lastSubJob;
        private byte _lastMainJobLevel;
        private byte _lastSubJobLevel;

        /// <summary>File logger tagged with this instance's character (once known).</summary>
        private Serilog.ILogger _log = Serilog.Log.ForContext("Instance", "(new)");

        // Job stats change rarely; re-read at most every 10 s or on job/character change.
        private List<JobStatusRow> _jobStats;
        private DateTime _jobStatsReadUtc = DateTime.MinValue;
        private string _jobStatsKey = string.Empty;

        /// <summary>Window-title text for this instance; the shell shows it when this tab is active.</summary>
        public string CurrentTitle { get; private set; } = "CurePlease3";

        /// <summary>Attached character name; null until the first good snapshot (or after detach).</summary>
        public string CharacterName => _lastPlayerName.Length > 0 ? _lastPlayerName : null;

        /// <summary>Active profile name (for the shell's cross-tab collision check).</summary>
        public string ActiveProfileName => _profiles?.ActiveName;

        /// <summary>The addon UDP port this instance's profile asks for.</summary>
        public int DesiredAddonPort => _settings.Current.Addon.Port;

        /// <summary>
        /// The port the shared addon listener is actually bound to (set by the shell).
        /// The in-game addon must push to the shared listener, not to a per-profile port.
        /// </summary>
        public int SharedAddonPort { get; set; }

        /// <summary>Title text changed (attach/detach/snapshot). Args: the new title.</summary>
        public event Action<string> TitleChanged;

        /// <summary>Attached character changed. Args: name, or null on detach.</summary>
        public event Action<string> CharacterChanged;

        /// <summary>The active profile switched (shell checks for cross-tab collisions).</summary>
        public event Action ProfileChanged;

        public InstanceView(AddonListener addon, GlobalSettingsService globals, PidRegistry pids)
        {
            InitializeComponent();

            _addon = addon;
            _globals = globals;
            _pids = pids;

            _casting = new CastingService(_state);
            _engine = new Engine(_state, _casting, _settings)
            {
                Addon = _addon,
                PartyMemberSelections = _partySelections,
            };

            _engine.RegisterModule(new CureModule(_state));
            _engine.RegisterModule(new CuragaModule(_state));
            _engine.RegisterModule(new DebuffModule(_state));
            _engine.RegisterModule(new BuffUpkeepModule(_state));
            _engine.RegisterModule(new PlayerBuffModule(_state, _playerBuffs));
            _bard = new BardModule(_state);
            _bard.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.RegisterModule(_bard);
            GeoModule geo = new GeoModule(_state);
            geo.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.RegisterModule(geo);
            _engine.RegisterModule(new SchModule(_state));
            _engine.RegisterModule(new WhmModule(_state));
            _engine.RegisterModule(new RdmModule(_state));

            // Profiles live next to the exe (<exe>\profiles), app-level UI prefs in
            // <exe>\global_settings.json. Startup: load the default profile; on first
            // run migrate the old %appdata% settings file into it so existing setups
            // keep working. Job-combo profiles (WHM_SCH.json, Charname_BRD_NIN.json,
            // ...) auto-switch on context changes unless the global sticky-profile
            // preference is off.
            _profiles = new ProfileService(_settings);
            _profiles.AutoSwitchEnabled = _globals.Current.AutoSwitchProfiles;
            AutoSwitchBox.IsChecked = _profiles.AutoSwitchEnabled;
            _profiles.ProfileSwitched += name => Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateActiveProfileLabel();
                AppendLog("Profile active: " + name);
                _log.Information("Profile active: {Profile}", name);
                ProfileChanged?.Invoke();
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
            _settings.SettingsChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateActionButton));
            UpdateActionButton();

            _engine.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.ZonePauseChanged += paused => Dispatcher.BeginInvoke(new Action(UpdateActionButton));
            _engine.SnapshotReady += snapshot => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (snapshot != null)
                {
                    _profiles.ConsiderSnapshot(snapshot);
                }

                UpdateSnapshot(snapshot);
            }));

            // File log gets the outcome detail the UI line omits (elapsed time).
            _engine.ActionExecuted += result => _log.Information(
                "Cast {Spell} -> {Target}: {Outcome} in {ElapsedMs:0} ms{Detail}",
                result.SpellName, result.TargetName, result.Outcome, result.Elapsed.TotalMilliseconds,
                result.Detail.Length > 0 ? " [" + result.Detail + "]" : string.Empty);

            _state.StateChanged += state => Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = state.ToString();
                _log.Information("Connection state: {State}{Error}", state,
                    state == ConnectionState.Lost && _state.LastError != null ? " (" + _state.LastError + ")" : string.Empty);
                JobInfoButton.Visibility = state == ConnectionState.Connected ? Visibility.Visible : Visibility.Collapsed;
                UpdateAttachmentControls();
                UpdateActionButton();
                if (state == ConnectionState.Detached)
                {
                    // Detach clears the tab identity; zoning/logout keep it.
                    _lastPlayerName = "";
                    _log = Serilog.Log.ForContext("Instance", "(new)");
                    CharacterChanged?.Invoke(null);
                    SetTitle("CurePlease3");
                    JobStatsText.Text = string.Empty;
                }
                else if (state != ConnectionState.Connected)
                {
                    SetTitle("CurePlease3");
                    JobStatsText.Text = string.Empty;
                }

                if (state == ConnectionState.Connected)
                {
                    // Once per attach: load the addon and push the SHARED listener's
                    // port (same order legacy used). An already-loaded addon just
                    // prints a harmless "already loaded" notice in the client console.
                    if (_addonLoadPending)
                    {
                        _addonLoadPending = false;
                        int port = SharedAddonPort > 0 ? SharedAddonPort : DesiredAddonPort;
                        _state.SendCommand("//lua load CurePlease3");
                        _state.SendCommand("//cp3 settings 127.0.0.1 " + port);
                        AppendLog("Sent addon load (CurePlease3, port " + port + ")");
                    }

                    // Ask the addon to confirm itself (harmless when not loaded).
                    _state.SendCommand("//cp3 verify");
                }
            }));

            // Display polling when the engine is not running: keeps status/party view
            // live after Attach without requiring Start. The engine owns snapshots
            // once started, so this timer skips those periods to avoid double reads.
            _previewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _previewTimer.Tick += (s, e) =>
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
            _previewTimer.Start();

            RefreshProcessLists();
        }

        // ----- shell-facing API -----

        /// <summary>Cast event routed from the shared addon listener (thread-safe — Interlocked inside).</summary>
        public void NotifyCastEvent(CastEventKind kind, int spellId)
        {
            _casting.NotifyCastEvent(kind, spellId);
        }

        /// <summary>Spell-landed report routed from the shared addon listener (bard song tracking).</summary>
        public void HandleSpellLanded(int spellId, string[] recipients)
        {
            _bard.OnSpellLanded(spellId, recipients);
        }

        /// <summary>Thread-safe log line from the shell (addon events arrive on the UDP thread).</summary>
        public void PostLog(string message)
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
        }

        /// <summary>
        /// Stops the engine, unloads the addon from the client, detaches and
        /// releases the claimed process id. Called on tab close and app shutdown.
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                _previewTimer.Stop();
                await _engine.StopAsync();

                // Closing detaches, and detaching unloads the addon from the client.
                if (_state.IsAttached)
                {
                    _state.SendCommand("//lua unload CurePlease3");
                }

                _addonLoadPending = false;
                _state.Detach();
                if (_claimedPlPid != 0)
                {
                    _pids.Release(_claimedPlPid);
                    _claimedPlPid = 0;
                }
            }
            catch (Exception ex)
            {
                // Shutdown must not crash on a dead client — the process is exiting anyway.
                _log.Warning(ex, "Detach/cleanup during shutdown failed");
            }
        }

        // ----- attach / detach -----

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

        private void MonitoredPidBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAttachmentControls();
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (_state.IsAttached)
            {
                DetachFromGame();
                return;
            }

            if (!(PlPidBox.SelectedItem is GameProcessInfo pl))
            {
                AppendLog("Select the PL character first");
                return;
            }

            // Two tabs must never drive the same character.
            if (!_pids.TryClaim(pl.Pid))
            {
                AppendLog("That process is already attached as PL in another tab");
                return;
            }

            try
            {
                _state.Attach(pl.Pid);
                _claimedPlPid = pl.Pid;
                _addonLoadPending = true; // fires on the first Connected state
                _playerBuffs.Clear();
            _partySelections.Clear(); // session assignments belong to the previous party
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
            }
            catch (Exception ex)
            {
                _addonLoadPending = false;
                _state.Detach();
                _pids.Release(pl.Pid);
                _claimedPlPid = 0;
                AppendLog("Attach failed: " + ex.Message);
            }

            UpdateAttachmentControls();
            UpdateActionButton();
        }

        private void MonitoredAttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_state.IsAttached)
            {
                AppendLog("Attach the PL character first");
                return;
            }

            int? selectedPid = (MonitoredPidBox.SelectedItem as GameProcessInfo)?.Pid;
            try
            {
                if (_state.IsMonitoredAttached
                    && (!selectedPid.HasValue || selectedPid.Value == _state.MonitoredProcessId))
                {
                    StopForAttachmentChange("monitored character detached");
                    _state.DetachMonitored();
                    AppendLog("Monitored character detached");
                }
                else if (selectedPid.HasValue)
                {
                    bool switching = _state.IsMonitoredAttached;
                    if (switching)
                    {
                        StopForAttachmentChange("monitored character switched");
                    }

                    _state.AttachMonitored(selectedPid.Value);
                    AppendLog((switching ? "Switched monitored character to " : "Monitored ")
                        + ((GameProcessInfo)MonitoredPidBox.SelectedItem).DisplayName);
                }
                else
                {
                    AppendLog("Select a monitored character first");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Monitored attach failed: " + ex.Message);
            }

            UpdateAttachmentControls();
        }

        private void StopForAttachmentChange(string reason)
        {
            if (_attachmentStopHandled)
            {
                return;
            }

            _attachmentStopHandled = true;
            if (_engine.IsRunning)
            {
                _engine.Stop();
                AppendLog("Engine stopped: " + reason);
            }
        }

        private void UpdateAttachmentControls()
        {
            bool plAttached = _state.IsAttached;
            bool monitoredAttached = _state.IsMonitoredAttached;
            if (plAttached && monitoredAttached)
            {
                _attachmentStopHandled = false;
            }
            else
            {
                StopForAttachmentChange("both PL and monitored characters must be attached");
            }

            AttachButton.Content = plAttached ? "Detach" : "Attach";
            PlPidBox.IsEnabled = !plAttached;

            int monitoredPid = _state.MonitoredProcessId;
            int? selectedPid = (MonitoredPidBox.SelectedItem as GameProcessInfo)?.Pid;
            MonitoredAttachButton.Content = monitoredAttached
                ? !selectedPid.HasValue || selectedPid.Value == monitoredPid ? "Detach" : "Switch"
                : "Attach";
            MonitoredAttachButton.IsEnabled = plAttached && (monitoredAttached || selectedPid.HasValue);
            MonitoredStatusText.Text = monitoredAttached ? "Attached (PID " + monitoredPid + ")" : "Detached";
            UpdateActionButton();
        }

        /// <summary>
        /// Unloads the addon (chat command needs a live connection; silently skipped
        /// when the client is gone — the addon dies with it anyway), stops the
        /// engine, detaches, and releases the process claim. Called by the Attach
        /// toggle and on tab/window close.
        /// </summary>
        private void DetachFromGame()
        {
            StopForAttachmentChange("PL character detached");

            if (_state.SendCommand("//lua unload CurePlease3"))
            {
                AppendLog("Sent addon unload (CurePlease3)");
            }

            _addonLoadPending = false;
            _state.Detach();
            if (_claimedPlPid != 0)
            {
                _pids.Release(_claimedPlPid);
                _claimedPlPid = 0;
            }

            _playerBuffs.Clear();
            _partySelections.Clear();
            UpdateAttachmentControls();
            UpdateActionButton();
            AppendLog("Detached");
        }

        // ----- buttons -----

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            bool? saved;
            try
            {
                var window = new SettingsWindow(_settings, _profiles, _globals,
                    suggestedProfileName: SuggestProfileName(), buffFilter: SuggestBuffFilter())
                { Owner = Window.GetWindow(this) };

                // Profile ops inside the dialog (Save As/Duplicate/Delete) change the active
                // profile even when the dialog is cancelled — always refresh.
                saved = window.ShowDialog();
            }
            catch (Exception ex)
            {
                AppendLog("Settings window failed: " + ex.Message);
                _log.Error(ex, "Settings window failed");
                saved = false;
            }

            UpdateActiveProfileLabel();
            if (saved == true)
            {
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

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
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
                bool saved = _profiles.SaveActive() || _profiles.SaveAs(ProfileService.DefaultProfileName);

                UpdateActiveProfileLabel();
                AppendLog(saved
                    ? "Saved to " + _profiles.ActivePath
                    : "WARNING: import is active in memory but could not be written to a profile");
            }
            catch (Exception ex)
            {
                AppendLog("Import failed: " + ex.Message);
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_state.IsAttached || !_state.IsMonitoredAttached || _state.State != ConnectionState.Connected)
            {
                AppendLog("Attach both PL and monitored characters, and wait for the PL to connect, before starting");
                UpdateActionButton();
                return;
            }

            bool resuming = _settings.Current.General.PauseActions || _engine.PausedByZone;
            if (!_engine.IsRunning)
            {
                SetActionsPaused(false);
                _engine.ResumeFromZone();
                _engine.Start();
                AppendLog(resuming ? "Engine started (actions resumed)" : "Engine started");
            }
            else if (resuming)
            {
                SetActionsPaused(false);
                _engine.ResumeFromZone();
                AppendLog("All actions resumed");
            }
            else
            {
                SetActionsPaused(true);
                AppendLog("All actions paused");
            }

            UpdateActionButton();
        }

        private void SetActionsPaused(bool paused)
        {
            if (_settings.Current.General.PauseActions == paused)
            {
                return;
            }

            CurePleaseSettings updated = _settings.Current.DeepClone();
            updated.General.PauseActions = paused;
            _settings.Adopt(updated);

            if (!_profiles.SaveActive() && !_profiles.SaveAs(ProfileService.DefaultProfileName))
            {
                AppendLog("WARNING: pause state could not be written to the profile file");
            }

            UpdateActiveProfileLabel();
        }

        // ----- snapshot display -----

        private void UpdateSnapshot(GameSnapshot snapshot)
        {
            UpdateAttachmentControls();
            if (snapshot == null)
            {
                JobStatsText.Text = string.Empty;
                _jobStatsKey = string.Empty;
                PartyGrid.ItemsSource = null;
                return;
            }

            PlayerState pl = snapshot.Player;
            if (!string.Equals(_lastPlayerName, pl.Name, StringComparison.Ordinal))
            {
                _lastPlayerName = pl.Name ?? "";
                _log = Serilog.Log.ForContext("Instance", CharacterName ?? "(new)");
                CharacterChanged?.Invoke(CharacterName);
            }

            _lastMainJob = pl.MainJob;
            _lastSubJob = pl.SubJob;
            _lastMainJobLevel = pl.MainJobLevel;
            _lastSubJobLevel = pl.SubJobLevel;

            string titleJob = ProfileService.JobAbbreviation(pl.MainJob) ?? pl.MainJob.ToString();
            string titleSub = ProfileService.JobAbbreviation(pl.SubJob);
            SetTitle("CurePlease3 - " + pl.Name + " " + titleJob + pl.MainJobLevel
                + (titleSub != null ? "/" + titleSub + pl.SubJobLevel : string.Empty));

            UpdateJobStats(pl);

            string followTarget = _settings.Current.General.FollowTargetName ?? string.Empty;
            PartyGrid.ItemsSource = snapshot.PartyMembers
                .Where(m => m.Active && m.Name.Length > 0)
                .GroupBy(m => m.ServerId) // stale duplicate slots can carry the same character twice
                .Select(g => g.First())
                .OrderBy(m => m.MemberNumber)
                .Select(m => new PartyRow(m, pl.Name, _playerBuffs, _partySelections, followTarget))
                .ToList();
        }

        private void SetTitle(string title)
        {
            CurrentTitle = title;
            TitleChanged?.Invoke(title);
        }

        /// <summary>
        /// "WHM99/SCH59 — Mastered (ML 50)" / "… — JP 231/2100" next to the player
        /// line. Reads are cached 10 s and re-done on character/job change; the
        /// More Info window reads fresh on open/Refresh instead.
        /// </summary>
        private void UpdateJobStats(PlayerState pl)
        {
            string key = pl.Name + "|" + pl.MainJob + "|" + pl.MainJobLevel;
            if (key != _jobStatsKey || DateTime.UtcNow - _jobStatsReadUtc > TimeSpan.FromSeconds(10))
            {
                _jobStatsKey = key;
                _jobStatsReadUtc = DateTime.UtcNow;
                _jobStats = _state.GetJobStatuses();
            }

            if (_jobStats == null)
            {
                JobStatsText.Text = string.Empty;
                return;
            }

            string text = ProfileService.JobAbbreviation(pl.MainJob) + pl.MainJobLevel
                + "/" + ProfileService.JobAbbreviation(pl.SubJob) + pl.SubJobLevel;

            JobStatusRow main = _jobStats.FirstOrDefault(j => j.JobId == pl.MainJob);
            if (pl.MainJobLevel == 99 && main != null)
            {
                text += main.Mastered
                    ? "  —  Mastered (ML " + main.MasterLevel + ")"
                    : "  —  JP " + main.SpentJobPoints + "/2100";
            }

            JobStatsText.Text = text;
        }

        private void JobInfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_jobsWindow != null && _jobsWindow.IsLoaded)
            {
                _jobsWindow.RefreshRows();
                _jobsWindow.Activate();
                return;
            }

            _jobsWindow = new PlayerJobsWindow(_state) { Owner = Window.GetWindow(this) };
            _jobsWindow.Closed += (s, args) => _jobsWindow = null;
            _jobsWindow.Show();
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
                try
                {
                    var window = new SettingsWindow(_settings, _profiles, _globals, "Buff Upkeep",
                        SuggestProfileName(), SuggestBuffFilter()) { Owner = Window.GetWindow(this) };
                    window.ShowDialog();
                }
                catch (Exception ex)
                {
                    AppendLog("Settings window failed: " + ex.Message);
                    _log.Error(ex, "Settings window failed");
                }

                UpdateActiveProfileLabel();
                return;
            }

            if (_buffPopups.TryGetValue(row.Name, out PlayerBuffsPopup open) && open.IsLoaded)
            {
                open.Activate();
                return;
            }

            var popup = new PlayerBuffsPopup(row.Name, _playerBuffs) { Owner = Window.GetWindow(this) };
            popup.Closed += (s, args) => _buffPopups.Remove(row.Name);
            _buffPopups[row.Name] = popup;
            popup.Show();
        }

        /// <summary>The PartyRow behind a grid cell button.</summary>
        private static PartyRow ButtonRow(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as PartyRow;
        }

        // ----- follow (party row button) -----

        /// <summary>Party row Follow/Stop button: sets or clears the follow target (persisted in the active profile). The PL cannot follow itself.</summary>
        private void FollowButton_Click(object sender, RoutedEventArgs e)
        {
            PartyRow row = ButtonRow(sender);
            if (row == null || row.IsSelf)
            {
                return;
            }

            string current = _settings.Current.General.FollowTargetName;
            bool clearing = current.Length > 0 && row.Name.Equals(current, StringComparison.OrdinalIgnoreCase);
            CurePleaseSettings updated = _settings.Current.DeepClone();
            updated.General.FollowTargetName = clearing ? string.Empty : row.Name;
            _settings.Adopt(updated);
            PersistQuietly();
            AppendLog(clearing ? "Follow target cleared" : "Follow target: " + row.Name);
        }

        /// <summary>Saves the active profile after a quick settings change; warns in the log on failure.</summary>
        private void PersistQuietly()
        {
            if (!_profiles.SaveActive() && !_profiles.SaveAs(ProfileService.DefaultProfileName))
            {
                AppendLog("WARNING: settings change could not be written to the profile file");
            }
        }

        // ----- profile row -----

        private void AutoSwitchBox_Toggled(object sender, RoutedEventArgs e)
        {
            _profiles.AutoSwitchEnabled = AutoSwitchBox.IsChecked == true;
            _globals.Current.AutoSwitchProfiles = _profiles.AutoSwitchEnabled;
            _globals.Save();
        }

        private void UpdateActiveProfileLabel()
        {
            _profileComboGuard = true;
            try
            {
                var names = _profiles.ListProfiles();
                string active = _profiles.ActiveName;
                ActiveProfileBox.ItemsSource = names;
                ActiveProfileBox.SelectedItem = active == null
                    ? null
                    : names.FirstOrDefault(n => n.Equals(active, StringComparison.OrdinalIgnoreCase));
                ActiveProfileBox.Text = active ?? "(none)";
            }
            finally
            {
                _profileComboGuard = false;
            }
        }

        private void ActiveProfileBox_DropDownOpened(object sender, EventArgs e)
        {
            // Pick up profiles created/duplicated/deleted while the window was open.
            UpdateActiveProfileLabel();
        }

        private void ActiveProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_profileComboGuard || !(ActiveProfileBox.SelectedItem is string name))
            {
                return;
            }

            if (_profiles.ActiveName != null && name.Equals(_profiles.ActiveName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // SwitchTo fires ProfileSwitched, which refreshes the combo and logs.
            if (!_profiles.SwitchTo(name))
            {
                AppendLog("Profile load failed: " + name + " — keeping the current settings");
                UpdateActiveProfileLabel();
            }
        }

        /// <summary>"Charname_MAIN_SUB" from the last snapshot, for the settings window's Save As prompt.</summary>
        private string SuggestProfileName()
        {
            return _lastPlayerName.Length > 0 && _lastMainJob != 0
                ? ProfileService.ProfileNameFor(_lastPlayerName, _lastMainJob, _lastSubJob)
                : null;
        }

        /// <summary>Buff Upkeep filter preset from the attached character's job combo; null when detached.</summary>
        private BuffFilterPreset SuggestBuffFilter()
        {
            return _lastPlayerName.Length > 0 && _lastMainJob != 0
                ? new BuffFilterPreset
                {
                    MainJob = _lastMainJob,
                    MainLevel = _lastMainJobLevel,
                    SubJob = _lastSubJob,
                    SubLevel = _lastSubJobLevel,
                }
                : null;
        }

        private void UpdateActionButton()
        {
            bool plAttached = _state.IsAttached;
            bool monitoredAttached = _state.IsMonitoredAttached;
            bool running = plAttached && monitoredAttached && _engine.IsRunning;
            bool paused = running && (_settings.Current.General.PauseActions || _engine.PausedByZone);
            StartButton.Content = !running ? "Start" : paused ? "Resume" : "Pause";
            StartButton.Foreground = paused
                ? System.Windows.Media.Brushes.Red
                : System.Windows.SystemColors.ControlTextBrush;
            StartButton.IsEnabled = plAttached && monitoredAttached && _state.State == ConnectionState.Connected;
            StartButton.ToolTip = !plAttached
                ? "Attach a PL character first."
                : !monitoredAttached
                    ? "Attach a monitored character before starting or resuming."
                    : _state.State != ConnectionState.Connected
                        ? "Wait for the PL character to connect before starting or resuming."
                        : !running
                            ? "Start the decision engine with actions resumed. Either detach stops it."
                            : _engine.PausedByZone
                                ? "Resume actions after zoning."
                                : paused
                                    ? "Resume all actions."
                                    : "Pause new actions while status and party updates continue. Either detach stops the engine.";
        }

        private void AppendLog(string message)
        {
            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            LogBox.ScrollToEnd();

            // Mirror everything the UI log shows to the rolling file (quoted — log
            // lines are plain text, not message templates).
            _log.Information("{LogLine}", message);
        }

        private sealed class PartyRow
        {
            private readonly PartyMemberSelections _selections;

            public PartyRow(
                PartyMemberState m,
                string plName,
                PlayerBuffAssignments assignments,
                PartyMemberSelections selections,
                string followTargetName)
            {
                _selections = selections;
                MemberNumber = m.MemberNumber;
                PartyDisplay = "P" + (m.PartyIndex + 1);
                Name = m.Name;
                IsSelf = m.Name.Equals(plName, StringComparison.OrdinalIgnoreCase);
                CanFollow = !IsSelf;
                IsFollowTarget = !IsSelf && followTargetName.Length > 0
                    && m.Name.Equals(followTargetName, StringComparison.OrdinalIgnoreCase);
                FollowLabel = IsFollowTarget ? "Stop" : "Follow";
                FollowTooltip = IsSelf
                    ? "The PL cannot follow itself"
                    : IsFollowTarget ? "Stop following " + m.Name : "Follow " + m.Name;
                CurrentHP = m.CurrentHP;
                CurrentHPP = m.CurrentHPP + "%";
                CurrentMP = m.CurrentMP;
                CurrentTP = m.CurrentTP;
                HPP = m.CurrentHPP;
                MPP = m.CurrentMPP;
                HpText = m.CurrentHP.ToString("N0") + "  ·  " + m.CurrentHPP + "%";
                MpText = m.CurrentMP.ToString("N0") + "  ·  " + m.CurrentMPP + "%";
                string mainJob = ProfileService.JobAbbreviation(m.MainJob) ?? m.MainJob.ToString();
                string subJob = ProfileService.JobAbbreviation(m.SubJob);
                JobDisplay = mainJob + m.MainJobLevel
                    + (subJob != null ? "/" + subJob + m.SubJobLevel : string.Empty);
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
            public bool IsSelected
            {
                get => _selections.IsEnabled(Name);
                set => _selections.SetEnabled(Name, value);
            }
            public bool IsSelf { get; }
            public bool CanFollow { get; }
            public bool IsFollowTarget { get; }
            public string FollowLabel { get; }
            public string FollowTooltip { get; }
            public uint CurrentHP { get; }
            public string CurrentHPP { get; }
            public uint CurrentMP { get; }
            public uint CurrentTP { get; }
            public int HPP { get; }
            public int MPP { get; }
            public string HpText { get; }
            public string MpText { get; }
            public string JobDisplay { get; }
            public string BuffSummary { get; }
            public string BuffButtonTooltip { get; }
        }
    }
}
