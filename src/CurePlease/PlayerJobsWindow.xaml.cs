using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CurePlease.Core;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease
{
    /// <summary>
    /// Character info window. Jobs tab: all 22 jobs with level, mastery (2100 spent
    /// JP), and ML/JP progress. Spells tab: every spell a selected job can learn,
    /// classified Learned / Learnable (level met) / Locked / Gear Required (never
    /// learned, castable only with specific gear — e.g. Impact via Twilight Cloak)
    /// against the character's actual job levels and known-spell mask, grouped into
    /// five-level buckets. Abilities tab: same idea for job abilities (levels from the
    /// curated <see cref="AbilityCatalog"/>, plus Merit / main-only / die-learned notes),
    /// grouped into ten-level buckets. All reads are live; Refresh re-reads.
    /// </summary>
    public partial class PlayerJobsWindow : Window
    {
        private readonly GameStateService _state;
        private List<EliteAPI.ISpell> _allSpells;
        private List<JobStatusRow> _jobStatuses;

        public PlayerJobsWindow(GameStateService state)
        {
            InitializeComponent();
            _state = state;
            RefreshRows();
            LoadSpells();
            LoadAbilities();
        }

        // ---------- Jobs tab ----------

        public void RefreshRows()
        {
            _jobStatuses = _state.GetJobStatuses();
            if (_jobStatuses == null)
            {
                JobsGrid.ItemsSource = null;
                return;
            }

            _state.GetCurrentJobs(out byte mainJob, out byte subJob);

            JobsGrid.ItemsSource = _jobStatuses
                .Select(s => new Row(s, mainJob, subJob))
                .ToList();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRows();
        }

        private sealed class Row
        {
            public Row(JobStatusRow s, int mainJob, int subJob)
            {
                Job = ProfileService.JobAbbreviation((byte)s.JobId);
                Level = s.Level.ToString();
                IsCurrentJob = s.JobId == mainJob || s.JobId == subJob;

                if (s.Level == 99)
                {
                    Mastered = s.Mastered ? "Yes" : "No";
                    MasterLevel = s.Mastered ? s.MasterLevel.ToString() : "—";
                    JobPoints = s.Mastered ? "2100/2100" : s.SpentJobPoints + "/2100";
                }
                else
                {
                    Mastered = "—";
                    MasterLevel = "—";
                    JobPoints = "—";
                }
            }

            public string Job { get; }
            public string Level { get; }
            public string Mastered { get; }
            public string MasterLevel { get; }
            public string JobPoints { get; }
            public bool IsCurrentJob { get; }
        }

        // ---------- Spells tab ----------

        /// <summary>Loads (or reloads) the spell list and job combo; called on open and Refresh.</summary>
        private void LoadSpells()
        {
            _allSpells = _state.GetAllSpells();
            _jobStatuses = _jobStatuses ?? _state.GetJobStatuses();

            var items = new List<JobItem>();
            for (byte job = 1; job <= 22; job++)
            {
                string abbr = ProfileService.JobAbbreviation(job);
                if (abbr == null)
                {
                    continue;
                }

                int level = _jobStatuses?.FirstOrDefault(s => s.JobId == job)?.Level ?? 0;
                items.Add(new JobItem { JobId = job, Display = abbr + (level > 0 ? level.ToString() : string.Empty) });
            }

            _state.GetCurrentJobs(out byte mainJob, out byte _);
            SpellJobBox.ItemsSource = items;
            SpellJobBox.SelectedItem = items.FirstOrDefault(i => i.JobId == mainJob) ?? items.FirstOrDefault();
            RebuildSpellRows();
        }

        private void SpellRefresh_Click(object sender, RoutedEventArgs e)
        {
            _jobStatuses = null; // re-read levels too — a job may have leveled
            LoadSpells();
            LoadAbilities();
        }

        private void SpellFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent (IsChecked="True" on ShowLockedBox)
            // before SpellsGrid exists — ignore until the window is fully parsed.
            if (!IsInitialized)
            {
                return;
            }

            RebuildSpellRows();
        }

        private void RebuildSpellRows()
        {
            if (_allSpells == null || !(SpellJobBox.SelectedItem is JobItem selected))
            {
                SpellsGrid.ItemsSource = null;
                return;
            }

            int jobLevel = _jobStatuses?.FirstOrDefault(s => s.JobId == selected.JobId)?.Level ?? 0;
            bool learnableOnly = LearnableOnlyBox.IsChecked == true;
            bool showLocked = ShowLockedBox.IsChecked == true;

            var rows = new List<SpellRow>();
            foreach (EliteAPI.ISpell spell in _allSpells)
            {
                if (spell.LevelRequired == null || selected.JobId >= spell.LevelRequired.Length)
                {
                    continue;
                }

                int required = spell.LevelRequired[selected.JobId];
                if (required <= 0)
                {
                    continue; // not available to this job at any level
                }

                // HasSpell is keyed by ISpell.Index (the real spell id), never .ID.
                // Gear-gated spells (Impact) can never be learned — flag them instead
                // of classifying against the known-spell mask.
                SpellLearnState state = SpellLearning.IsGearGated(spell.Index)
                    ? SpellLearnState.GearGated
                    : SpellLearning.Classify(_state.KnowsSpell(spell.Index), jobLevel, required);
                if (learnableOnly && state != SpellLearnState.Learnable)
                {
                    continue;
                }

                if (!showLocked && state == SpellLearnState.Locked)
                {
                    continue;
                }

                rows.Add(new SpellRow(spell, required, state));
            }

            var view = new ListCollectionView(rows
                .OrderBy(r => SpellLearning.EffectiveLevel(r.RequiredLevel))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SpellRow.Bucket)));
            SpellsGrid.ItemsSource = view;
        }

        private sealed class JobItem
        {
            public int JobId { get; set; }
            public string Display { get; set; }
            public override string ToString() => Display;
        }

        private sealed class SpellRow
        {
            public SpellRow(EliteAPI.ISpell spell, int required, SpellLearnState state)
            {
                Name = spell.Name[0];
                RequiredLevel = required;
                Level = SpellLearning.LevelDisplay(required);
                Bucket = SpellLearning.LevelBucket(required);
                State = state;
                Status = state == SpellLearnState.GearGated ? "Gear Required" : state.ToString();
                StatusTooltip = state == SpellLearnState.GearGated
                    ? "Not learnable — castable only while equipped: " + SpellLearning.GearRequirement(spell.Index)
                    : null;
                MagicType = Enum.IsDefined(typeof(MagicType), (int)spell.MagicType)
                    ? ((MagicType)spell.MagicType).ToString()
                    : spell.MagicType.ToString();
            }

            public string Name { get; }
            public int RequiredLevel { get; }
            public string Level { get; }
            public string Bucket { get; }
            public SpellLearnState State { get; }
            public string Status { get; }
            public string StatusTooltip { get; }
            public string MagicType { get; }
        }

        // ---------- Abilities tab ----------

        /// <summary>Populates the ability job combo and builds the rows; called on open and Refresh.</summary>
        private void LoadAbilities()
        {
            _jobStatuses = _jobStatuses ?? _state.GetJobStatuses();

            var items = new List<JobItem>();
            for (byte job = 1; job <= 22; job++)
            {
                string abbr = ProfileService.JobAbbreviation(job);
                if (abbr == null)
                {
                    continue;
                }

                int level = _jobStatuses?.FirstOrDefault(s => s.JobId == job)?.Level ?? 0;
                items.Add(new JobItem { JobId = job, Display = abbr + (level > 0 ? level.ToString() : string.Empty) });
            }

            _state.GetCurrentJobs(out byte mainJob, out byte _);
            AbilityJobBox.ItemsSource = items;
            AbilityJobBox.SelectedItem = items.FirstOrDefault(i => i.JobId == mainJob) ?? items.FirstOrDefault();
            RebuildAbilityRows();
        }

        private void AbilityFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent (IsChecked="True" on AbilityShowLockedBox)
            // before AbilitiesGrid exists — ignore until the window is fully parsed.
            if (!IsInitialized)
            {
                return;
            }

            RebuildAbilityRows();
        }

        private void RebuildAbilityRows()
        {
            if (!(AbilityJobBox.SelectedItem is JobItem selected))
            {
                AbilitiesGrid.ItemsSource = null;
                return;
            }

            int jobLevel = _jobStatuses?.FirstOrDefault(s => s.JobId == selected.JobId)?.Level ?? 0;
            bool learnableOnly = AbilityLearnableOnlyBox.IsChecked == true;
            bool showLocked = AbilityShowLockedBox.IsChecked == true;

            var rows = new List<AbilityRow>();
            foreach (AbilityCatalogEntry entry in AbilityCatalog.ForJob(selected.JobId))
            {
                SpellLearnState state = AbilityLearning.Classify(
                    _state.KnowsAbility(entry.Id), jobLevel, entry.Level, entry.MeritGated);
                if (learnableOnly && state != SpellLearnState.Learnable)
                {
                    continue;
                }

                if (!showLocked && state == SpellLearnState.Locked)
                {
                    continue;
                }

                rows.Add(new AbilityRow(entry, state));
            }

            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AbilityRow.Bucket)));
            AbilitiesGrid.ItemsSource = view;
        }

        private sealed class AbilityRow
        {
            public AbilityRow(AbilityCatalogEntry entry, SpellLearnState state)
            {
                Name = entry.Name;
                Level = entry.Level.ToString();
                Bucket = AbilityLearning.LevelBucket(entry.Level);
                State = state;
                Status = state == SpellLearnState.MeritGated ? "Merit" : state.ToString();
                StatusTooltip = AbilityLearning.Notes(entry);
                Type = entry.Type;
            }

            public string Name { get; }
            public string Level { get; }
            public string Bucket { get; }
            public SpellLearnState State { get; }
            public string Status { get; }
            public string StatusTooltip { get; }
            public string Type { get; }
        }
    }
}
