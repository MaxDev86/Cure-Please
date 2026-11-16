using System.Windows.Controls;

namespace CurePlease.Settings.Pages
{
    public partial class BardPage : UserControl
    {
        private BardEditModel Model => (BardEditModel)DataContext;

        public BardPage()
        {
            InitializeComponent();
        }

        private void AddPartySong_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddTo(Model.PartySongs, PartySongPicker);
        }

        private void RemovePartySong_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveSelected(Model.PartySongs, PartySongsList);
        }

        private void MovePartySongUp_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Move(Model.PartySongs, PartySongsList, -1);
        }

        private void MovePartySongDown_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Move(Model.PartySongs, PartySongsList, 1);
        }

        private void AddDummySong_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddTo(Model.DummySongs, DummySongPicker);
        }

        private void RemoveDummySong_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveSelected(Model.DummySongs, DummySongsList);
        }

        private void AddInstrument_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddTo(Model.OwnedInstruments, InstrumentPicker);
        }

        private void RemoveInstrument_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveSelected(Model.OwnedInstruments, InstrumentsList);
        }

        private void AddEnemySong_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddTo(Model.EnemyDebuffSongs, EnemySongPicker);
        }

        private void RemoveEnemySong_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveSelected(Model.EnemyDebuffSongs, EnemySongsList);
        }

        private static void AddTo(System.Collections.ObjectModel.ObservableCollection<string> list, ComboBox picker)
        {
            string value = picker.Text?.Trim();
            if (value != null && value.Length > 0 && !list.Contains(value))
            {
                list.Add(value);
            }

            picker.Text = string.Empty;
        }

        private static void RemoveSelected(System.Collections.ObjectModel.ObservableCollection<string> list, ListBox box)
        {
            if (box.SelectedItem is string selected)
            {
                list.Remove(selected);
            }
        }

        private static void Move(System.Collections.ObjectModel.ObservableCollection<string> list, ListBox box, int direction)
        {
            int index = box.SelectedIndex;
            int target = index + direction;
            if (index < 0 || target < 0 || target >= list.Count)
            {
                return;
            }

            list.Move(index, target);
            box.SelectedIndex = target;
        }
    }
}
