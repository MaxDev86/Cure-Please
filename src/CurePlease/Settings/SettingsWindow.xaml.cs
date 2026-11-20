using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CurePlease.Settings.Pages;

namespace CurePlease.Settings
{
    /// <summary>
    /// Nav-list + page settings dialog. Edits a deep copy of the live settings;
    /// Save commits the page models into the copy, publishes it through
    /// SettingsService (the engine picks it up on the next tick) and writes the
    /// active profile file. Cancel discards everything.
    ///
    /// The nav is code-built from <see cref="SettingsNavigation"/>: feature pages
    /// always show, job pages can be trimmed to the profile's main/sub combo via
    /// the ComboFilterBox (GlobalSettings.FilterJobPagesByCombo — off by default,
    /// nothing is ever hidden unless the user asks). A deep-linked page beats the
    /// filter.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsService _settings;
        private readonly ProfileService _profiles;
        private readonly GlobalSettingsService _globals;
        private readonly BuffFilterPreset _buffFilter;
        private CurePleaseSettings _clone;
        private BardEditModel _bardModel;
        private HealingEditModel _healingModel;
        private DebuffsEditModel _debuffsModel;
        private BuffsEditModel _buffsModel;
        private SchEditModel _schModel;
        private WhmEditModel _whmModel;
        private PrioritiesEditModel _prioritiesModel;

        private byte _comboMain;
        private byte _comboSub;
        private readonly string _forcedPageKey;
        private readonly string _suggestedProfileName;

        /// <summary>Baseline JSON captured at open (and refreshed by Save As); Cancel/X prompts when the edited clone differs.</summary>
        private string _originalJson;
        private bool _closeConfirmed;
        private bool _profileComboGuard;

        public SettingsWindow(SettingsService settings, ProfileService profiles, GlobalSettingsService globals,
            string initialPage = null, string suggestedProfileName = null, BuffFilterPreset buffFilter = null)
        {
            InitializeComponent();

            _settings = settings;
            _profiles = profiles;
            _globals = globals;
            _buffFilter = buffFilter;
            _suggestedProfileName = suggestedProfileName;
            _clone = settings.Current.DeepClone();
            _bardModel = new BardEditModel(_clone.Bard);
            _healingModel = new HealingEditModel(_clone);
            _debuffsModel = new DebuffsEditModel(_clone);
            _buffsModel = new BuffsEditModel(_clone);
            if (buffFilter != null)
            {
                _buffsModel.FilterMainJob = buffFilter.MainJob;
                _buffsModel.FilterMainLevel = buffFilter.MainLevel;
                _buffsModel.FilterSubJob = buffFilter.SubJob;
                _buffsModel.FilterSubLevel = buffFilter.SubLevel;
            }
            _schModel = new SchEditModel(_clone.Sch);
            _whmModel = new WhmEditModel(_clone.Whm);
            _prioritiesModel = new PrioritiesEditModel(_clone.Priorities);

            bool comboKnown = ProfileService.TryComboFromProfileName(_profiles.ActiveName, out _comboMain, out _comboSub);

            // Bake model normalization into the baseline so an untouched window is never "dirty".
            CommitEdits();
            _originalJson = SettingsService.SerializeForComparison(_clone);

            UpdateProfileLabel();

            // The combo filter only exists when a combo can be read from the profile name.
            ComboFilterBox.Visibility = comboKnown ? Visibility.Visible : Visibility.Collapsed;
            if (comboKnown)
            {
                ComboFilterBox.Content = "Only job pages for " + ComboLabel();
                ComboFilterBox.IsChecked = _globals.Current.FilterJobPagesByCombo;
            }

            // Deep-links beat the filter: force-include the target page this session.
            if (initialPage != null && comboKnown && _globals.Current.FilterJobPagesByCombo
                && !SettingsNavigation.FeaturePages.Contains(initialPage))
            {
                _forcedPageKey = initialPage;
            }

            BuildNav();

            if (initialPage != null)
            {
                SelectPage(initialPage);
            }

            if (NavList.SelectedItem == null)
            {
                SelectPage("General");
            }
        }

        private void BuildNav()
        {
            string selectedKey = (NavList.SelectedItem as ListBoxItem)?.Tag as string;

            NavList.Items.Clear();
            foreach (SettingsNavItem entry in SettingsNavigation.Build(
                         ComboFilterBox.IsChecked == true, _comboMain, _comboSub, _forcedPageKey))
            {
                NavList.Items.Add(new ListBoxItem
                {
                    Content = entry.Label,
                    Tag = entry.PageKey,
                    IsEnabled = !entry.IsHeader,
                    FontWeight = entry.IsHeader ? FontWeights.Bold : FontWeights.Normal,
                });
            }

            if (selectedKey != null)
            {
                SelectPage(selectedKey);
            }
        }

        private void SelectPage(string pageKey)
        {
            foreach (object entry in NavList.Items)
            {
                if (entry is ListBoxItem navItem && navItem.Tag as string == pageKey)
                {
                    NavList.SelectedItem = navItem;
                    return;
                }
            }
        }

        private void ComboFilterBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
            {
                return;
            }

            _globals.Current.FilterJobPagesByCombo = ComboFilterBox.IsChecked == true;
            _globals.Save();
            BuildNav();
        }

        private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Fires during InitializeComponent (initial selection) before the fields
            // and PageHost exist — the constructor selects the first page when ready.
            if (!IsInitialized || PageHost == null || _clone == null)
            {
                return;
            }

            if (!(NavList.SelectedItem is ListBoxItem item) || !(item.Tag is string pageKey))
            {
                return;
            }

            switch (pageKey)
            {
                case "Bard":
                    PageHost.Content = new BardPage { DataContext = _bardModel };
                    break;
                case "Healing":
                    PageHost.Content = new HealingPage { DataContext = _healingModel };
                    break;
                case "Priorities":
                    PageHost.Content = new PrioritiesPage { DataContext = _prioritiesModel };
                    break;
                case "Debuff Removal":
                    PageHost.Content = new DebuffsPage { DataContext = _debuffsModel };
                    break;
                case "Buff Upkeep":
                    PageHost.Content = new BuffsPage { DataContext = _buffsModel };
                    break;
                case "Scholar":
                    PageHost.Content = new ScholarPage { DataContext = _schModel };
                    break;
                case "White Mage":
                    PageHost.Content = new WhiteMagePage { DataContext = _whmModel };
                    break;
                case "Red Mage":
                    PageHost.Content = new RedMagePage { DataContext = _clone.Rdm };
                    break;
                case "Geomancy":
                    PageHost.Content = new GeomancyPage { DataContext = _clone.Geo };
                    break;
                default:
                    PageHost.Content = new GeneralPage { DataContext = _clone };
                    break;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_bardModel.ValidateGracePeriod())
            {
                StatusText.Text = _bardModel.GracePeriodError;
                return;
            }

            CommitEdits();

            // Publish a copy for the profile write; restore the prior state if it fails.
            CurePleaseSettings previous = _settings.Current;
            _settings.Adopt(_clone.DeepClone());

            // No profile file yet (e.g. fresh install before attach): save as default.
            if (!_profiles.SaveActive() && !_profiles.SaveAs(ProfileService.DefaultProfileName))
            {
                _settings.Adopt(previous);
                StatusText.Text = "Save failed — see logs (file locked or not writable?)";
                return;
            }

            DialogResult = true;
            Close();
        }

        /// <summary>Commits page models that buffer their edits (Bard list, Buff grid) into the clone.</summary>
        private void CommitEdits()
        {
            _bardModel.Commit();
            _buffsModel.Commit();
        }

        private string ComboLabel()
        {
            string main = ProfileService.JobAbbreviation(_comboMain);
            if (main == null)
            {
                return null;
            }

            string sub = ProfileService.JobAbbreviation(_comboSub);
            return sub == null ? main : main + "/" + sub;
        }

        private void UpdateProfileLabel()
        {
            _profileComboGuard = true;
            try
            {
                string active = _profiles.ActiveName;
                var names = _profiles.ListProfiles();
                ProfileComboBox.ItemsSource = names;
                ProfileComboBox.SelectedItem = active == null
                    ? null
                    : names.FirstOrDefault(n => n.Equals(active, StringComparison.OrdinalIgnoreCase));
                ProfileComboBox.Text = active ?? "(unsaved settings)";
                string combo = ComboLabel();
                ProfileHintText.Text = combo == null ? string.Empty : "— " + combo;
            }
            finally
            {
                _profileComboGuard = false;
            }
        }

        private void ProfileComboBox_DropDownOpened(object sender, EventArgs e)
        {
            UpdateProfileLabel();
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_profileComboGuard || !IsInitialized || !(ProfileComboBox.SelectedItem is string name)
                || name.Equals(_profiles.ActiveName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (HasUnsavedProfileEdits()
                && MessageBox.Show(this, "Load '" + name + "' and discard unsaved changes?",
                    "Cure Please — Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                UpdateProfileLabel();
                return;
            }

            if (!_profiles.SwitchTo(name))
            {
                StatusText.Text = "Profile load failed: " + name;
                UpdateProfileLabel();
                return;
            }

            ReloadEditorFromProfile(name);
        }

        private bool HasUnsavedProfileEdits()
        {
            CommitEdits();
            return _bardModel.GracePeriodError != null
                || SettingsService.SerializeForComparison(_clone) != _originalJson;
        }

        private void ReloadEditorFromProfile(string profileName)
        {
            _clone = _settings.Current.DeepClone();
            _bardModel = new BardEditModel(_clone.Bard);
            _healingModel = new HealingEditModel(_clone);
            _debuffsModel = new DebuffsEditModel(_clone);
            _buffsModel = new BuffsEditModel(_clone);
            if (_buffFilter != null)
            {
                _buffsModel.FilterMainJob = _buffFilter.MainJob;
                _buffsModel.FilterMainLevel = _buffFilter.MainLevel;
                _buffsModel.FilterSubJob = _buffFilter.SubJob;
                _buffsModel.FilterSubLevel = _buffFilter.SubLevel;
            }
            _schModel = new SchEditModel(_clone.Sch);
            _whmModel = new WhmEditModel(_clone.Whm);
            _prioritiesModel = new PrioritiesEditModel(_clone.Priorities);

            bool comboKnown = ProfileService.TryComboFromProfileName(profileName, out _comboMain, out _comboSub);
            ComboFilterBox.Visibility = comboKnown ? Visibility.Visible : Visibility.Collapsed;
            if (comboKnown)
            {
                ComboFilterBox.Content = "Only job pages for " + ComboLabel();
                ComboFilterBox.IsChecked = _globals.Current.FilterJobPagesByCombo;
            }

            CommitEdits();
            _originalJson = SettingsService.SerializeForComparison(_clone);
            UpdateProfileLabel();
            BuildNav();
            NavList_SelectionChanged(NavList, null);
            StatusText.Text = "Loaded " + profileName;
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ProfileNameDialog("Save profile as",
                "Save the current settings (including unsaved edits) as a new profile and switch to it:",
                SuggestName()) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string name = dialog.ProfileName;
            if (ProfileExists(name) && MessageBox.Show(this,
                    "A profile named '" + name + "' already exists. Overwrite it?",
                    "Save As", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!_bardModel.ValidateGracePeriod())
            {
                StatusText.Text = _bardModel.GracePeriodError;
                return;
            }

            CommitEdits();

            // Publish a COPY: the window keeps editing _clone, so the live settings
            // must not be the same object (bindings would mutate it mid-edit).
            CurePleaseSettings previous = _settings.Current;
            _settings.Adopt(_clone.DeepClone());
            if (!_profiles.SaveAs(name))
            {
                _settings.Adopt(previous);
                StatusText.Text = "Save As failed — see logs (file locked or not writable?)";
                return;
            }

            _originalJson = SettingsService.SerializeForComparison(_clone);
            UpdateProfileLabel();
            StatusText.Text = "Saved as " + name + " (now active)";
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            string source = _profiles.ActiveName;
            if (source == null)
            {
                StatusText.Text = "Nothing to duplicate — save first.";
                return;
            }

            var dialog = new ProfileNameDialog("Duplicate profile",
                "Copy the saved profile '" + source + "' (last saved state, not unsaved edits) as:",
                SuggestName()) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string name = dialog.ProfileName;
            if (ProfileExists(name))
            {
                StatusText.Text = "'" + name + "' already exists — pick another name.";
                return;
            }

            try
            {
                _profiles.Duplicate(source, name);
                StatusText.Text = "Duplicated to " + name + " (switch on the main window)";
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Profile duplicate failed: {Source} -> {Name}", source, name);
                StatusText.Text = "Duplicate failed: " + ex.Message;
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            string active = _profiles.ActiveName;
            if (active == null)
            {
                StatusText.Text = "No profile file to delete.";
                return;
            }

            if (MessageBox.Show(this,
                    "Delete profile '" + active + "' from disk? The loaded settings stay in memory; "
                    + "saving afterwards writes a new file.",
                    "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!_profiles.Delete(active))
            {
                StatusText.Text = "Delete failed — see logs (file locked or not writable?)";
                return;
            }

            UpdateProfileLabel();
            StatusText.Text = "Deleted " + active;
        }

        /// <summary>Charname_MAIN_SUB from the attached character when available; else the combo from the profile name.</summary>
        private string SuggestName()
        {
            if (!string.IsNullOrEmpty(_suggestedProfileName))
            {
                return _suggestedProfileName;
            }

            string combo = ProfileService.ComboNameFor(_comboMain, _comboSub);
            return combo ?? string.Empty;
        }

        private bool ProfileExists(string name)
        {
            foreach (string existing in _profiles.ListProfiles())
            {
                if (existing.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard())
            {
                return;
            }

            _closeConfirmed = true;
            DialogResult = false;
            Close();
        }

        /// <summary>True when it is safe to close: no edits, or the user confirmed discarding them.</summary>
        private bool ConfirmDiscard()
        {
            CommitEdits();
            if (SettingsService.SerializeForComparison(_clone) == _originalJson)
            {
                return true;
            }

            return MessageBox.Show(this, "Discard unsaved changes?", "Cure Please — Settings",
                       MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Save sets DialogResult=true; Cancel sets _closeConfirmed after its own prompt.
            if (DialogResult != true && !_closeConfirmed && !ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }
    }
}
