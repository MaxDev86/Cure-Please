using System.Collections.Generic;

namespace CurePlease.Settings
{
    /// <summary>One row in the Settings window nav list.</summary>
    public sealed class SettingsNavItem
    {
        public bool IsHeader { get; private set; }

        /// <summary>Display label — job pages of the profile's sub job carry a " (sub)" suffix.</summary>
        public string Label { get; private set; }

        /// <summary>Page key used to instantiate the page ("General", "Bard", ...); null for headers.</summary>
        public string PageKey { get; private set; }

        /// <summary>Job id for job pages (WHM=3, RDM=5, BRD=10, SCH=20, GEO=21); 0 otherwise.</summary>
        public byte JobId { get; private set; }

        public static SettingsNavItem Header(string label)
        {
            return new SettingsNavItem { IsHeader = true, Label = label };
        }

        public static SettingsNavItem Page(string pageKey, byte jobId = 0, string label = null)
        {
            return new SettingsNavItem { PageKey = pageKey, Label = label ?? pageKey, JobId = jobId };
        }
    }

    /// <summary>
    /// The Settings window nav definition. Feature pages always show; job pages
    /// can be trimmed to a profile combo — but only when the user explicitly
    /// turns the filter on (GlobalSettings.FilterJobPagesByCombo, off by
    /// default), and an unknown combo fails open (everything stays visible).
    /// Kept UI-free so the matrix can be verified headlessly.
    /// </summary>
    public static class SettingsNavigation
    {
        /// <summary>Page keys for the feature pages, in nav order (never filtered).</summary>
        public static readonly IReadOnlyList<string> FeaturePages = new[]
        {
            "General", "Priorities", "Healing", "Debuff Removal", "Buff Upkeep",
        };

        /// <summary>
        /// Builds the nav rows. <paramref name="forcedPage"/> force-includes one
        /// page even when the combo filter would hide it (deep-links from the
        /// main window beat the filter).
        /// </summary>
        public static List<SettingsNavItem> Build(bool filterByCombo, byte mainJob, byte subJob, string forcedPage = null)
        {
            var items = new List<SettingsNavItem>
            {
                SettingsNavItem.Header("— Core —"),
                SettingsNavItem.Page("General"),
                SettingsNavItem.Page("Priorities"),
                SettingsNavItem.Header("— Healing —"),
                SettingsNavItem.Page("Healing"),
                SettingsNavItem.Page("Debuff Removal"),
                SettingsNavItem.Header("— Buffs —"),
                SettingsNavItem.Page("Buff Upkeep"),
            };

            var jobPages = new List<SettingsNavItem>
            {
                SettingsNavItem.Page("White Mage", 3),
                SettingsNavItem.Page("Red Mage", 5),
                SettingsNavItem.Page("Scholar", 20),
                SettingsNavItem.Page("Bard", 10),
                SettingsNavItem.Page("Geomancy", 21),
            };

            bool comboKnown = mainJob != 0;
            var visible = new List<SettingsNavItem>();
            foreach (SettingsNavItem page in jobPages)
            {
                bool inCombo = comboKnown && (page.JobId == mainJob || page.JobId == subJob);
                bool forced = forcedPage != null && page.PageKey == forcedPage;
                if (filterByCombo && comboKnown && !inCombo && !forced)
                {
                    continue;
                }

                SettingsNavItem row = page;
                if (comboKnown && page.JobId == subJob && page.JobId != mainJob)
                {
                    row = SettingsNavItem.Page(page.PageKey, page.JobId, page.PageKey + " (sub)");
                }

                visible.Add(row);
            }

            if (visible.Count > 0)
            {
                items.Add(SettingsNavItem.Header("— Jobs —"));
                items.AddRange(visible);
            }

            return items;
        }
    }
}
