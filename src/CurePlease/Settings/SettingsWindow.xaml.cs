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
        private readonly CurePleaseSettings _clone;
        private readonly BardEditModel _bardModel;
        private readonly HealingEditModel _healingModel;
        private readonly DebuffsEditModel _debuffsModel;
        private readonly BuffsEditModel _buffsModel;
        private readonly SchEditModel _schModel;
        private readonly WhmEditModel _whmModel;
        private readonly PrioritiesEditModel _prioritiesModel;

        private readonly byte _comboMain;
        private readonly byte _comboSub;
        private readonly string _forcedPageKey;

        public SettingsWindow(SettingsService settings, ProfileService profiles, GlobalSettingsService globals,
            string initialPage = null)
        {
            InitializeComponent();

            _settings = settings;
            _profiles = profiles;
            _globals = globals;
            _clone = settings.Current.DeepClone();
            _bardModel = new BardEditModel(_clone.Bard);
            _healingModel = new HealingEditModel(_clone);
            _debuffsModel = new DebuffsEditModel(_clone);
            _buffsModel = new BuffsEditModel(_clone);
            _schModel = new SchEditModel(_clone.Sch);
            _whmModel = new WhmEditModel(_clone.Whm);
            _prioritiesModel = new PrioritiesEditModel(_clone.Priorities);

            bool comboKnown = ProfileService.TryComboFromProfileName(_profiles.ActiveName, out _comboMain, out _comboSub);
            string comboLabel = comboKnown
                ? ProfileService.JobAbbreviation(_comboMain)
                  + (_comboSub != 0 ? "/" + ProfileService.JobAbbreviation(_comboSub) : string.Empty)
                : null;

            ProfileNameText.Text = _profiles.ActiveName ?? "(unsaved settings)";
            if (comboLabel != null)
            {
                ProfileNameText.Text += "  —  " + comboLabel;
            }

            // The combo filter only exists when a combo can be read from the profile name.
            ComboFilterBox.Visibility = comboKnown ? Visibility.Visible : Visibility.Collapsed;
            if (comboKnown)
            {
                ComboFilterBox.Content = "Only job pages for " + comboLabel;
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
            _bardModel.Commit();
            _buffsModel.Commit();

            // Publish first so the profile file receives the edited clone, not the old settings.
            _settings.Adopt(_clone);

            try
            {
                if (!_profiles.SaveActive())
                {
                    // No profile file yet (e.g. fresh install before attach): save as default.
                    _profiles.SaveAs(ProfileService.DefaultProfileName);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Save failed: " + ex.Message;
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
