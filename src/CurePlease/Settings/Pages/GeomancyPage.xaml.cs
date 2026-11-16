using System.Windows;
using System.Windows.Controls;
using CurePlease.Core;
using CurePlease.Settings;

namespace CurePlease.Settings.Pages
{
    public partial class GeomancyPage : UserControl
    {
        /// <summary>True while RefreshEnmitySpells repopulates the dropdown (SelectionChanged fires with null).</summary>
        private bool _refreshing;

        public GeomancyPage()
        {
            InitializeComponent();
            Loaded += (s, e) => RefreshEnmitySpells();
        }

        private void EnmityFilterChanged(object sender, RoutedEventArgs e)
        {
            RefreshEnmitySpells();
        }

        /// <summary>Repopulates the hate-spell dropdown for the current mode + sub-job filter.</summary>
        private void RefreshEnmitySpells()
        {
            if (!(DataContext is GeoSettings geo))
            {
                return;
            }

            _refreshing = true;
            try
            {
                EnmitySpellCombo.ItemsSource = EnmitySpellCatalog.For(geo.EnmityCastOnEnemy, geo.EnmitySubJobId);
                EnmitySpellCombo.SelectedItem = null;
                EnmitySpellCombo.Text = geo.EnmitySpell;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void EnmitySpellChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || !(DataContext is GeoSettings geo))
            {
                return;
            }

            if (EnmitySpellCombo.SelectedItem is string spell)
            {
                geo.EnmitySpell = spell;
            }
            else if (!string.IsNullOrWhiteSpace(EnmitySpellCombo.Text))
            {
                geo.EnmitySpell = EnmitySpellCombo.Text.Trim();
            }
        }
    }
}
