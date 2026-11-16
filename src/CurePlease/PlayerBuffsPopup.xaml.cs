using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CurePlease.Core;

namespace CurePlease
{
    /// <summary>
    /// Session buff checklist for one party/alliance member, opened from the main
    /// window's per-row Buffs button. Edits the live <see cref="PlayerBuffAssignments"/>
    /// store directly — there is no Save; assignments are runtime state.
    /// Only party-targetable catalog buffs are listed (BuffCatalog.PartyTargetable),
    /// grouped by the catalog's display groups.
    /// </summary>
    public partial class PlayerBuffsPopup : Window
    {
        private readonly string _memberName;
        private readonly PlayerBuffAssignments _assignments;

        public string MemberName => _memberName;

        public PlayerBuffsPopup(string memberName, PlayerBuffAssignments assignments)
        {
            InitializeComponent();

            _memberName = memberName;
            _assignments = assignments;
            Title = "Buffs — " + memberName;
            HeaderText.Text = memberName;

            BuildList();
        }

        private void BuildList()
        {
            foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.List<BuffCatalogEntry>> group in BuffCatalog.Groups())
            {
                StackPanel groupPanel = null;
                foreach (BuffCatalogEntry entry in group.Value)
                {
                    if (!entry.PartyTargetable)
                    {
                        continue;
                    }

                    if (groupPanel == null)
                    {
                        groupPanel = new StackPanel();
                        groupPanel.Children.Add(new TextBlock
                        {
                            Text = group.Key,
                            FontWeight = FontWeights.Bold,
                            Margin = new Thickness(0, 6, 0, 2),
                        });
                        BuffList.Items.Add(groupPanel);
                    }

                    var box = new CheckBox
                    {
                        Content = entry.Spell + (entry.Notes != null ? "  (" + entry.Notes + ")" : string.Empty),
                        IsChecked = _assignments.GetSpells(_memberName).Contains(entry.Spell),
                        Margin = new Thickness(12, 1, 0, 1),
                    };
                    string spell = entry.Spell;
                    box.Checked += (s, e) => _assignments.SetSpell(_memberName, spell, true);
                    box.Unchecked += (s, e) => _assignments.SetSpell(_memberName, spell, false);
                    groupPanel.Children.Add(box);
                }
            }
        }
    }
}
