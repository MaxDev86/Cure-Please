namespace CurePlease.Core
{
    /// <summary>
    /// Pure classification helpers for the Character Info abilities tab. Learned/Learnable/
    /// Locked reuse <see cref="SpellLearning.Classify"/>; this adds the merit gate and the
    /// wider ten-level buckets used for abilities.
    /// </summary>
    public static class AbilityLearning
    {
        /// <summary>
        /// Classifies an ability: merit-gated abilities (Altruism, Tranquility, ...) show
        /// as <see cref="SpellLearnState.MeritGated"/> when the level is met but the merit
        /// was never allocated; everything else follows the spell classification.
        /// </summary>
        /// <param name="known">True when the character's known-ability mask has the ability.</param>
        /// <param name="jobLevel">The character's level in the ability's job.</param>
        /// <param name="requiredLevel">Level the ability is learned at.</param>
        /// <param name="meritGated">True when the ability requires merit point allocation.</param>
        /// <returns>The learning state for display.</returns>
        public static SpellLearnState Classify(bool known, int jobLevel, int requiredLevel, bool meritGated)
        {
            if (!known && meritGated && jobLevel >= requiredLevel)
            {
                return SpellLearnState.MeritGated;
            }

            return SpellLearning.Classify(known, jobLevel, requiredLevel);
        }

        /// <summary>
        /// Tooltip notes for an ability row: merit gate, main-job-only, die-learned.
        /// Null when there is nothing noteworthy.
        /// </summary>
        /// <param name="entry">The catalog entry for the ability.</param>
        /// <returns>Tooltip text, or null.</returns>
        public static string Notes(AbilityCatalogEntry entry)
        {
            string notes = null;
            if (entry.MeritGated)
            {
                notes = "Requires merit point allocation";
            }

            if (entry.MainOnly)
            {
                notes = Append(notes, "Main job only — not usable as sub job");
            }

            if (entry.ItemLearned)
            {
                notes = Append(notes, "Learned from an item (COR rolls: use the matching die)");
            }

            return notes;
        }

        /// <summary>Ten-level bucket label ("Levels 1–10" .. "Levels 91–99") for grid grouping.</summary>
        /// <param name="requiredLevel">Level the ability is learned at.</param>
        /// <returns>Bucket label; never null.</returns>
        public static string LevelBucket(int requiredLevel)
        {
            int low = ((requiredLevel - 1) / 10) * 10 + 1;
            int high = low + 9 > 99 ? 99 : low + 9;
            return "Levels " + low + "–" + high;
        }

        private static string Append(string notes, string note)
        {
            return notes == null ? note : notes + "\n" + note;
        }
    }
}
