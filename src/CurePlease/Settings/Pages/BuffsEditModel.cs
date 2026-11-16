using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CurePlease.Core;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the Buff Upkeep page: one row per buff GROUP (Protect, Shell,
    /// Bar (element), Enspell (Fire), ...) with a dropdown of the group's spells.
    /// Buff Upkeep covers the PL only — per-member party buffs get their own page.
    /// Rows write straight into the BuffEntry objects of the settings clone (only
    /// published on Save); Commit is a no-op kept for the SettingsWindow pattern.
    /// Gain, Boost and enspell rows are mutually exclusive across their rows
    /// (one Gain-*, one Boost-*, one enspell at a time).
    /// </summary>
    public sealed class BuffsEditModel
    {
        /// <summary>The settings clone being edited (Buffs section).</summary>
        public CurePleaseSettings Settings { get; }

        public ObservableCollection<BuffGroupEditModel> Rows { get; }

        public BuffsEditModel(CurePleaseSettings settings)
        {
            Settings = settings;
            settings.Buffs.EnsureSeeded();
            Rows = new ObservableCollection<BuffGroupEditModel>(
                BuffCatalog.Groups().Select(g => new BuffGroupEditModel(settings, g.Key, g.Value)));

            foreach (BuffGroupEditModel row in Rows)
            {
                string key = ExclusiveKeyFor(row);
                if (key != null)
                {
                    row.WireExclusivity(key, DisableSiblings);
                }
            }
        }

        private static string ExclusiveKeyFor(BuffGroupEditModel row)
        {
            if (row.Group.StartsWith("Gain (", System.StringComparison.Ordinal))
            {
                return "Gain";
            }

            if (row.Group.StartsWith("Boost (", System.StringComparison.Ordinal))
            {
                return "Boost";
            }

            // Single-target and party -ra bar rows grant the same status ids —
            // one bar-element row and one bar-status row total.
            if (row.Group == "Bar (element)" || row.Group == "Bar-ra (element)")
            {
                return "BarElement";
            }

            if (row.Group == "Bar (status)" || row.Group == "Bar-ra (status)")
            {
                return "BarStatus";
            }

            // Protect/Shell and their party -ra versions grant the same status ids.
            if (row.Group == "Protect" || row.Group == "Protectra")
            {
                return "Protect";
            }

            if (row.Group == "Shell" || row.Group == "Shellra")
            {
                return "Shell";
            }

            // Storm rows: one storm at a time (same idea as one Gain-*/Boost-*).
            if (row.Members.Any(m => m.Family == BuffFamily.Storm))
            {
                return "Storm";
            }

            // Enspell (element) rows plus Enlight/Endark share the enspell slot.
            if (row.Members.Any(m => m.Family == BuffFamily.Enspell))
            {
                return "Enspell";
            }

            return null;
        }

        private void DisableSiblings(string exclusiveKey, BuffGroupEditModel enabledRow)
        {
            foreach (BuffGroupEditModel row in Rows)
            {
                if (row != enabledRow && row.ExclusiveSetKey == exclusiveKey)
                {
                    row.DisableFromExclusivity();
                }
            }
        }

        public void Commit()
        {
            // Rows write through to the clone; nothing to flush.
        }
    }

    /// <summary>One buff group row. The selected spell's BuffEntry carries the state.</summary>
    public sealed class BuffGroupEditModel : INotifyPropertyChanged
    {
        private readonly CurePleaseSettings _settings;
        private readonly List<BuffCatalogEntry> _members;
        private System.Action<string, BuffGroupEditModel> _exclusiveDisable;

        public BuffGroupEditModel(CurePleaseSettings settings, string group, List<BuffCatalogEntry> members)
        {
            _settings = settings;
            _members = members;
            Group = group;
            Spells = members.Select(m => m.Spell).ToList();
        }

        public string Group { get; }

        public IReadOnlyList<string> Spells { get; }

        /// <summary>Catalog entries behind this row (exposed for the exclusivity wiring).</summary>
        public IReadOnlyList<BuffCatalogEntry> Members => _members;

        /// <summary>Non-null when this row shares a one-at-a-time slot with other rows ("Gain"/"Boost"/"Enspell").</summary>
        public string ExclusiveSetKey { get; private set; }

        internal void WireExclusivity(string key, System.Action<string, BuffGroupEditModel> disableSiblings)
        {
            ExclusiveSetKey = key;
            _exclusiveDisable = disableSiblings;
        }

        /// <summary>False for single-spell groups — the dropdown is disabled.</summary>
        public bool HasChoices => Spells.Count > 1;

        private BuffCatalogEntry CatalogOf(string spell) =>
            _members.First(m => m.Spell == spell);

        private BuffEntry EntryOf(string spell) =>
            _settings.Buffs.Entries.First(e => e.Spell.Equals(spell, System.StringComparison.OrdinalIgnoreCase));

        private BuffEntry CurrentEntry => EntryOf(SelectedSpell);

        /// <summary>The chosen spell: the stored group selection, else the enabled member, else the first member.</summary>
        public string SelectedSpell
        {
            get
            {
                if (_settings.Buffs.GroupSelection.TryGetValue(Group, out string chosen)
                    && _members.Any(m => m.Spell == chosen))
                {
                    return chosen;
                }

                BuffEntry enabled = _settings.Buffs.Entries.FirstOrDefault(
                    e => e.Enabled && _members.Any(m => m.Spell == e.Spell));
                return enabled != null ? enabled.Spell : _members[0].Spell;
            }
            set
            {
                if (value == null || value == SelectedSpell || !_members.Any(m => m.Spell == value))
                {
                    return;
                }

                BuffEntry oldEntry = CurrentEntry;
                BuffEntry newEntry = EntryOf(value);

                // The group's state travels with the selection; duration resets to the
                // new spell's stock value.
                newEntry.Enabled = oldEntry.Enabled;
                newEntry.UseAccession = oldEntry.UseAccession && CatalogOf(value).AccessionCompatible;
                newEntry.UsePerpetuance = oldEntry.UsePerpetuance && CatalogOf(value).PerpetuanceCompatible;
                newEntry.DurationMinutes = CatalogOf(value).DefaultMinutes;
                oldEntry.Enabled = false;
                oldEntry.UseAccession = false;
                oldEntry.UsePerpetuance = false;
                _settings.Buffs.GroupSelection[Group] = value;

                Notify(nameof(SelectedSpell));
                Notify(nameof(Enabled));
                Notify(nameof(DurationMinutes));
                Notify(nameof(UseAccession));
                Notify(nameof(AccessionCompatible));
                Notify(nameof(UsePerpetuance));
                Notify(nameof(PerpetuanceCompatible));
                Notify(nameof(Notes));
            }
        }

        public bool Enabled
        {
            get => CurrentEntry.Enabled;
            set
            {
                // Defensive: only one member of a group is ever enabled.
                foreach (BuffCatalogEntry member in _members)
                {
                    EntryOf(member.Spell).Enabled = false;
                }

                CurrentEntry.Enabled = value;
                Notify(nameof(Enabled));

                if (value && ExclusiveSetKey != null)
                {
                    _exclusiveDisable?.Invoke(ExclusiveSetKey, this);
                }
            }
        }

        /// <summary>Called by the edit model when a sibling row in the same exclusive set was enabled.</summary>
        internal void DisableFromExclusivity()
        {
            if (!CurrentEntry.Enabled)
            {
                return;
            }

            CurrentEntry.Enabled = false;
            Notify(nameof(Enabled));
        }

        public double DurationMinutes
        {
            get => CurrentEntry.DurationMinutes;
            set
            {
                CurrentEntry.DurationMinutes = value;
                Notify(nameof(DurationMinutes));
            }
        }

        public bool UseAccession
        {
            get => CurrentEntry.UseAccession;
            set
            {
                CurrentEntry.UseAccession = value;
                Notify(nameof(UseAccession));
            }
        }

        /// <summary>Accession checkbox only enabled for spells on SCH's Accession list.</summary>
        public bool AccessionCompatible => CatalogOf(SelectedSpell).AccessionCompatible;

        public bool UsePerpetuance
        {
            get => CurrentEntry.UsePerpetuance;
            set
            {
                CurrentEntry.UsePerpetuance = value;
                Notify(nameof(UsePerpetuance));
            }
        }

        /// <summary>Perpetuance checkbox only enabled for spells an SCH main can cast.</summary>
        public bool PerpetuanceCompatible => CatalogOf(SelectedSpell).PerpetuanceCompatible;

        public string Notes => CatalogOf(SelectedSpell).Notes ?? string.Empty;

        /// <summary>Mutual-exclusion label (bar-element/bar-status/enspell/storm), empty otherwise.</summary>
        public string FamilyLabel
        {
            get
            {
                switch (CatalogOf(SelectedSpell).Family)
                {
                    case BuffFamily.BarElement: return "one bar-element";
                    case BuffFamily.BarStatus: return "one bar-status";
                    case BuffFamily.Enspell: return "one enspell";
                    case BuffFamily.Storm: return "one storm";
                    default: return string.Empty;
                }
            }
        }

        private void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
