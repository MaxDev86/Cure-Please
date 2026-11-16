using System.Collections.Generic;

namespace CurePlease.Core
{
    /// <summary>
    /// Spell lists for the GEO enmity-assist dropdowns, generated from Windower's
    /// spells.lua (tools: gen_enmity_catalog.py). PlayerSpells = party-targetable
    /// spells a sub job can grant (level &lt;= 49); EnemySpells = enemy-targetable
    /// spells, GEO native (any level) or sub job (&lt;= 49). Geo-*/Indi-* are
    /// excluded — they are luopon placement, not hate spells.
    /// Levels format: "jobId=level,..." with Windower job ids (3=WHM, 5=RDM, 21=GEO...).
    /// </summary>
    public static class EnmitySpellCatalog
    {
        /// <summary>
        /// Sub-job filter dropdown content: "Any" + the 22 job abbreviations.
        /// The list index IS the Windower job id (0 = Any).
        /// </summary>
        public static readonly IReadOnlyList<string> JobNames = new[]
        {
            "Any", "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK", "BST", "BRD",
            "RNG", "SAM", "NIN", "DRG", "SMN", "BLU", "COR", "PUP", "DNC", "SCH", "GEO", "RUN",
        };

        public sealed class Entry
        {
            public string Spell { get; }
            public ushort Targets { get; }
            public string Levels { get; }

            public Entry(string spell, ushort targets, string levels)
            {
                Spell = spell;
                Targets = targets;
                Levels = levels;
            }

            /// <summary>Level for the given job id; 0 when the job never learns it.</summary>
            public int LevelFor(int jobId)
            {
                string needle = jobId + "=";
                foreach (string pair in Levels.Split(','))
                {
                    if (pair.StartsWith(needle, System.StringComparison.Ordinal))
                    {
                        return int.Parse(pair.Substring(needle.Length));
                    }
                }

                return 0;
            }

            /// <summary>Learned by GEO natively (always available regardless of sub job).</summary>
            public bool GeoNative => LevelFor(21) > 0;

            /// <summary>Obtainable from this sub job (level &lt;= 49); 0 = any sub job.</summary>
            public bool UsableWithSub(int subJobId)
            {
                if (subJobId == 0)
                {
                    return true;
                }

                int level = LevelFor(subJobId);
                return level > 0 && level <= 49;
            }
        }

        /// <summary>Party-targetable hate spells (cure-the-tank mode).</summary>
        public static readonly IReadOnlyList<Entry> PlayerSpells = new[]
        {
            new Entry("Cure", 63, "3=1,5=3,7=5,20=5"),
            new Entry("Cure II", 63, "3=11,5=14,7=17,20=17"),
            new Entry("Cure III", 63, "3=21,5=26,7=30,20=30"),
            new Entry("Cure IV", 63, "3=41,5=48,7=55,20=55"),
            new Entry("Curaga", 5, "3=16"),
            new Entry("Curaga II", 5, "3=31"),
            new Entry("Raise", 157, "3=25,5=38,7=50,20=35"),
            new Entry("Poisona", 29, "3=6,20=10"),
            new Entry("Paralyna", 29, "3=9,20=12"),
            new Entry("Blindna", 29, "3=14,20=17"),
            new Entry("Silena", 29, "3=19,20=22"),
            new Entry("Stona", 29, "3=39,20=50"),
            new Entry("Viruna", 29, "3=34,20=46"),
            new Entry("Cursna", 29, "3=29,20=32"),
            new Entry("Protect", 29, "3=7,5=7,7=10,20=10,22=20"),
            new Entry("Protect II", 29, "3=27,5=27,7=30,20=30,22=40"),
            new Entry("Protect III", 29, "3=47,5=47,7=50,20=50,22=60"),
            new Entry("Shell", 29, "3=17,5=17,7=20,20=20,22=10"),
            new Entry("Shell II", 29, "3=37,5=37,7=40,20=40,22=30"),
            new Entry("Haste", 29, "3=40,5=48"),
            new Entry("Sandstorm", 5, "20=41"),
            new Entry("Regen", 5, "3=21,5=21,20=18,22=23"),
            new Entry("Refresh", 5, "5=41,22=62"),
            new Entry("Regen II", 5, "3=44,5=76,20=37,22=48"),
            new Entry("Rainstorm", 5, "20=42"),
            new Entry("Windstorm", 5, "20=43"),
            new Entry("Firestorm", 5, "20=44"),
            new Entry("Hailstorm", 5, "20=45"),
            new Entry("Thunderstorm", 5, "20=46"),
            new Entry("Voidstorm", 5, "20=47"),
            new Entry("Aurorastorm", 5, "20=48"),
            new Entry("Invisible", 5, "3=25,5=25,20=25"),
            new Entry("Sneak", 5, "3=20,5=20,20=20"),
            new Entry("Deodorize", 5, "3=15,5=15,20=15"),
            new Entry("Erase", 5, "3=32,20=39"),
            new Entry("Warp II", 5, "4=40"),
            new Entry("Tractor", 157, "4=25,8=32"),
            new Entry("Embrava", 29, "20=5"),
            new Entry("Wild Carrot", 5, "16=30"),
            new Entry("Flurry", 29, "5=48"),
        };

        /// <summary>Enemy-targetable hate spells (cast-on-enemy mode; GEO natives always listed).</summary>
        public static readonly IReadOnlyList<Entry> EnemySpells = new[]
        {
            new Entry("Dia", 32, "3=3,5=1"),
            new Entry("Dia II", 32, "3=36,5=31"),
            new Entry("Banish", 32, "3=5,7=7"),
            new Entry("Banish II", 32, "3=30,7=34"),
            new Entry("Diaga", 32, "3=18,5=15"),
            new Entry("Diaga II", 32, "3=52,5=45"),
            new Entry("Banishga", 32, "3=15,7=30"),
            new Entry("Banishga II", 32, "3=40"),
            new Entry("Slow", 32, "3=13,5=13"),
            new Entry("Paralyze", 32, "3=4,5=6"),
            new Entry("Silence", 32, "3=15,5=18"),
            new Entry("Repose", 32, "3=48"),
            new Entry("Flash", 32, "3=45,7=37,22=45"),
            new Entry("Fire", 32, "4=13,5=19,8=23,20=16,21=19"),
            new Entry("Fire II", 32, "4=38,5=50,8=60,20=42,21=46"),
            new Entry("Fire III", 32, "4=62,5=71,8=88,20=63,21=67"),
            new Entry("Fire IV", 32, "4=73,5=86,20=73,21=85"),
            new Entry("Blizzard", 32, "4=17,5=24,8=29,20=20,21=24"),
            new Entry("Blizzard II", 32, "4=42,5=55,8=66,20=46,21=50"),
            new Entry("Blizzard III", 32, "4=64,5=73,8=92,20=66,21=70"),
            new Entry("Blizzard IV", 32, "4=74,5=89,20=74,21=88"),
            new Entry("Aero", 32, "4=9,5=14,8=17,20=12,21=14"),
            new Entry("Aero II", 32, "4=34,5=45,8=54,20=38,21=42"),
            new Entry("Aero III", 32, "4=59,5=69,8=84,20=60,21=64"),
            new Entry("Aero IV", 32, "4=72,5=83,20=72,21=82"),
            new Entry("Stone", 32, "4=1,5=4,8=5,20=4,21=4"),
            new Entry("Stone II", 32, "4=26,5=35,8=42,20=30,21=34"),
            new Entry("Stone III", 32, "4=51,5=65,8=76,20=54,21=58"),
            new Entry("Stone IV", 32, "4=68,5=77,20=70,21=76"),
            new Entry("Thunder", 32, "4=21,5=29,8=35,20=24,21=29"),
            new Entry("Thunder II", 32, "4=46,5=60,8=72,20=51,21=54"),
            new Entry("Thunder III", 32, "4=66,5=75,8=96,20=69,21=73"),
            new Entry("Thunder IV", 32, "4=75,5=92,20=75,21=91"),
            new Entry("Water", 32, "4=5,5=9,8=11,20=8,21=9"),
            new Entry("Water II", 32, "4=30,5=40,8=48,20=34,21=38"),
            new Entry("Water III", 32, "4=55,5=67,8=80,20=57,21=61"),
            new Entry("Water IV", 32, "4=70,5=80,20=71,21=79"),
            new Entry("Firaga", 32, "4=28"),
            new Entry("Blizzaga", 32, "4=32"),
            new Entry("Aeroga", 32, "4=23"),
            new Entry("Aeroga II", 32, "4=48"),
            new Entry("Stonega", 32, "4=15"),
            new Entry("Stonega II", 32, "4=40"),
            new Entry("Thundaga", 32, "4=36"),
            new Entry("Waterga", 32, "4=19"),
            new Entry("Waterga II", 32, "4=44"),
            new Entry("Gravity", 32, "5=21"),
            new Entry("Poison", 32, "4=3,5=5,8=6"),
            new Entry("Poison II", 32, "4=43,5=46,8=46"),
            new Entry("Poisonga", 32, "4=24,8=26"),
            new Entry("Bio", 32, "4=10,5=10,8=15"),
            new Entry("Bio II", 32, "4=35,5=36,8=40"),
            new Entry("Burn", 32, "4=24"),
            new Entry("Frost", 32, "4=22"),
            new Entry("Choke", 32, "4=20"),
            new Entry("Rasp", 32, "4=18"),
            new Entry("Shock", 32, "4=16"),
            new Entry("Drown", 32, "4=27"),
            new Entry("Drain", 32, "4=12,8=10,20=21,21=15"),
            new Entry("Aspir", 32, "4=25,8=20,20=36,21=30"),
            new Entry("Aspir II", 32, "4=83,8=78,20=97,21=90"),
            new Entry("Stun", 32, "4=45,8=37"),
            new Entry("Sleep", 32, "4=20,5=25,8=30,20=30,21=35"),
            new Entry("Blind", 32, "4=4,5=8"),
            new Entry("Virus", 32, "4=1"),
            new Entry("Curse", 32, "4=1"),
            new Entry("Bind", 32, "4=7,5=11,8=20"),
            new Entry("Sleep II", 32, "4=41,5=46,8=56,20=65,21=70"),
            new Entry("Dispel", 32, "5=32,20=32"),
            new Entry("Absorb-STR", 32, "8=43"),
            new Entry("Absorb-DEX", 32, "8=41"),
            new Entry("Absorb-VIT", 32, "8=35"),
            new Entry("Absorb-AGI", 32, "8=37"),
            new Entry("Absorb-INT", 32, "8=39"),
            new Entry("Absorb-MND", 32, "8=31"),
            new Entry("Absorb-CHR", 32, "8=33"),
            new Entry("Sleepga", 32, "4=31"),
            new Entry("Absorb-TP", 32, "8=45"),
            new Entry("Geohelix", 32, "20=18"),
            new Entry("Hydrohelix", 32, "20=20"),
            new Entry("Anemohelix", 32, "20=22"),
            new Entry("Pyrohelix", 32, "20=24"),
            new Entry("Cryohelix", 32, "20=26"),
            new Entry("Ionohelix", 32, "20=28"),
            new Entry("Noctohelix", 32, "20=30"),
            new Entry("Luminohelix", 32, "20=32"),
            new Entry("Katon: Ichi", 32, "13=15"),
            new Entry("Katon: Ni", 32, "13=40"),
            new Entry("Hyoton: Ichi", 32, "13=15"),
            new Entry("Hyoton: Ni", 32, "13=40"),
            new Entry("Huton: Ichi", 32, "13=15"),
            new Entry("Huton: Ni", 32, "13=40"),
            new Entry("Doton: Ichi", 32, "13=15"),
            new Entry("Doton: Ni", 32, "13=40"),
            new Entry("Raiton: Ichi", 32, "13=15"),
            new Entry("Raiton: Ni", 32, "13=40"),
            new Entry("Suiton: Ichi", 32, "13=15"),
            new Entry("Suiton: Ni", 32, "13=40"),
            new Entry("Jubaku: Ichi", 32, "13=30"),
            new Entry("Hojo: Ichi", 32, "13=23"),
            new Entry("Hojo: Ni", 32, "13=48"),
            new Entry("Kurayami: Ichi", 32, "13=19"),
            new Entry("Kurayami: Ni", 32, "13=44"),
            new Entry("Dokumori: Ichi", 32, "13=27"),
            new Entry("Dispelga", 32, "3=99,4=99,5=99,10=99,15=99,20=99,21=99"),
            new Entry("Foe Requiem", 32, "10=7"),
            new Entry("Foe Requiem II", 32, "10=17"),
            new Entry("Foe Requiem III", 32, "10=37"),
            new Entry("Foe Requiem IV", 32, "10=47"),
            new Entry("Horde Lullaby", 32, "10=27"),
            new Entry("Battlefield Elegy", 32, "10=39"),
            new Entry("Fire Threnody", 32, "10=20"),
            new Entry("Ice Threnody", 32, "10=22"),
            new Entry("Wind Threnody", 32, "10=18"),
            new Entry("Earth Threnody", 32, "10=14"),
            new Entry("Ltng. Threnody", 32, "10=24"),
            new Entry("Water Threnody", 32, "10=16"),
            new Entry("Light Threnody", 32, "10=10"),
            new Entry("Dark Threnody", 32, "10=12"),
            new Entry("Magic Finale", 32, "10=33"),
            new Entry("Foe Lullaby", 32, "10=16"),
            new Entry("Kaustra", 32, "20=5"),
            new Entry("Impact", 32, "3=90,4=90,5=90,8=90,15=90,20=90,21=90"),
            new Entry("Venom Shell", 32, "16=42"),
            new Entry("Screwdriver", 32, "16=26"),
            new Entry("MP Drainkiss", 32, "16=42"),
            new Entry("Death Ray", 32, "16=34"),
            new Entry("Sandspin", 32, "16=1"),
            new Entry("Smite of Rage", 32, "16=34"),
            new Entry("Bludgeon", 32, "16=18"),
            new Entry("Blitzstrahl", 32, "16=44"),
            new Entry("Mysterious Light", 32, "16=40"),
            new Entry("Poison Breath", 32, "16=22"),
            new Entry("Stinking Gas", 32, "16=44"),
            new Entry("Terror Touch", 32, "16=40"),
            new Entry("Blood Saber", 32, "16=48"),
            new Entry("Digest", 32, "16=36"),
            new Entry("Mandibular Bite", 32, "16=44"),
            new Entry("Cursed Sphere", 32, "16=18"),
            new Entry("Sickle Slash", 32, "16=48"),
            new Entry("Power Attack", 32, "16=4"),
            new Entry("Magnetite Cloud", 32, "16=46"),
            new Entry("Helldive", 32, "16=16"),
            new Entry("Jet Stream", 32, "16=38"),
            new Entry("Blood Drain", 32, "16=20"),
            new Entry("Sound Blast", 32, "16=32"),
            new Entry("Jettatura", 32, "16=48"),
            new Entry("Foot Kick", 32, "16=1"),
            new Entry("Chaotic Eye", 32, "16=32"),
            new Entry("Sheep Song", 32, "16=16"),
            new Entry("Claw Cyclone", 32, "16=20"),
            new Entry("Blank Gaze", 32, "16=38"),
            new Entry("Uppercut", 32, "16=38"),
            new Entry("Pinecone Bomb", 32, "16=36"),
            new Entry("Sprout Smack", 32, "16=4"),
            new Entry("Soporific", 32, "16=24"),
            new Entry("Queasyshroom", 32, "16=8"),
            new Entry("Wild Oats", 32, "16=4"),
            new Entry("Geist Wall", 32, "16=46"),
            new Entry("Awful Eye", 32, "16=46"),
            new Entry("Blastbomb", 32, "16=18"),
            new Entry("Battle Dance", 32, "16=12"),
            new Entry("Grand Slam", 32, "16=30"),
            new Entry("Head Butt", 32, "16=12"),
            new Entry("Bomb Toss", 32, "16=28"),
            new Entry("Feather Storm", 32, "16=12"),
            new Entry("Fira", 32, "21=40"),
            new Entry("Fira II", 32, "21=85"),
            new Entry("Blizzara", 32, "21=45"),
            new Entry("Blizzara II", 32, "21=90"),
            new Entry("Aera", 32, "21=35"),
            new Entry("Aera II", 32, "21=80"),
            new Entry("Stonera", 32, "21=25"),
            new Entry("Stonera II", 32, "21=70"),
            new Entry("Thundara", 32, "21=50"),
            new Entry("Thundara II", 32, "21=95"),
            new Entry("Watera", 32, "21=30"),
            new Entry("Watera II", 32, "21=75"),
            new Entry("Distract", 32, "5=35"),
            new Entry("Frazzle", 32, "5=42"),
        };

        /// <summary>
        /// Dropdown content for one mode and sub job (0 = Any). Enemy mode always
        /// includes the GEO-native spells; a chosen sub job filters the rest.
        /// </summary>
        public static List<string> For(bool enemyMode, int subJobId)
        {
            var result = new List<string>();
            foreach (Entry entry in enemyMode ? EnemySpells : PlayerSpells)
            {
                bool include = enemyMode && entry.GeoNative
                    ? true
                    : entry.UsableWithSub(subJobId);
                if (enemyMode && subJobId == 0)
                {
                    include = true; // Any: GEO natives + every sub-job spell
                }

                if (include)
                {
                    result.Add(entry.Spell);
                }
            }

            result.Sort(System.StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
