#!/usr/bin/env python3
"""Generates CurePlease Core/EnmitySpellCatalog.cs from Windower's spells.lua.

Two lists for the GEO enmity-assist dropdowns:
  PlayerSpells: party-targetable spells obtainable from a sub job (level <= 49)
  EnemySpells:  enemy-targetable spells — GEO native (any level) or sub job <= 49
Geo-*/Indi-* spells are excluded from both (they are luopon placement, not hate spells).
"""
import re, sys

JOB_NAMES = {1:"WAR",2:"MNK",3:"WHM",4:"BLM",5:"RDM",6:"THF",7:"PLD",8:"DRK",9:"BST",10:"BRD",
             11:"RNG",12:"SAM",13:"NIN",14:"DRG",15:"SMN",16:"BLU",17:"COR",18:"PUP",19:"DNC",
             20:"SCH",21:"GEO",22:"RUN"}

src = open(sys.argv[1], encoding="utf-8").read()

entry_re = re.compile(
    r'\{\s*id=\d+,\s*en="(?P<en>[^"]+)",.*?levels=\{(?P<levels>[^}]*)\}.*?targets=(?P<targets>\d+),\s*type="(?P<type>[^"]+)"',
    re.S)

player, enemy = [], []
seen = set()
for m in entry_re.finditer(src):
    name = m.group("en")
    if m.group("type") == "Trust" or name.startswith(("Geo-", "Indi-")) or name in seen:
        continue
    levels = {int(j): int(l) for j, l in re.findall(r"\[(\d+)\]=(\d+)", m.group("levels"))}
    levels = {j: l for j, l in levels.items() if j in JOB_NAMES and l <= 99}
    if not levels:
        continue
    seen.add(name)
    targets = int(m.group("targets"))
    lvl = ",".join(f"{j}={l}" for j, l in sorted(levels.items()))
    geo_native = 21 in levels
    sub_min = min((l for j, l in levels.items() if j != 21), default=99)
    row = f'            new Entry("{name}", {targets}, "{lvl}"),'
    # Strictly hostile: enemy bit set, no player/party bits (Cure = 63 is friendly
    # despite the enemy bit — undead nuke edge case, noise for a hate spell).
    if targets & 32 and not targets & 6:
        if geo_native or sub_min <= 49:
            enemy.append(row)
    if targets & 6:  # player(2)/party(4) targetable; self-only (1) excluded
        if sub_min <= 49:
            player.append(row)

out = f"""using System.Collections.Generic;

namespace CurePlease.Core
{{
    /// <summary>
    /// Spell lists for the GEO enmity-assist dropdowns, generated from Windower's
    /// spells.lua (tools: gen_enmity_catalog.py). PlayerSpells = party-targetable
    /// spells a sub job can grant (level &lt;= 49); EnemySpells = enemy-targetable
    /// spells, GEO native (any level) or sub job (&lt;= 49). Geo-*/Indi-* are
    /// excluded — they are luopon placement, not hate spells.
    /// Levels format: "jobId=level,..." with Windower job ids (3=WHM, 5=RDM, 21=GEO...).
    /// </summary>
    public static class EnmitySpellCatalog
    {{
        /// <summary>
        /// Sub-job filter dropdown content: "Any" + the 22 job abbreviations.
        /// The list index IS the Windower job id (0 = Any).
        /// </summary>
        public static readonly IReadOnlyList<string> JobNames = new[]
        {{
            "Any", "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK", "BST", "BRD",
            "RNG", "SAM", "NIN", "DRG", "SMN", "BLU", "COR", "PUP", "DNC", "SCH", "GEO", "RUN",
        }};

        public sealed class Entry
        {{
            public string Spell {{ get; }}
            public ushort Targets {{ get; }}
            public string Levels {{ get; }}

            public Entry(string spell, ushort targets, string levels)
            {{
                Spell = spell;
                Targets = targets;
                Levels = levels;
            }}

            /// <summary>Level for the given job id; 0 when the job never learns it.</summary>
            public int LevelFor(int jobId)
            {{
                string needle = jobId + "=";
                foreach (string pair in Levels.Split(','))
                {{
                    if (pair.StartsWith(needle, System.StringComparison.Ordinal))
                    {{
                        return int.Parse(pair.Substring(needle.Length));
                    }}
                }}

                return 0;
            }}

            /// <summary>Learned by GEO natively (always available regardless of sub job).</summary>
            public bool GeoNative => LevelFor(21) > 0;

            /// <summary>Obtainable from this sub job (level &lt;= 49); 0 = any sub job.</summary>
            public bool UsableWithSub(int subJobId)
            {{
                if (subJobId == 0)
                {{
                    return true;
                }}

                int level = LevelFor(subJobId);
                return level > 0 && level <= 49;
            }}
        }}

        /// <summary>Party-targetable hate spells (cure-the-tank mode).</summary>
        public static readonly IReadOnlyList<Entry> PlayerSpells = new[]
        {{
{chr(10).join(player)}
        }};

        /// <summary>Enemy-targetable hate spells (cast-on-enemy mode; GEO natives always listed).</summary>
        public static readonly IReadOnlyList<Entry> EnemySpells = new[]
        {{
{chr(10).join(enemy)}
        }};

        /// <summary>
        /// Dropdown content for one mode and sub job (0 = Any). Enemy mode always
        /// includes the GEO-native spells; a chosen sub job filters the rest.
        /// </summary>
        public static List<string> For(bool enemyMode, int subJobId)
        {{
            var result = new List<string>();
            foreach (Entry entry in enemyMode ? EnemySpells : PlayerSpells)
            {{
                bool include = enemyMode && entry.GeoNative
                    ? true
                    : entry.UsableWithSub(subJobId);
                if (enemyMode && subJobId == 0)
                {{
                    include = true; // Any: GEO natives + every sub-job spell
                }}

                if (include)
                {{
                    result.Add(entry.Spell);
                }}
            }}

            result.Sort(System.StringComparer.OrdinalIgnoreCase);
            return result;
        }}
    }}
}}
"""

open(sys.argv[2], "w", newline="\n").write(out)
print(f"player={len(player)} enemy={len(enemy)}")
