using System.Collections.Generic;

namespace CurePlease.Core
{
    /// <summary>Learning state of one spell or ability for one job on the attached character.</summary>
    public enum SpellLearnState
    {
        /// <summary>Present in the character's known-spell bitmask.</summary>
        Learned,

        /// <summary>Job level requirement met but not learned (scroll/spell never acquired).</summary>
        Learnable,

        /// <summary>Job level requirement not yet met.</summary>
        Locked,

        /// <summary>Never learned into the spell list — castable only while specific gear is equipped.</summary>
        GearGated,

        /// <summary>Level requirement met but the ability requires merit point allocation.</summary>
        MeritGated,

        /// <summary>
        /// Level requirement met but the learned state cannot be read: the client's
        /// known-ability mask is a snapshot of jobs used since login, not an
        /// account-wide map (live-verified — unlike the known-spell mask). A set bit
        /// is always a true learned ability; a cleared bit proves nothing off-job.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// Pure classification for the Character Info spell browser. JP-gated spells
    /// carry levels &gt; 99 in the resource data (100 = main-only II spells,
    /// 1200 = 99 + spent JP) and are treated as level 99 for learnability.
    /// </summary>
    public static class SpellLearning
    {
        /// <summary>
        /// Spell ids that no player can learn or cast — Windower's spells.lua marks these
        /// <c>unlearnable=true</c> (enemy-only, e.g. Hastega, Slowga). They DO carry job
        /// levels in the client data, so level checks alone cannot exclude them.
        /// Gear-gated unlearnable spells (e.g. Impact) are NOT here — see
        /// <see cref="GearGatedSpells"/>.
        /// </summary>
        public static readonly IReadOnlySet<int> UnlearnableIds = new HashSet<int>
        {
            26, 27,             // Dia IV, Dia V
            31, 32,             // Banish IV, Banish V
            34, 35, 36, 37,     // Diaga II–V
            40, 41, 42,         // Banishga III–V
            177, 178,           // Firaga IV, Firaga V
            182, 183,           // Blizzaga IV, Blizzaga V
            187, 188,           // Aeroga IV, Aeroga V
            192, 193,           // Stonega IV, Stonega V
            197, 198,           // Thundaga IV, Thundaga V
            202, 203,           // Waterga IV, Waterga V
            222, 223, 224,      // Poison III–V
            226, 227, 228, 229, // Poisonga II–V
            233, 234,           // Bio IV, Bio V
            244,                // Meteor II
            256, 257,           // Virus, Curse
            265,                // Tractor II
            342, 343,           // Jubaku: Ni, Jubaku: San
            346,                // Hojo: San
            349,                // Kurayami: San
            351, 352,           // Dokumori: Ni, Dokumori: San
            356, 357, 358, 359, // Paralyga, Slowga, Hastega, Silencega
            360, 361, 362,      // Dispelga, Blindga, Bindga
            363, 364,           // Sleepga, Sleepga II
            366,                // Graviga
            375,                // Foe Requiem VIII
            384, 385,           // Army's Paeon VII, VIII
            407,                // Chocobo Hum
            411,                // Jester's Operetta
            413,                // Devotee Serenade
            416,                // Cactuar Fugue
            423,                // Massacre Elegy
        };

        /// <summary>
        /// Spells that are never learned into the spell list but CAN be cast while
        /// specific gear is equipped (Windower marks them unlearnable too, but hiding
        /// them would be misleading). Key: spell id; value: the gear that grants it.
        /// </summary>
        public static readonly IReadOnlyDictionary<int, string> GearGatedSpells =
            new Dictionary<int, string>
            {
                { 503, "Twilight Cloak or Crepuscular Cloak (body)" }, // Impact
            };

        /// <summary>
        /// True when the spell is castable only while specific gear is equipped
        /// (never learned into the spell list), e.g. Impact via Twilight Cloak.
        /// </summary>
        /// <param name="spellId">Real spell id (Windower spells.lua id / ISpell.Index).</param>
        /// <returns>True when the spell is gear-gated.</returns>
        public static bool IsGearGated(int spellId)
        {
            return GearGatedSpells.ContainsKey(spellId);
        }

        /// <summary>
        /// Human-readable gear requirement for a gear-gated spell, e.g.
        /// "Twilight Cloak or Crepuscular Cloak (body)" for Impact.
        /// </summary>
        /// <param name="spellId">Real spell id (Windower spells.lua id / ISpell.Index).</param>
        /// <returns>Gear description, or null when the spell is not gear-gated.</returns>
        public static string GearRequirement(int spellId)
        {
            return GearGatedSpells.TryGetValue(spellId, out string gear) ? gear : null;
        }

        /// <summary>
        /// True when no player can ever learn this spell (Windower spells.lua
        /// <c>unlearnable=true</c>). Used to hide enemy-only spells from the
        /// Character Info spell browser.
        /// </summary>
        /// <param name="spellId">Real spell id (Windower spells.lua id / ISpell.Index).</param>
        /// <returns>True when the spell is enemy-only or gear-gated.</returns>
        public static bool IsUnlearnable(int spellId)
        {
            return UnlearnableIds.Contains(spellId);
        }

        /// <summary>
        /// Classifies a spell: known mask first, then job level vs requirement
        /// (JP-gated requirements count as 99).
        /// </summary>
        /// <param name="known">True when the character's known-spell mask has the spell.</param>
        /// <param name="jobLevel">The character's level in the spell's job.</param>
        /// <param name="requiredLevel">Required job level from the resource database (&gt;99 = JP-gated).</param>
        /// <returns>The learning state for display.</returns>
        public static SpellLearnState Classify(bool known, int jobLevel, int requiredLevel)
        {
            if (known)
            {
                return SpellLearnState.Learned;
            }

            int effective = requiredLevel > 99 ? 99 : requiredLevel;
            return jobLevel >= effective ? SpellLearnState.Learnable : SpellLearnState.Locked;
        }

        /// <summary>Level column text: "99 (JP)" for JP-gated spells, the plain level otherwise.</summary>
        /// <param name="requiredLevel">Required job level from the resource database.</param>
        /// <returns>Display string for the level column.</returns>
        public static string LevelDisplay(int requiredLevel)
        {
            return requiredLevel > 99 ? "99 (JP)" : requiredLevel.ToString();
        }

        /// <summary>Sort key for the level column — JP-gated spells sort as 99.</summary>
        /// <param name="requiredLevel">Required job level from the resource database.</param>
        /// <returns>Numeric level used for sorting and bucketing.</returns>
        public static int EffectiveLevel(int requiredLevel)
        {
            return requiredLevel > 99 ? 99 : requiredLevel;
        }

        /// <summary>Fixed five-level bucket label ("Levels 1–5" .. "Levels 96–99") for grid grouping.</summary>
        /// <param name="requiredLevel">Required job level from the resource database.</param>
        /// <returns>Bucket label; never null.</returns>
        public static string LevelBucket(int requiredLevel)
        {
            int level = EffectiveLevel(requiredLevel);
            int low = ((level - 1) / 5) * 5 + 1;
            int high = low + 4 > 99 ? 99 : low + 4;
            return "Levels " + low + "–" + high;
        }
    }
}
