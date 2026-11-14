using System;
using System.ComponentModel;
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
        private readonly Engine _engine;
        private readonly AddonListener _addon = new AddonListener();

        public MainWindow()
        {
            InitializeComponent();

            _casting = new CastingService(_state);
            _engine = new Engine(_state, _casting, _settings) { Addon = _addon };

            _engine.RegisterModule(new CureModule(_state));
            _engine.RegisterModule(new CuragaModule(_state));
            _engine.RegisterModule(new DebuffModule(_state));

            _addon.CastEvent += kind => _casting.NotifyCastEvent(kind);
            _addon.Handshake += () => Dispatcher.BeginInvoke(new Action(() => AppendLog("Addon handshake confirmed")));
            _addon.Error += message => Dispatcher.BeginInvoke(new Action(() => AppendLog("Addon listener: " + message)));
            _addon.Start(_settings.Current.Addon.Port);

            _engine.Log += message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            _engine.SnapshotReady += snapshot => Dispatcher.BeginInvoke(new Action(() => UpdateSnapshot(snapshot)));
            _state.StateChanged += state => Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = state.ToString();
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
                    UpdateSnapshot(_state.TakeSnapshot());
                }
            };
            previewTimer.Start();

            Closing += OnClosing;
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PlPidBox.Text, out int pid))
            {
                AppendLog("Invalid PL PID");
                return;
            }

            try
            {
                _state.Attach(pid);
                AppendLog("Attached to PID " + pid);

                if (int.TryParse(MonitoredPidBox.Text, out int monitoredPid))
                {
                    _state.AttachMonitored(monitoredPid);
                    AppendLog("Monitored PID " + monitoredPid);
                }

                StartButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AppendLog("Attach failed: " + ex.Message);
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
            }
            catch (Exception ex)
            {
                AppendLog("Import failed: " + ex.Message);
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
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
            PlayerText.Text = string.Format(
                "{0}  HP {1}/{2} ({3}%)  MP {4}/{5}  TP {6}  Job {7}/{8}  Zone {9}  Buffs: {10}",
                pl.Name, pl.HP, pl.HPMax, pl.HPP, pl.MP, pl.MPMax, pl.TP,
                pl.MainJob, pl.SubJob, pl.ZoneId,
                string.Join(",", pl.Buffs.Where(b => b > 0 && b != 255)));

            PartyGrid.ItemsSource = snapshot.PartyMembers
                .Where(m => m.Active)
                .Select(m => new PartyRow(m))
                .ToList();
        }

        private void AppendLog(string message)
        {
            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            await _engine.StopAsync();
            _addon.Dispose();
            _state.Detach();
        }

        private sealed class PartyRow
        {
            public PartyRow(PartyMemberState m)
            {
                MemberNumber = m.MemberNumber;
                Name = m.Name;
                CurrentHP = m.CurrentHP;
                CurrentHPP = m.CurrentHPP + "%";
                CurrentMP = m.CurrentMP;
                CurrentTP = m.CurrentTP;
                JobDisplay = m.MainJob + "/" + m.SubJob;
                Active = m.Active;
            }

            public byte MemberNumber { get; }
            public string Name { get; }
            public uint CurrentHP { get; }
            public string CurrentHPP { get; }
            public uint CurrentMP { get; }
            public uint CurrentTP { get; }
            public string JobDisplay { get; }
            public bool Active { get; }
        }
    }
}
