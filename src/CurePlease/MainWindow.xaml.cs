using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CurePlease.Core;
using CurePlease.Settings;

namespace CurePlease
{
    /// <summary>
    /// Tab shell: one <see cref="InstanceView"/> per attached game process.
    /// Owns the resources shared across instances — the single UDP addon listener
    /// (messages are character-name-keyed), the global UI preferences, and the
    /// PID registry that stops two tabs from driving the same character.
    ///
    /// Tab strip behaves like a browser's: a trailing "+" pseudo-tab opens a new
    /// instance; the first instance can never be closed (no ✕ on its header);
    /// every later tab closes from its own header button.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AddonListener _addon = new AddonListener();
        private readonly GlobalSettingsService _globals = new GlobalSettingsService();
        private readonly PidRegistry _pids = new PidRegistry();
        private readonly List<InstanceView> _views = new List<InstanceView>();

        /// <summary>Profile names currently flagged as open in more than one tab (warn once per episode).</summary>
        private readonly HashSet<string> _collisionWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private TabItem _addTab;
        private bool _switching;
        private bool _shutdownComplete;
        private int _nextTabNumber = 1;
        private int _addonPort;

        public MainWindow()
        {
            InitializeComponent();

            _globals.Load();

            // One listener feeds every tab; routing by character name happens here.
            _addon.CastEvent += OnAddonCastEvent;
            _addon.SpellLanded += OnAddonSpellLanded;
            _addon.Handshake += OnAddonHandshake;
            _addon.Error += OnAddonError;

            AddTab();
            _addTab = new TabItem { Header = "+", ToolTip = "Open another FFXI instance" };
            InstanceTabs.Items.Add(_addTab);

            Closing += OnClosing;
        }

        // ----- tabs -----

        private void AddTab()
        {
            string label = "Instance " + _nextTabNumber++;
            var view = new InstanceView(_addon, _globals, _pids);

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var headerText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(headerText);
            var tab = new TabItem { Header = header, Content = view };

            // Only the first instance is permanent; later tabs close themselves.
            if (_views.Count > 0)
            {
                var close = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Detach and close this instance",
                };
                close.Click += async (s, e) =>
                {
                    e.Handled = true;
                    await CloseTabAsync(tab);
                };
                header.Children.Add(close);
            }

            view.TitleChanged += title =>
            {
                if (ReferenceEquals(InstanceTabs.SelectedContent, view))
                {
                    Title = title;
                }
            };
            view.CharacterChanged += name => headerText.Text = name ?? label;
            view.ProfileChanged += CheckProfileCollisions;

            lock (_views)
            {
                _views.Add(view);
            }

            int insertAt = _addTab != null ? InstanceTabs.Items.IndexOf(_addTab) : InstanceTabs.Items.Count;
            InstanceTabs.Items.Insert(insertAt, tab);
            InstanceTabs.SelectedItem = tab;

            if (_addonPort == 0)
            {
                // The first tab's profile picks the shared listener port.
                _addonPort = view.DesiredAddonPort;
                _addon.Start(_addonPort);
            }
            else if (view.DesiredAddonPort != _addonPort)
            {
                view.PostLog("Note: this profile asks for addon port " + view.DesiredAddonPort
                    + ", but the shared listener is bound to " + _addonPort + " — using " + _addonPort + ".");
            }

            view.SharedAddonPort = _addonPort;
            CheckProfileCollisions();
        }

        private async Task CloseTabAsync(TabItem tab)
        {
            if (!(tab.Content is InstanceView view))
            {
                return;
            }

            bool wasSelected = ReferenceEquals(InstanceTabs.SelectedItem, tab);
            _switching = true;
            try
            {
                lock (_views)
                {
                    _views.Remove(view);
                }

                // The first tab has no close button, so a closed tab is always at
                // index >= 1 and the previous slot is a real tab, never the "+".
                int index = InstanceTabs.Items.IndexOf(tab);
                InstanceTabs.Items.Remove(tab);
                if (wasSelected)
                {
                    InstanceTabs.SelectedItem = InstanceTabs.Items[Math.Max(0, index - 1)];
                }
            }
            finally
            {
                _switching = false;
            }

            if (InstanceTabs.SelectedItem is TabItem selected && selected.Content is InstanceView selectedView)
            {
                Title = selectedView.CurrentTitle;
            }
            else
            {
                Title = "CurePlease3";
            }

            try
            {
                await view.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Tab shutdown failed");
            }

            CheckProfileCollisions();
        }

        private void InstanceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_switching)
            {
                return;
            }

            if (ReferenceEquals(InstanceTabs.SelectedItem, _addTab))
            {
                AddTab(); // selects the new tab on return
                return;
            }

            if (InstanceTabs.SelectedItem is TabItem tab && tab.Content is InstanceView view)
            {
                Title = view.CurrentTitle;
            }
            else
            {
                Title = "CurePlease3";
            }
        }

        // ----- shared addon listener routing -----

        private void OnAddonCastEvent(string caster, CastEventKind kind)
        {
            // v2 events are caster-name-keyed: deliver only to the tab driving that
            // character. Bare v1 events (caster null) go to every tab.
            foreach (InstanceView view in SnapshotViews())
            {
                if (caster == null || caster.Equals(view.CharacterName, StringComparison.OrdinalIgnoreCase))
                {
                    view.NotifyCastEvent(kind);
                }
            }
        }

        private void OnAddonSpellLanded(string caster, int spellId, string[] recipients)
        {
            foreach (InstanceView view in SnapshotViews())
            {
                if (caster == null || caster.Equals(view.CharacterName, StringComparison.OrdinalIgnoreCase))
                {
                    view.HandleSpellLanded(spellId, recipients);
                }
            }
        }

        private void OnAddonHandshake(string name)
        {
            InstanceView[] views = SnapshotViews();
            InstanceView match = name != null
                ? views.FirstOrDefault(v => name.Equals(v.CharacterName, StringComparison.OrdinalIgnoreCase))
                : null;

            string message = "Addon handshake confirmed" + (name != null ? " (" + name + ")" : "");
            if (match != null)
            {
                match.PostLog(message);
            }
            else
            {
                foreach (InstanceView view in views)
                {
                    view.PostLog(message);
                }
            }
        }

        private void OnAddonError(string message)
        {
            Serilog.Log.Warning("Addon listener: {Error}", message);
            foreach (InstanceView view in SnapshotViews())
            {
                view.PostLog("Addon listener: " + message);
            }
        }

        /// <summary>Addon events arrive on the UDP thread while tabs come and go on the UI thread — iterate a copy.</summary>
        private InstanceView[] SnapshotViews()
        {
            lock (_views)
            {
                return _views.ToArray();
            }
        }

        // ----- profile collision warning -----

        private void CheckProfileCollisions()
        {
            InstanceView[] views = SnapshotViews();

            var duplicates = views
                .Where(v => v.ActiveProfileName != null)
                .GroupBy(v => v.ActiveProfileName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicates)
            {
                if (!_collisionWarned.Add(group.Key))
                {
                    continue;
                }

                foreach (InstanceView view in group)
                {
                    view.PostLog("WARNING: profile '" + group.Key + "' is open in another tab too —"
                        + " last save wins. Consider character-prefixed profiles (Charname_JOB_SUB).");
                }
            }

            // Names no longer duplicated may be re-warned if they collide again later.
            _collisionWarned.RemoveWhere(n => !duplicates.Any(g => g.Key.Equals(n, StringComparison.OrdinalIgnoreCase)));
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (_shutdownComplete)
            {
                return;
            }

            // Cancel-then-close: shutdown awaits (engine stop, addon unload, detach)
            // must finish before the window is allowed to die — without e.Cancel the
            // process exits at the first await and mid-detach state is left behind.
            e.Cancel = true;

            try
            {
                foreach (InstanceView view in SnapshotViews())
                {
                    try
                    {
                        await view.ShutdownAsync();
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Tab shutdown failed");
                    }
                }

                _addon.Dispose();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Window shutdown failed");
            }
            finally
            {
                _shutdownComplete = true;
                Close();
            }
        }
    }
}
