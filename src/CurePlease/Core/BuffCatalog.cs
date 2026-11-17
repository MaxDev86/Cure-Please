using System;
using System.Collections.Generic;

namespace CurePlease.Core
{
    /// <summary>Mutually exclusive buff families: only one spell of a family can be up on a target at a time.</summary>
    public enum BuffFamily
    {
        None = 0,
        BarElement,
        BarStatus,
        Enspell,
        Storm,
    }

    /// <summary>
    /// Static white-magic buff catalog behind Buff Upkeep v2 (docs/buff-upkeep-design.md).
    /// Buff status ids, stock durations, and job levels come from Windower's resources
    /// (spells.lua), so durations are stock — gear, Composure, and Perpetuance extend
    /// them and are handled via the entry's editable DurationMinutes / recorded landing
    /// durations.
    ///
    /// AccessionCompatible: per BG-wiki's Accession page — spells SCH learns natively
    /// or can use from a sub job. Self-only spells (enspells, Flurry) are marked false:
    /// Accession has no effect on them. Haste, Phalanx II, Refresh II/III, Boost-* and
    /// Gain-* are excluded by the game.
    ///
    /// PerpetuanceCompatible: Perpetuance is SCH main only, so it can only extend
    /// spells an SCH main can actually cast — SCH-native spells, or WHM/RDM/PLD
    /// spells at level ≤ 49 (the sub-job cap under SCH/WHM, SCH/RDM, SCH/PLD).
    /// E.g. Phalanx II (RDM 75) can never be Perpetuanced; Haste (WHM 40) can via
    /// SCH/WHM.
    ///
    /// Group: rows on the Buff Upkeep page — one row per group with a dropdown of
    /// the group's spells (Protect I–V, the 6 bar-elements, Enfire/Enfire II, ...).
    /// Grouping is by element for enspells/storms: element is the tactical choice,
    /// tier is a potency choice inside the row (same axis as Protect I→V).
    /// </summary>
    public sealed class BuffCatalogEntry
    {
        public string Spell { get; }
        public short BuffId { get; }
        public double DefaultMinutes { get; }
        public bool AccessionCompatible { get; }
        public bool PerpetuanceCompatible { get; }
        public BuffFamily Family { get; }
        public string Group { get; }
        public string Notes { get; }

        /// <summary>
        /// True when the spell can target another party/alliance member (Windower
        /// spells.lua targets mask has a non-self bit: Player=2, Party=4, Ally=8,
        /// NPC=16). Self-only spells (targets=1: enspells, bar-singles, Boost/Gain,
        /// Phalanx, Aquaveil, Blink, Stoneskin...) and self-centered AoE (-ra spells)
        /// are false. Verified against the current resources dump.
        /// </summary>
        public bool PartyTargetable => BuffCatalog.PartyTargetableSpells.Contains(Spell);

        public BuffCatalogEntry(
            string spell, short buffId, double defaultMinutes,
            bool accessionCompatible, bool perpetuanceCompatible, string group,
            BuffFamily family = BuffFamily.None, string notes = null)
        {
            Spell = spell;
            BuffId = buffId;
            DefaultMinutes = defaultMinutes;
            AccessionCompatible = accessionCompatible;
            PerpetuanceCompatible = perpetuanceCompatible;
            Family = family;
            Group = group;
            Notes = notes;
        }
    }

    public static class BuffCatalog
    {
        private static readonly BuffCatalogEntry[] EntriesArray =
        {
            // --- Party staples (grouped by line) ---
            new BuffCatalogEntry("Protect V", 40, 30, true, true, "Protect"),
            new BuffCatalogEntry("Protect IV", 40, 30, true, true, "Protect"),
            new BuffCatalogEntry("Protect III", 40, 30, true, true, "Protect"),
            new BuffCatalogEntry("Protect II", 40, 30, true, true, "Protect"),
            new BuffCatalogEntry("Protect", 40, 30, true, true, "Protect"),
            new BuffCatalogEntry("Shell V", 41, 30, true, true, "Shell"),
            new BuffCatalogEntry("Shell IV", 41, 30, true, true, "Shell"),
            new BuffCatalogEntry("Shell III", 41, 30, true, true, "Shell"),
            new BuffCatalogEntry("Shell II", 41, 30, true, true, "Shell"),
            new BuffCatalogEntry("Shell", 41, 30, true, true, "Shell"),

            // --- Party -ra versions (self-cast, AoE around the PL — no Accession
            // needed; same status ids as the singles, so presence/family checks
            // treat them interchangeably). Perpetuance only where SCH has access
            // (WHM sub cap 49): Protectra I-III, Shellra I-II, bar-ra except
            // Baramnesra (WHM 78 main). ---
            new BuffCatalogEntry("Protectra V", 40, 30, false, false, "Protectra", notes: "WHM 75; party AoE"),
            new BuffCatalogEntry("Protectra IV", 40, 30, false, false, "Protectra", notes: "WHM 63; party AoE"),
            new BuffCatalogEntry("Protectra III", 40, 30, false, true, "Protectra", notes: "WHM 47; party AoE"),
            new BuffCatalogEntry("Protectra II", 40, 30, false, true, "Protectra", notes: "WHM 27; party AoE"),
            new BuffCatalogEntry("Protectra", 40, 30, false, true, "Protectra", notes: "WHM 7; party AoE"),
            new BuffCatalogEntry("Shellra V", 41, 30, false, false, "Shellra", notes: "WHM 75; party AoE"),
            new BuffCatalogEntry("Shellra IV", 41, 30, false, false, "Shellra", notes: "WHM 68; party AoE"),
            new BuffCatalogEntry("Shellra III", 41, 30, false, false, "Shellra", notes: "WHM 57; party AoE"),
            new BuffCatalogEntry("Shellra II", 41, 30, false, true, "Shellra", notes: "WHM 37; party AoE"),
            new BuffCatalogEntry("Shellra", 41, 30, false, true, "Shellra", notes: "WHM 17; party AoE"),
            new BuffCatalogEntry("Haste", 33, 3, false, true, "Haste", notes: "WHM 40 / RDM 48"),
            new BuffCatalogEntry("Haste II", 33, 3, false, false, "Haste", notes: "RDM 96 main"),
            new BuffCatalogEntry("Regen V", 42, 1, true, true, "Regen", notes: "SCH 99; stock 1 min, builds reach 4-5 min"),
            new BuffCatalogEntry("Regen IV", 42, 1, true, true, "Regen"),
            new BuffCatalogEntry("Regen III", 42, 1, true, true, "Regen"),
            new BuffCatalogEntry("Regen II", 42, 1, true, true, "Regen"),
            new BuffCatalogEntry("Regen", 42, 1.25, true, true, "Regen"),
            new BuffCatalogEntry("Refresh", 43, 2.5, true, true, "Refresh", notes: "RDM 41"),
            new BuffCatalogEntry("Refresh II", 43, 2.5, false, false, "Refresh", notes: "RDM 82 main"),
            new BuffCatalogEntry("Refresh III", 43, 2.5, false, false, "Refresh", notes: "RDM 99 main (JP spell)"),
            new BuffCatalogEntry("Phalanx", 116, 3, true, true, "Phalanx", notes: "RDM 33"),
            new BuffCatalogEntry("Phalanx II", 116, 4, false, false, "Phalanx", notes: "RDM 75 main"),
            new BuffCatalogEntry("Flurry", 581, 3, false, true, "Flurry", notes: "RDM 48 (SCH/RDM 98)"),
            new BuffCatalogEntry("Flurry II", 581, 3, false, false, "Flurry", notes: "RDM 96 main"),
            new BuffCatalogEntry("Stoneskin", 37, 5, true, true, "Stoneskin"),
            new BuffCatalogEntry("Blink", 36, 5, true, true, "Blink"),
            new BuffCatalogEntry("Aquaveil", 39, 10, true, true, "Aquaveil"),
            new BuffCatalogEntry("Adloquium", 170, 3, true, true, "Adloquium", notes: "SCH 88 main; Regain status"),
            new BuffCatalogEntry("Auspice", 275, 3, false, false, "Auspice", notes: "WHM 55 main"),
            new BuffCatalogEntry("Embrava", 228, 1.5, true, true, "Embrava", notes: "SCH 5 main (Tabula Rasa / 1hr)"),
            new BuffCatalogEntry("Animus Augeo", 289, 3, true, true, "Animus Augeo", notes: "SCH 85 main; Enmity Boost"),
            new BuffCatalogEntry("Animus Minuo", 171, 3, true, true, "Animus Minuo", notes: "SCH 85 main; Enmity Down"),

            // --- Bar elements (one per player at a time; RDM/RUN singles) ---
            new BuffCatalogEntry("Barfire", 100, 8, true, true, "Bar (element)", BuffFamily.BarElement, "RDM 17"),
            new BuffCatalogEntry("Barblizzard", 101, 8, true, true, "Bar (element)", BuffFamily.BarElement, "RDM 21"),
            new BuffCatalogEntry("Baraero", 102, 8, true, true, "Bar (element)", BuffFamily.BarElement, "RDM 13"),
            new BuffCatalogEntry("Barstone", 103, 8, true, true, "Bar (element)", BuffFamily.BarElement, "RDM 5"),
            new BuffCatalogEntry("Barthunder", 104, 8, true, true, "Bar (element)", BuffFamily.BarElement, "RDM 25"),
            new BuffCatalogEntry("Barwater", 105, 8, true, true, "Bar (element)", BuffFamily.BarElement, "RDM 9"),

            // --- Bar-ra elements (party AoE; same status ids as the singles) ---
            new BuffCatalogEntry("Barfira", 100, 8, false, true, "Bar-ra (element)", BuffFamily.BarElement, "WHM 17; party AoE"),
            new BuffCatalogEntry("Barblizzara", 101, 8, false, true, "Bar-ra (element)", BuffFamily.BarElement, "WHM 21; party AoE"),
            new BuffCatalogEntry("Baraera", 102, 8, false, true, "Bar-ra (element)", BuffFamily.BarElement, "WHM 13; party AoE"),
            new BuffCatalogEntry("Barstonra", 103, 8, false, true, "Bar-ra (element)", BuffFamily.BarElement, "WHM 5; party AoE"),
            new BuffCatalogEntry("Barthundra", 104, 8, false, true, "Bar-ra (element)", BuffFamily.BarElement, "WHM 25; party AoE"),
            new BuffCatalogEntry("Barwatera", 105, 8, false, true, "Bar-ra (element)", BuffFamily.BarElement, "WHM 9; party AoE"),

            // --- Bar status (one per player at a time) ---
            new BuffCatalogEntry("Barsleep", 106, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 7"),
            new BuffCatalogEntry("Barpoison", 107, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 10"),
            new BuffCatalogEntry("Barparalyze", 108, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 12"),
            new BuffCatalogEntry("Barblind", 109, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 18"),
            new BuffCatalogEntry("Barsilence", 110, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 23"),
            new BuffCatalogEntry("Barpetrify", 111, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 43"),
            new BuffCatalogEntry("Barvirus", 112, 8, true, true, "Bar (status)", BuffFamily.BarStatus, "RDM 39"),
            new BuffCatalogEntry("Baramnesia", 286, 8, false, false, "Bar (status)", BuffFamily.BarStatus, "RDM 78 main"),

            // --- Bar-ra status (party AoE; same status ids as the singles) ---
            new BuffCatalogEntry("Barsleepra", 106, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 7; party AoE"),
            new BuffCatalogEntry("Barpoisonra", 107, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 10; party AoE"),
            new BuffCatalogEntry("Barparalyzra", 108, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 12; party AoE"),
            new BuffCatalogEntry("Barblindra", 109, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 18; party AoE"),
            new BuffCatalogEntry("Barsilencera", 110, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 23; party AoE"),
            new BuffCatalogEntry("Barpetra", 111, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 43; party AoE"),
            new BuffCatalogEntry("Barvira", 112, 8, false, true, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 39; party AoE"),
            new BuffCatalogEntry("Baramnesra", 286, 8, false, false, "Bar-ra (status)", BuffFamily.BarStatus, "WHM 78 main; party AoE"),

            // --- Enspells (self-target, one at a time; grouped by element) ---
            // Accession: tier-1 enspells are on the BG-wiki list — SCH self-casts and
            // party members in range get the enspell too. Tier-2/Enlight/Endark are
            // other-main-job-only spells, which BG-wiki says Accession does NOT extend.
            new BuffCatalogEntry("Enfire", 94, 3, true, true, "Enspell (Fire)", BuffFamily.Enspell, "RDM 24"),
            new BuffCatalogEntry("Enfire II", 277, 3, false, false, "Enspell (Fire)", BuffFamily.Enspell, "RDM 58 main"),
            new BuffCatalogEntry("Enblizzard", 95, 3, true, true, "Enspell (Ice)", BuffFamily.Enspell, "RDM 22"),
            new BuffCatalogEntry("Enblizzard II", 278, 3, false, false, "Enspell (Ice)", BuffFamily.Enspell, "RDM 56 main"),
            new BuffCatalogEntry("Enaero", 96, 3, true, true, "Enspell (Wind)", BuffFamily.Enspell, "RDM 20"),
            new BuffCatalogEntry("Enaero II", 279, 3, false, false, "Enspell (Wind)", BuffFamily.Enspell, "RDM 54 main"),
            new BuffCatalogEntry("Enstone", 97, 3, true, true, "Enspell (Earth)", BuffFamily.Enspell, "RDM 18"),
            new BuffCatalogEntry("Enstone II", 280, 3, false, false, "Enspell (Earth)", BuffFamily.Enspell, "RDM 52 main"),
            new BuffCatalogEntry("Enthunder", 98, 3, true, true, "Enspell (Lightning)", BuffFamily.Enspell, "RDM 16"),
            new BuffCatalogEntry("Enthunder II", 281, 3, false, false, "Enspell (Lightning)", BuffFamily.Enspell, "RDM 50 main"),
            new BuffCatalogEntry("Enwater", 99, 3, true, true, "Enspell (Water)", BuffFamily.Enspell, "RDM 27"),
            new BuffCatalogEntry("Enwater II", 282, 3, false, false, "Enspell (Water)", BuffFamily.Enspell, "RDM 60 main"),
            new BuffCatalogEntry("Enlight", 274, 3, false, false, "Enlight", BuffFamily.Enspell, "PLD 85 main"),
            new BuffCatalogEntry("Enlight II", 274, 3, false, false, "Enlight", BuffFamily.Enspell, "PLD 100 main"),
            new BuffCatalogEntry("Endark", 288, 3, false, false, "Endark", BuffFamily.Enspell, "DRK 85 main"),
            new BuffCatalogEntry("Endark II", 288, 3, false, false, "Endark", BuffFamily.Enspell, "DRK 100 main"),

            // --- Storms (SCH; grouped by element, I/II inside the row; Accession storms hit the party) ---
            new BuffCatalogEntry("Firestorm", 178, 3, true, true, "Storm (Fire)", BuffFamily.Storm, "SCH 44"),
            new BuffCatalogEntry("Firestorm II", 589, 3, true, true, "Storm (Fire)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Hailstorm", 179, 3, true, true, "Storm (Ice)", BuffFamily.Storm, "SCH 45"),
            new BuffCatalogEntry("Hailstorm II", 590, 3, true, true, "Storm (Ice)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Windstorm", 180, 3, true, true, "Storm (Wind)", BuffFamily.Storm, "SCH 43"),
            new BuffCatalogEntry("Windstorm II", 591, 3, true, true, "Storm (Wind)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Sandstorm", 181, 3, true, true, "Storm (Earth)", BuffFamily.Storm, "SCH 41"),
            new BuffCatalogEntry("Sandstorm II", 592, 3, true, true, "Storm (Earth)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Thunderstorm", 182, 3, true, true, "Storm (Lightning)", BuffFamily.Storm, "SCH 46"),
            new BuffCatalogEntry("Thunderstorm II", 593, 3, true, true, "Storm (Lightning)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Rainstorm", 183, 3, true, true, "Storm (Water)", BuffFamily.Storm, "SCH 42"),
            new BuffCatalogEntry("Rainstorm II", 594, 3, true, true, "Storm (Water)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Aurorastorm", 184, 3, true, true, "Storm (Light)", BuffFamily.Storm, "SCH 48"),
            new BuffCatalogEntry("Aurorastorm II", 595, 3, true, true, "Storm (Light)", BuffFamily.Storm, "SCH 99 main + 100 JP"),
            new BuffCatalogEntry("Voidstorm", 185, 3, true, true, "Storm (Dark)", BuffFamily.Storm, "SCH 47"),
            new BuffCatalogEntry("Voidstorm II", 596, 3, true, true, "Storm (Dark)", BuffFamily.Storm, "SCH 99 main + 100 JP"),

            // --- Boost (WHM main 81-99) / Gain (RDM main 81-99); one Boost and one Gain at a time (UI-enforced) ---
            new BuffCatalogEntry("Boost-STR", 119, 5, false, false, "Boost (STR)", notes: "WHM 93 main"),
            new BuffCatalogEntry("Boost-DEX", 120, 5, false, false, "Boost (DEX)", notes: "WHM 99 main"),
            new BuffCatalogEntry("Boost-VIT", 121, 5, false, false, "Boost (VIT)", notes: "WHM 81 main"),
            new BuffCatalogEntry("Boost-AGI", 122, 5, false, false, "Boost (AGI)", notes: "WHM 90 main"),
            new BuffCatalogEntry("Boost-INT", 123, 5, false, false, "Boost (INT)", notes: "WHM 96 main"),
            new BuffCatalogEntry("Boost-MND", 124, 5, false, false, "Boost (MND)", notes: "WHM 84 main"),
            new BuffCatalogEntry("Boost-CHR", 86, 5, false, false, "Boost (CHR)", notes: "WHM 87 main"),
            new BuffCatalogEntry("Gain-STR", 119, 5, false, false, "Gain (STR)", notes: "RDM 93 main"),
            new BuffCatalogEntry("Gain-DEX", 120, 5, false, false, "Gain (DEX)", notes: "RDM 99 main"),
            new BuffCatalogEntry("Gain-VIT", 121, 5, false, false, "Gain (VIT)", notes: "RDM 81 main"),
            new BuffCatalogEntry("Gain-AGI", 122, 5, false, false, "Gain (AGI)", notes: "RDM 90 main"),
            new BuffCatalogEntry("Gain-INT", 123, 5, false, false, "Gain (INT)", notes: "RDM 96 main"),
            new BuffCatalogEntry("Gain-MND", 124, 5, false, false, "Gain (MND)", notes: "RDM 84 main"),
            new BuffCatalogEntry("Gain-CHR", 125, 5, false, false, "Gain (CHR)", notes: "RDM 87 main"),
        };

        private static readonly Dictionary<string, BuffCatalogEntry> ByName = BuildIndex();

        /// <summary>
        /// Catalog spells whose targets mask (Windower spells.lua) allows aiming at
        /// another party/alliance member. Sourced from the current resources dump —
        /// the masks, for reference: Protect/Shell/Haste/Flurry/Embrava = 29
        /// (Self+Party+Ally+NPC), Regen/Refresh/Phalanx II/Adloquium/storms/Animus = 5
        /// (Self+Party).
        /// </summary>
        internal static readonly HashSet<string> PartyTargetableSpells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Protect", "Protect II", "Protect III", "Protect IV", "Protect V",
            "Shell", "Shell II", "Shell III", "Shell IV", "Shell V",
            "Haste", "Haste II",
            "Regen", "Regen II", "Regen III", "Regen IV", "Regen V",
            "Refresh", "Refresh II", "Refresh III",
            "Phalanx II",
            "Flurry", "Flurry II",
            "Adloquium",
            "Embrava",
            "Animus Augeo", "Animus Minuo",
            "Firestorm", "Firestorm II", "Hailstorm", "Hailstorm II",
            "Windstorm", "Windstorm II", "Sandstorm", "Sandstorm II",
            "Thunderstorm", "Thunderstorm II", "Rainstorm", "Rainstorm II",
            "Aurorastorm", "Aurorastorm II", "Voidstorm", "Voidstorm II",
        };

        private static Dictionary<string, BuffCatalogEntry> BuildIndex()
        {
            var index = new Dictionary<string, BuffCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (BuffCatalogEntry entry in EntriesArray)
            {
                index[entry.Spell] = entry;
            }

            return index;
        }

        /// <summary>All catalog entries, in display order.</summary>
        public static IReadOnlyList<BuffCatalogEntry> Entries => EntriesArray;

        /// <summary>Lookup by spell name (case-insensitive); null when not in the catalog.</summary>
        public static BuffCatalogEntry Find(string spellName)
        {
            return spellName != null && ByName.TryGetValue(spellName.Trim(), out BuffCatalogEntry entry)
                ? entry
                : null;
        }

        /// <summary>Job levels for a spell (job id -> level learned), generated from spells.lua; null when unknown.</summary>
        public static IReadOnlyDictionary<int, int> LevelsOf(string spellName)
        {
            return spellName != null && BuffCatalogLevels.Table.TryGetValue(spellName.Trim(), out IReadOnlyDictionary<int, int> levels)
                ? levels
                : null;
        }

        /// <summary>
        /// True when a job at the given level can cast the spell. Levels above 99 are
        /// JP-gated in spells.lua (100 = main-only II spells, 1200 = 99 + spent JP):
        /// those require the MAIN job at 99 — never a sub job, even at ML 50. Unknown
        /// spells fail open (visible) so a data gap never hides a row.
        /// </summary>
        public static bool CastableBy(string spellName, int jobId, int jobLevel, bool isMainJob)
        {
            IReadOnlyDictionary<int, int> levels = LevelsOf(spellName);
            if (levels == null)
            {
                return true;
            }

            if (!levels.TryGetValue(jobId, out int required))
            {
                return false;
            }

            if (required > 99)
            {
                return isMainJob && jobLevel >= 99;
            }

            return required <= jobLevel;
        }

        /// <summary>Group names in catalog order, with their member spells.</summary>
        public static IReadOnlyList<KeyValuePair<string, List<BuffCatalogEntry>>> Groups()
        {
            var groups = new List<KeyValuePair<string, List<BuffCatalogEntry>>>();
            var byName = new Dictionary<string, List<BuffCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (BuffCatalogEntry entry in EntriesArray)
            {
                if (!byName.TryGetValue(entry.Group, out List<BuffCatalogEntry> members))
                {
                    members = new List<BuffCatalogEntry>();
                    byName[entry.Group] = members;
                    groups.Add(new KeyValuePair<string, List<BuffCatalogEntry>>(entry.Group, members));
                }

                members.Add(entry);
            }

            return groups;
        }

        /// <summary>Buff ids belonging to a family (for exclusivity checks).</summary>
        public static HashSet<short> BuffIdsOfFamily(BuffFamily family)
        {
            var ids = new HashSet<short>();
            foreach (BuffCatalogEntry entry in EntriesArray)
            {
                if (entry.Family == family)
                {
                    ids.Add(entry.BuffId);
                }
            }

            return ids;
        }
    }
}
