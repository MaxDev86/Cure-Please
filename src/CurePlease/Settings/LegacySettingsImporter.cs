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

            // --- Buff upkeep intervals ---
            settings.Buffs.AutoHasteMinutes = GetDouble(values, "autoHasteMinutes", ref mapped);
            settings.Buffs.AutoProtectMinutes = GetDouble(values, "autoProtect_Minutes", ref mapped);
            settings.Buffs.AutoShellMinutes = GetDouble(values, "autoShellMinutes", ref mapped);

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

            result.Warnings.Add("Per-member buff selections (haste/protect/shell per slot), song lists, JA toggles, and item settings are not mapped yet — they arrive with their feature modules.");

            return result;
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
