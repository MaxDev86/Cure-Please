using System.Windows;

namespace CurePlease.Settings
{
    /// <summary>
    /// Single-field name prompt for profile file operations (Save As, Duplicate).
    /// The caller validates collisions; this only guarantees a non-empty name.
    /// </summary>
    public partial class ProfileNameDialog : Window
    {
        public ProfileNameDialog(string title, string prompt, string suggestion)
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            NameBox.Text = suggestion ?? string.Empty;
            NameBox.SelectAll();
            NameBox.Focus();
        }

        /// <summary>The entered profile name (without .json). Set when the dialog closes with OK.</summary>
        public string ProfileName { get; private set; }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0)
            {
                ErrorText.Text = "Enter a name.";
                return;
            }

            ProfileName = name;
            DialogResult = true;
            Close();
        }
    }
}
