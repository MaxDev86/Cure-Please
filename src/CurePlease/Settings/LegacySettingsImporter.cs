using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CurePlease.Settings
{
    /// <summary>
    /// Imports legacy CurePlease 2.x XML settings files (hand-rolled serialization of
    /// Form2.MySettings, ~180 flat camelCase elements) into the new sectioned model.
    /// Unknown or obsolete elements are ignored and counted in the report; the import
    /// never throws on unexpected content.
    /// </summary>
    public static class LegacySettingsImporter
    {
        public sealed class ImportResult
        {
            public CurePleaseSettings Settings { get; set; }
            public int MappedCount { get; set; }
            public int IgnoredCount { get; set; }
            public List<string> Warnings { get; } = new List<string>();
        }

        public static ImportResult Import(string path)
        {
            XDocument doc = XDocument.Load(path);
            XElement root = doc.Root;
            if (root == null || !root.Name.LocalName.Equals("MySettings", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Not a legacy CurePlease settings file (expected <MySettings>).");
            }

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in root.Elements())
            {
                values[element.Name.LocalName] = element.Value;
            }

            ImportResult result = new ImportResult { Settings = new CurePleaseSettings() };
            CurePleaseSettings settings = result.Settings;
            int mapped = 0;

            // --- Cure tiers (Tiers[0] = Cure VI .. Tiers[5] = Cure) ---
            string[] cureFields = { "cure6", "cure5", "cure4", "cure3", "cure2", "cure1" };
            for (int i = 0; i < cureFields.Length; i++)
            {
                bool enabled = GetBool(values, cureFields[i] + "enabled", ref mapped);
                uint amount = GetUInt(values, cureFields[i] + "amount", ref mapped);
                settings.Cures.Tiers[i].Enabled = enabled;
                settings.Cures.Tiers[i].MinimumHpLoss = amount;
            }

            // --- Curaga tiers (Tiers[0] = Curaga V .. Tiers[4] = Curaga) ---
            // Legacy names: curaga5enabled/curaga5Amount .. curagaEnabled/curagaAmount.
            string[] curagaEnabledFields = { "curaga5enabled", "curaga4enabled", "curaga3enabled", "curaga2enabled", "curagaEnabled" };
            string[] curagaAmountFields = { "curaga5Amount", "curaga4Amount", "curaga3Amount", "curaga2Amount", "curagaAmount" };
            for (int i = 0; i < curagaEnabledFields.Length; i++)
            {
                bool enabled = GetBool(values, curagaEnabledFields[i], ref mapped);
                uint amount = GetUInt(values, curagaAmountFields[i], ref mapped);
                settings.Curaga.Tiers[i].Enabled = enabled;
                settings.Curaga.Tiers[i].MinimumHpLoss = amount;
            }

            // --- Thresholds ---
            settings.Cures.CurePercentage = GetByte(values, "curePercentage", ref mapped);
            settings.Cures.PriorityCurePercentage = GetByte(values, "priorityCurePercentage", ref mapped);
            settings.Cures.MonitoredCurePercentage = GetByte(values, "monitoredCurePercentage", ref mapped);
            settings.Curaga.CurePercentage = GetByte(values, "curagaCurePercentage", ref mapped);
            settings.Curaga.RequiredMembers = GetInt(values, "curagaRequiredMembers", ref mapped);
            settings.Curaga.Enabled = settings.Curaga.Tiers.Any(t => t.Enabled);

            // --- Tier fallback policy ---
            settings.Cures.Undercure = GetBool(values, "Undercure", ref mapped);
            settings.Cures.Overcure = GetBool(values, "Overcure", ref mapped);
            settings.Cures.OvercureOnHighPriority = GetBool(values, "OvercureOnHighPriority", ref mapped);

            // --- General ---
            settings.General.EnableOutOfPartyHealing = GetBool(values, "enableOutOfPartyHealing", ref mapped);

            // --- Buff upkeep intervals and spell tiers ---
            settings.Buffs.AutoHasteMinutes = GetDouble(values, "autoHasteMinutes", ref mapped);
            settings.Buffs.AutoProtectMinutes = GetDouble(values, "autoProtect_Minutes", ref mapped);
            settings.Buffs.AutoShellMinutes = GetDouble(values, "autoShellMinutes", ref mapped);

            string[] protectTiers = { "Protect", "Protect II", "Protect III", "Protect IV", "Protect V" };
            string[] shellTiers = { "Shell", "Shell II", "Shell III", "Shell IV", "Shell V" };
            int protectIdx = GetInt(values, "autoProtect_Spell", ref mapped);
            int shellIdx = GetInt(values, "autoShell_Spell", ref mapped);
            if (protectIdx >= 0 && protectIdx < protectTiers.Length)
            {
                settings.Buffs.ProtectSpell = protectTiers[protectIdx];
            }

            if (shellIdx >= 0 && shellIdx < shellTiers.Length)
            {
                settings.Buffs.ShellSpell = shellTiers[shellIdx];
            }

            // --- Debuff removals: union of the party na* and monitored* checkboxes ---
            Dictionary<string, string> debuffMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Poison", "Poison" }, { "Paralysis", "Paralysis" }, { "Blindness", "Blindness" },
                { "Silence", "Silence" }, { "Curse", "Curse" }, { "Disease", "Disease" },
                { "Petrification", "Petrification" }, { "Doom", "Doom" }, { "Amnesia", "Amnesia" },
                { "Plague", "Disease" }, { "Bane", "Curse" },
            };

            HashSet<string> removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in debuffMap)
            {
                int ignored = 0;
                bool partyEnabled = GetBool(values, "na" + pair.Key, ref ignored);
                bool monitoredEnabled = GetBool(values, "monitored" + pair.Key, ref ignored);
                if (partyEnabled || monitoredEnabled)
                {
                    removals.Add(pair.Value);
                    mapped++;
                }
            }

            // naErase enables the whole Erase group.
            if (GetBool(values, "naErase", ref mapped))
            {
                removals.UnionWith(new[] { "Slow", "Bind", "Weight", "Bio", "Dia", "Burn", "Flash", "STR_Down" });
            }

            if (removals.Count > 0)
            {
                settings.Debuffs.EnabledRemovals = removals.ToList();
            }

            // --- Bard songs (legacy song1-4/dummy1-2 are combo indices into a fixed list) ---
            string[] songFields = { "song1", "song2", "song3", "song4" };
            foreach (string field in songFields)
            {
                string song = LegacySongName(GetInt(values, field, ref mapped));
                if (song != null)
                {
                    settings.Bard.PartySongs.Add(song);
                }
            }

            string[] dummyFields = { "dummy1", "dummy2" };
            foreach (string field in dummyFields)
            {
                string song = LegacySongName(GetInt(values, field, ref mapped));
                if (song != null)
                {
                    settings.Bard.DummySongs.Add(song);
                }
            }

            bool troubadour = GetBool(values, "Troubadour", ref mapped);
            bool nightingale = GetBool(values, "Nightingale", ref mapped);
            settings.Bard.UseNitro = troubadour || nightingale;
            settings.Bard.UseMarcato = GetBool(values, "Marcato", ref mapped);

            double recastSongMinutes = GetDouble(values, "recastSongTime", ref mapped);
            if (recastSongMinutes > 0)
            {
                settings.Bard.BaseDurationSeconds = recastSongMinutes * 60;
            }

            settings.Bard.Enabled = settings.Bard.PartySongs.Count > 0;

            // --- Geomancer (legacy *_Spell ints are indices into the fixed GeomancerInfo pair table) ---
            GeoSettings geo = settings.Geo;
            bool indiEnabled = GetBool(values, "EnableGeoSpells", ref mapped);
            if (values.ContainsKey("IndiWhenEngaged"))
            {
                geo.IndiOnlyWhenEngaged = GetBool(values, "IndiWhenEngaged", ref mapped);
            }

            geo.LuopanEnabled = GetBool(values, "EnableLuopanSpells", ref mapped);

            if (indiEnabled)
            {
                geo.IndiSpell = LegacyGeoSpell(GetInt(values, "IndiSpell_Spell", ref mapped), "Indi-") ?? string.Empty;
            }

            if (geo.LuopanEnabled)
            {
                geo.GeoSpell = LegacyGeoSpell(GetInt(values, "GeoSpell_Spell", ref mapped), "Geo-") ?? string.Empty;
            }

            geo.EntrustSpell = LegacyGeoSpell(GetInt(values, "EntrustedSpell_Spell", ref mapped), "Indi-") ?? string.Empty;
            geo.EntrustTarget = GetString(values, "EntrustedSpell_Target", ref mapped);

            // Legacy LuopanSpell_Target was dual-purpose: the enemy name when
            // specifiedEngageTarget was set, otherwise the luopan placement character.
            string luopanTarget = GetString(values, "LuopanSpell_Target", ref mapped);
            if (GetBool(values, "specifiedEngageTarget", ref mapped))
            {
                geo.EngagedTargetName = luopanTarget;
            }
            else
            {
                geo.LuopanTarget = luopanTarget;
            }

            geo.UseEntrust = GetBool(values, "Entrust", ref mapped);
            geo.UseBlazeOfGlory = GetBool(values, "BlazeOfGlory", ref mapped);
            geo.UseDematerialize = GetBool(values, "Dematerialize", ref mapped);
            geo.UseEclipticAttrition = GetBool(values, "EclipticAttrition", ref mapped);
            if (values.ContainsKey("LifeCycle"))
            {
                geo.UseLifeCycle = GetBool(values, "LifeCycle", ref mapped);
            }

            if (values.ContainsKey("FullCircle"))
            {
                geo.UseFullCircle = GetBool(values, "FullCircle", ref mapped);
            }

            geo.UseRadialArcana = GetBool(values, "RadialArcana", ref mapped);
            uint radialMp = GetUInt(values, "RadialArcanaMP", ref mapped);
            if (radialMp > 0)
            {
                geo.RadialArcanaMpBelow = radialMp;
            }

            geo.Enabled = indiEnabled || geo.LuopanEnabled;

            // --- Report the rest as ignored ---
            result.MappedCount = mapped;
            result.IgnoredCount = values.Count - mapped;

            if (GetBool(values, "enableMonitoredPriority", ref mapped))
            {
                result.Warnings.Add("enableMonitoredPriority: legacy hard veto is replaced by the MonitoredWeight score multiplier.");
            }

            if (GetBool(values, "PrioritiseOverLowerTier", ref mapped))
            {
                result.Warnings.Add("PrioritiseOverLowerTier: obsolete — debuff removal now competes by severity score.");
            }

            if (GetBool(values, "EnableAddOn", ref mapped))
            {
                result.Warnings.Add("EnableAddOn: addon-based party debuff tracking is not ported yet (P2).");
            }

            result.Warnings.Add("Per-member buff selections (haste/protect/shell per slot), JA toggles, and item settings are not mapped yet — they arrive with their feature modules.");

            if (GetBool(values, "Pianissimo", ref mapped))
            {
                result.Warnings.Add("Pianissimo: legacy stored no song/target pairs — configure Bard.PianissimoTargets manually.");
            }

            return result;
        }

        /// <summary>
        /// Legacy song combo index -> canonical song name. Indices come from the fixed
        /// Form2 combo list (separators occupy 6/12/19/22/25/40/43/47/51/68/70; 0 = empty).
        /// Legacy typos/trailing spaces are corrected ("Valor Minuet ", "Godess's Hymnus").
        /// </summary>
        private static string LegacySongName(int index)
        {
            switch (index)
            {
                case 1: return "Knight's Minne";
                case 2: return "Knight's Minne II";
                case 3: return "Knight's Minne III";
                case 4: return "Knight's Minne IV";
                case 5: return "Knight's Minne V";
                case 7: return "Valor Minuet";
                case 8: return "Valor Minuet II";
                case 9: return "Valor Minuet III";
                case 10: return "Valor Minuet IV";
                case 11: return "Valor Minuet V";
                case 13: return "Army's Paeon";
                case 14: return "Army's Paeon II";
                case 15: return "Army's Paeon III";
                case 16: return "Army's Paeon IV";
                case 17: return "Army's Paeon V";
                case 18: return "Army's Paeon VI";
                case 20: return "Sword Madrigal";
                case 21: return "Blade Madrigal";
                case 23: return "Hunter's Prelude";
                case 24: return "Archer's Prelude";
                case 26: return "Sinewy Etude";
                case 27: return "Dextrous Etude";
                case 28: return "Vivacious Etude";
                case 29: return "Quick Etude";
                case 30: return "Learned Etude";
                case 31: return "Spirited Etude";
                case 32: return "Enchanting Etude";
                case 33: return "Herculean Etude";
                case 34: return "Uncanny Etude";
                case 35: return "Vital Etude";
                case 36: return "Swift Etude";
                case 37: return "Sage Etude";
                case 38: return "Logical Etude";
                case 39: return "Bewitching Etude";
                case 41: return "Sheepfoe Mambo";
                case 42: return "Dragonfoe Mambo";
                case 44: return "Mage's Ballad";
                case 45: return "Mage's Ballad II";
                case 46: return "Mage's Ballad III";
                case 48: return "Advancing March";
                case 49: return "Victory March";
                case 50: return "Honor March";
                case 52: return "Fire Carol";
                case 53: return "Fire Carol II";
                case 54: return "Ice Carol";
                case 55: return "Ice Carol II";
                case 56: return "Wind Carol";
                case 57: return "Wind Carol II";
                case 58: return "Earth Carol";
                case 59: return "Earth Carol II";
                case 60: return "Lightning Carol";
                case 61: return "Lightning Carol II";
                case 62: return "Water Carol";
                case 63: return "Water Carol II";
                case 64: return "Light Carol";
                case 65: return "Light Carol II";
                case 66: return "Dark Carol";
                case 67: return "Dark Carol II";
                case 69: return "Goddess's Hymnus";
                case 71: return "Sentinel's Scherzo";
                default: return null;
            }
        }

        /// <summary>
        /// Legacy geo combo index -> spell name. Indices come from the fixed
        /// GeomancerInfo table in legacy Form1.cs (geo_position order).
        /// </summary>
        private static string LegacyGeoSpell(int index, string prefix)
        {
            string[] suffixes =
            {
                "Voidance", "Precision", "Regen", "Haste", "Attunement", "Focus", "Barrier", "Refresh",
                "CHR", "MND", "Fury", "INT", "AGI", "Fend", "VIT", "DEX", "Acumen", "STR",
                "Poison", "Slow", "Torpor", "Slip", "Languor", "Paralysis", "Vex", "Frailty",
                "Wilt", "Malaise", "Gravity", "Fade",
            };

            return index >= 0 && index < suffixes.Length ? prefix + suffixes[index] : null;
        }

        private static string GetString(Dictionary<string, string> values, string key, ref int mapped)
        {
            if (values.TryGetValue(key, out string raw))
            {
                mapped++;
                return raw ?? string.Empty;
            }

            return string.Empty;
        }

        private static bool GetBool(Dictionary<string, string> values, string key, ref int mapped)
        {
            if (values.TryGetValue(key, out string raw))
            {
                mapped++;
                return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
            }

            return false;
        }

        private static uint GetUInt(Dictionary<string, string> values, string key, ref int mapped)
        {
            if (values.TryGetValue(key, out string raw))
            {
                mapped++;
                if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v))
                {
                    return v;
                }
            }

            return 0;
        }

        private static int GetInt(Dictionary<string, string> values, string key, ref int mapped)
        {
            if (values.TryGetValue(key, out string raw))
            {
                mapped++;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    return v;
                }
            }

            return 0;
        }

        private static byte GetByte(Dictionary<string, string> values, string key, ref int mapped)
        {
            if (values.TryGetValue(key, out string raw))
            {
                mapped++;
                if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v))
                {
                    return v;
                }
            }

            return 0;
        }

        private static double GetDouble(Dictionary<string, string> values, string key, ref int mapped)
        {
            if (values.TryGetValue(key, out string raw))
            {
                mapped++;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    return v;
                }
            }

            return 0;
        }
    }
}
