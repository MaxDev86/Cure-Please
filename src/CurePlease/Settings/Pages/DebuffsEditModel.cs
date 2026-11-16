using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CurePlease.Core.Modules;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the Debuff Removal page. The removal rows write through to
    /// the clone's name list on toggle, so nothing needs committing.
    /// </summary>
    public sealed class DebuffsEditModel
    {
        /// <summary>The settings clone being edited (Debuffs section).</summary>
        public CurePleaseSettings Settings { get; }

        public IReadOnlyList<DebuffRemovalRow> DebuffRemovals { get; }

        /// <summary>Dropdown contents (owned by the module so ids stay in sync).</summary>
        public IReadOnlyList<string> WakeSleepSpells => DebuffModule.WakeSleepSpells;
        public IReadOnlyList<string> SilenceItems => DebuffModule.SilenceItems;
        public IReadOnlyList<string> DoomItems => DebuffModule.DoomItems;

        public DebuffsEditModel(CurePleaseSettings settings)
        {
            Settings = settings;
            DebuffRemovals = DebuffModule.KnownRemovals
                .Select(r => new DebuffRemovalRow(r.Name, r.Spell, settings.Debuffs.EnabledRemovals))
                .ToList();
        }
    }

    /// <summary>One removable debuff; IsChecked mirrors membership in the settings name list.</summary>
    public sealed class DebuffRemovalRow : INotifyPropertyChanged
    {
        private readonly List<string> _enabledRemovals;

        public string Name { get; }
        public string Spell { get; }
        public string DisplayName => Name.Replace('_', ' ');

        public bool IsChecked
        {
            get => _enabledRemovals.Contains(Name);
            set
            {
                if (value == IsChecked)
                {
                    return;
                }

                if (value)
                {
                    _enabledRemovals.Add(Name);
                }
                else
                {
                    _enabledRemovals.Remove(Name);
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public DebuffRemovalRow(string name, string spell, List<string> enabledRemovals)
        {
            Name = name;
            Spell = spell;
            _enabledRemovals = enabledRemovals;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
