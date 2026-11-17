using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Buff Upkeep page. The grid rows come from <see cref="BuffsEditModel.Rows"/>;
    /// this code-behind owns the filter row (job dropdowns, search, enabled-only)
    /// and applies it through the default collection view. Filters are view state
    /// only — they never touch the settings clone.
    /// </summary>
    public partial class BuffsPage : UserControl
    {
        private BuffsEditModel _model;
        private ICollectionView _view;
        private bool _wiring;

        public BuffsPage()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _model = e.NewValue as BuffsEditModel;
            if (_model == null)
            {
                return;
            }

            _wiring = true;
            var jobs = new List<string> { "Any" };
            for (byte i = 1; i <= 22; i++)
            {
                string abbr = ProfileService.JobAbbreviation(i);
                if (abbr != null)
                {
                    jobs.Add(abbr);
                }
            }

            MainJobBox.ItemsSource = jobs;
            SubJobBox.ItemsSource = jobs;
            MainJobBox.SelectedIndex = _model.FilterMainJob;
            SubJobBox.SelectedIndex = _model.FilterSubJob;
            MainLevelBox.Text = _model.FilterMainLevel.ToString();
            SubLevelBox.Text = _model.FilterSubLevel.ToString();
            _wiring = false;

            _view = CollectionViewSource.GetDefaultView(_model.Rows);
            _view.Filter = o => _model.RowVisible((BuffGroupEditModel)o);
            _view.Refresh();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_wiring || _model == null)
            {
                return;
            }

            _model.FilterMainJob = MainJobBox.SelectedIndex > 0 ? MainJobBox.SelectedIndex : 0;
            _model.FilterSubJob = SubJobBox.SelectedIndex > 0 ? SubJobBox.SelectedIndex : 0;
            _model.FilterMainLevel = ParseLevel(MainLevelBox.Text, 99);
            _model.FilterSubLevel = ParseLevel(SubLevelBox.Text, 49);
            _model.SearchText = SearchBox.Text.Trim();
            _model.EnabledOnly = EnabledOnlyBox.IsChecked == true;
            _model.NotifySpellListsChanged();
            _view?.Refresh();
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null)
            {
                return;
            }

            _wiring = true;
            MainJobBox.SelectedIndex = 0;
            SubJobBox.SelectedIndex = 0;
            MainLevelBox.Text = "99";
            SubLevelBox.Text = "49";
            SearchBox.Text = string.Empty;
            EnabledOnlyBox.IsChecked = false;
            _wiring = false;

            _model.FilterMainJob = 0;
            _model.FilterSubJob = 0;
            _model.FilterMainLevel = 99;
            _model.FilterSubLevel = 49;
            _model.SearchText = string.Empty;
            _model.EnabledOnly = false;
            _model.NotifySpellListsChanged();
            _view?.Refresh();
        }

        private static int ParseLevel(string text, int fallback)
        {
            if (!int.TryParse(text, out int level))
            {
                return fallback;
            }

            return level < 1 ? 1 : level > 99 ? 99 : level;
        }
    }
}
