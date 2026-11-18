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
        /// was never allocated. Stratagems are granted automatically at level — the
        /// known-ability mask only mirrors the JA menu, which hides them until the
        /// matching Arts is active (live-verified) — so they classify by level alone.
        /// Everything else follows the spell classification.
        /// </summary>
        /// <param name="known">True when the character's known-ability mask has the ability.</param>
        /// <param name="jobLevel">The character's level in the ability's job.</param>
        /// <param name="entry">The catalog entry for the ability.</param>
        /// <returns>The learning state for display.</returns>
        public static SpellLearnState Classify(bool known, int jobLevel, AbilityCatalogEntry entry)
        {
            if (known)
            {
                return SpellLearnState.Learned;
            }

            if (entry.MeritGated && jobLevel >= entry.Level)
            {
                return SpellLearnState.MeritGated;
            }

            if (entry.Type == "Stratagem")
            {
                return jobLevel >= entry.Level ? SpellLearnState.Learned : SpellLearnState.Locked;
            }

            return SpellLearning.Classify(false, jobLevel, entry.Level);
        }

        /// <summary>
        /// True when a stratagem is usable by the given job combination, independent of
        /// the known-ability mask (which only reflects the JA menu under Arts). Sub-job
        /// access follows the grimoire chart: stratagems up to 40 are sub-accessible,
        /// 55 (Rapture, Ebullience) additionally needs any Master Leveled job, merit
        /// stratagems and 75+ are main-job only (merit allocation is not readable —
        /// the mask under Arts is the only evidence for those).
        /// </summary>
        /// <param name="entry">The stratagem's catalog entry.</param>
        /// <param name="mainJob">Current main job id (SCH = 20).</param>
        /// <param name="mainJobLevel">Current main job level.</param>
        /// <param name="subJob">Current sub job id (0 = none).</param>
        /// <param name="subJobLevel">Current sub job level.</param>
        /// <param name="anyJobMastered">True when any job on the character is master leveled.</param>
        /// <returns>True when the stratagem is granted to this job combination.</returns>
        public static bool StratagemAccessible(AbilityCatalogEntry entry, byte mainJob, int mainJobLevel,
            byte subJob, int subJobLevel, bool anyJobMastered)
        {
            const byte Scholar = 20;
            if (entry == null || entry.Type != "Stratagem" || entry.MeritGated)
            {
                return false;
            }

            if (mainJob == Scholar && mainJobLevel >= entry.Level)
            {
                return true;
            }

            if (subJob == Scholar && subJobLevel >= entry.Level)
            {
                return entry.Level <= 40 || (entry.Level <= 55 && anyJobMastered);
            }

            return false;
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

            if (entry.WhiteGrimoire)
            {
                notes = Append(notes, "White grimoire — usable only while Light Arts is active");
            }

            if (entry.BlackGrimoire)
            {
                notes = Append(notes, "Black grimoire — usable only while Dark Arts is active");
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
