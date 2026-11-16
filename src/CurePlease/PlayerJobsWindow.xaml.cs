using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CurePlease.Core;
using CurePlease.Settings;

namespace CurePlease
{
    /// <summary>
    /// Read-only list of all 22 jobs for the attached character: level, and for
    /// level-99 jobs the mastery state (mastered = 2100 spent JP) with either the
    /// master level or the spent JP progress. Reads are live on open/Refresh.
    /// </summary>
    public partial class PlayerJobsWindow : Window
    {
        private readonly GameStateService _state;

        public PlayerJobsWindow(GameStateService state)
        {
            InitializeComponent();
            _state = state;
            RefreshRows();
        }

        public void RefreshRows()
        {
            List<JobStatusRow> statuses = _state.GetJobStatuses();
            if (statuses == null)
            {
                JobsGrid.ItemsSource = null;
                return;
            }

            _state.GetCurrentJobs(out byte mainJob, out byte subJob);

            JobsGrid.ItemsSource = statuses
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
    }
}
