using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the Priorities page. Weight rows and the member multipliers
    /// write straight through to the clone's PrioritySettings; the high-priority
    /// member list edits as one-name-per-line text (write-through on focus loss).
    /// Nothing to commit.
    /// </summary>
    public sealed class PrioritiesEditModel
    {
        /// <summary>The PrioritySettings clone being edited.</summary>
        public PrioritySettings Settings { get; }

        /// <summary>Base scores per action kind, highest default first.</summary>
        public ObservableCollection<WeightRow> Weights { get; }

        public PrioritiesEditModel(PrioritySettings settings)
        {
            Settings = settings;
            Weights = new ObservableCollection<WeightRow>
            {
                new WeightRow("Debuff removal", "x severity (0-1) x member multiplier",
                    () => settings.DebuffWeight, v => settings.DebuffWeight = v),
                new WeightRow("Single-target cure", "x urgency (HP missing vs. cure potency) x member multiplier",
                    () => settings.CureWeight, v => settings.CureWeight = v),
                new WeightRow("Curaga", "x average urgency x members covered (capped at 2)",
                    () => settings.CuragaWeight, v => settings.CuragaWeight = v),
                new WeightRow("Bard songs", "JA prep steps (Nightingale, Marcato...) score above this",
                    () => settings.SongWeight, v => settings.SongWeight = v),
                new WeightRow("Geomancy", "prep JAs (Blaze of Glory, Entrust...) score above this",
                    () => settings.GeoWeight, v => settings.GeoWeight = v),
                new WeightRow("Scholar abilities", "arts, addenda, Sublimation — idle-time upkeep",
                    () => settings.SchWeight, v => settings.SchWeight = v),
                new WeightRow("White Mage abilities", "Afflatus stances; Devotion scores above this",
                    () => settings.WhmWeight, v => settings.WhmWeight = v),
                new WeightRow("Red Mage abilities", "Composure upkeep; Convert scores above this",
                    () => settings.RdmWeight, v => settings.RdmWeight = v),
                new WeightRow("Buff upkeep", "lowest base — fires when nothing else needs doing",
                    () => settings.BuffWeight, v => settings.BuffWeight = v),
            };
        }

        /// <summary>High-priority member names, one per line (case-insensitive match at runtime).</summary>
        public string HighPriorityNames
        {
            get => string.Join(Environment.NewLine, Settings.HighPriorityMembers);
            set => Settings.HighPriorityMembers = (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Editable view of one action-kind weight; writes through to the settings clone.</summary>
    public sealed class WeightRow
    {
        private readonly Func<double> _get;
        private readonly Action<double> _set;

        public string Name { get; }

        /// <summary>Short explanation of what multiplies the base score.</summary>
        public string Note { get; }

        public double Score
        {
            get => _get();
            set => _set(value);
        }

        public WeightRow(string name, string note, Func<double> get, Action<double> set)
        {
            Name = name;
            Note = note;
            _get = get;
            _set = set;
        }
    }
}
