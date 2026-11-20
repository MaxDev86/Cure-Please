using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CurePlease.Core;

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

            // Legacy fired Divine Seal by the PL's MP; here it doubles critical cures.
            settings.Cures.UseDivineSeal = GetBool(values, "divineSeal", ref mapped);

            // --- General ---
            settings.General.EnableOutOfPartyHealing = GetBool(values, "enableOutOfPartyHealing", ref mapped);

            // --- Buff upkeep: tier + interval per spell, mapped onto catalog entries ---
            // Legacy stored no enable flags for these (the per-slot checkboxes were
            // runtime-only arrays), so entries keep their defaults (disabled) and the
            // report tells the user to switch on the rows they want.
            string[] protectTiers = { "Protect", "Protect II", "Protect III", "Protect IV", "Protect V" };
            string[] shellTiers = { "Shell", "Shell II", "Shell III", "Shell IV", "Shell V" };
            string[] refreshTiers = { "Refresh", "Refresh II", "Refresh III" };
            string[] regenTiers = { "Regen", "Regen II", "Regen III", "Regen IV", "Regen V" };
            string[] stormTiers =
            {
                "Firestorm", "Sandstorm", "Rainstorm", "Windstorm",
                "Hailstorm", "Thunderstorm", "Voidstorm", "Aurorastorm",
                "Firestorm II", "Sandstorm II", "Rainstorm II", "Windstorm II",
                "Hailstorm II", "Thunderstorm II", "Voidstorm II", "Aurorastorm II",
            };

            double hasteMinutes = GetDouble(values, "autoHasteMinutes", ref mapped);
            double protectMinutes = GetDouble(values, "autoProtect_Minutes", ref mapped);
            double shellMinutes = GetDouble(values, "autoShellMinutes", ref mapped);
            double refreshMinutes = GetDouble(values, "autoRefresh_Minutes", ref mapped);
            double regenMinutes = GetDouble(values, "autoRegen_Minutes", ref mapped);
            double adloquiumMinutes = GetDouble(values, "autoAdloquiumMinutes", ref mapped);
            double phalanxMinutes = GetDouble(values, "autoPhalanxIIMinutes", ref mapped);
            double stormMinutes = GetDouble(values, "autoStormspellMinutes", ref mapped);

            string protectSpell = Tier(protectTiers, GetInt(values, "autoProtect_Spell", ref mapped));
            string shellSpell = Tier(shellTiers, GetInt(values, "autoShell_Spell", ref mapped));

            MapBuffEntry(settings, "Haste", hasteMinutes);
            MapBuffEntry(settings, protectSpell, protectMinutes);
            MapBuffEntry(settings, shellSpell, shellMinutes);
            MapBuffEntry(settings, Tier(refreshTiers, GetInt(values, "autoRefresh_Spell", ref mapped)), refreshMinutes);
            MapBuffEntry(settings, Tier(regenTiers, GetInt(values, "autoRegen_Spell", ref mapped)), regenMinutes);
            MapBuffEntry(settings, "Adloquium", adloquiumMinutes);
            MapBuffEntry(settings, "Phalanx II", phalanxMinutes);
            MapBuffEntry(settings, Tier(stormTiers, GetInt(values, "autoStorm_Spell", ref mapped)), stormMinutes);

            // --- Debuff removals: union of the party na*, PL pl* and monitored* checkboxes ---
            // Legacy suffix -> our removal name (StatusEffect enum names).
            Dictionary<string, string> debuffMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Poison", "Poison" }, { "Paralysis", "Paralysis" }, { "Blindness", "Blindness" },
                { "Silence", "Silence" }, { "Curse", "Curse" }, { "Curse2", "Curse2" },
                { "Disease", "Disease" }, { "Plague", "Plague" }, { "Bane", "Bane" },
                { "Petrification", "Petrification" }, { "Doom", "Doom" },
                { "Slow", "Slow" }, { "Addle", "Addle" }, { "Bind", "Bind" }, { "Weight", "Weight" },
                { "Bio", "Bio" }, { "Dia", "Dia" }, { "Flash", "Flash" }, { "Helix", "Helix" },
                { "Burn", "Burn" }, { "Frost", "Frost" }, { "Choke", "Choke" }, { "Rasp", "Rasp" },
                { "Shock", "Shock" }, { "Drown", "Drown" },
                { "AttackDown", "Attack_Down" }, { "AccuracyDown", "Accuracy_Down" },
                { "DefenseDown", "Defense_Down" }, { "EvasionDown", "Evasion_Down" },
                { "MagicAccDown", "Magic_Acc_Down" }, { "MagicAtkDown", "Magic_Atk_Down" },
                { "MaxHpDown", "Max_HP_Down" }, { "MaxMpDown", "Max_MP_Down" }, { "MaxTpDown", "Max_TP_Down" },
                { "StrDown", "STR_Down" }, { "DexDown", "DEX_Down" }, { "VitDown", "VIT_Down" },
                { "AgiDown", "AGI_Down" }, { "IntDown", "INT_Down" }, { "MndDown", "MND_Down" },
                { "ChrDown", "CHR_Down" },
                { "Requiem", "Requiem" }, { "Elegy", "Elegy" }, { "Threnody", "Threnody" },
            };

            HashSet<string> removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in debuffMap)
            {
                int ignored = 0;
                bool enabled = GetBool(values, "na" + pair.Key, ref ignored)
                    || GetBool(values, "pl" + pair.Key, ref ignored)
                    || GetBool(values, "monitored" + pair.Key, ref ignored);
                if (enabled)
                {
                    removals.Add(pair.Value);
                    mapped++;
                }
            }

            // naErase enables the whole Erase group.
            if (GetBool(values, "naErase", ref mapped))
            {
                removals.UnionWith(new[]
                {
                    "Slow", "Addle", "Bind", "Weight", "Bio", "Dia", "Flash", "Helix",
                    "Burn", "Frost", "Choke", "Rasp", "Shock", "Drown",
                    "Attack_Down", "Accuracy_Down", "Defense_Down", "Evasion_Down",
                    "Magic_Acc_Down", "Magic_Atk_Down", "Magic_Def_Down",
                    "Max_HP_Down", "Max_MP_Down", "Max_TP_Down",
                    "STR_Down", "DEX_Down", "VIT_Down", "AGI_Down", "INT_Down", "MND_Down", "CHR_Down",
                    "Requiem", "Elegy", "Threnody",
                });
            }

            settings.Debuffs.EnabledRemovals = removals.ToList();
            settings.Debuffs.Enabled = GetBool(values, "plDebuffEnabled", ref mapped)
                || GetBool(values, "monitoredDebuffEnabled", ref mapped)
                || GetBool(values, "enablePartyDebuffRemoval", ref mapped);

            // Amnesia was Esuna-removable once; SE removed that. Flag stale configs.
            int amnesiaIgnored = 0;
            if (GetBool(values, "naAmnesia", ref amnesiaIgnored)
                || GetBool(values, "plAmnesia", ref amnesiaIgnored)
                || GetBool(values, "monitoredAmnesia", ref amnesiaIgnored))
            {
                result.Warnings.Add("Amnesia removal: no longer possible in-game (Esuna does not remove it) — dropped.");
            }

            // --- Wake-sleep (cure-based) ---
            settings.Debuffs.WakeSleepEnabled = GetBool(values, "wakeSleepEnabled", ref mapped);
            string[] wakeSpells = { "Cure", "Cura", "Curaga" };
            string wakeSpell = Tier(wakeSpells, GetInt(values, "wakeSleepSpell", ref mapped));
            if (wakeSpell != null)
            {
                settings.Debuffs.WakeSleepSpell = wakeSpell;
            }

            int sleepIgnored = 0;
            if (settings.Debuffs.WakeSleepEnabled
                || GetBool(values, "monitoredSleep", ref sleepIgnored)
                || GetBool(values, "monitoredSleep2", ref sleepIgnored))
            {
                if (!settings.Debuffs.EnabledRemovals.Contains("Sleep"))
                {
                    settings.Debuffs.EnabledRemovals.Add("Sleep");
                }
            }

            // --- PL self-removal items (silence / doom) ---
            settings.Debuffs.PlSilenceItemEnabled = GetBool(values, "plSilenceItemEnabled", ref mapped);
            string[] silenceItems = { "Catholicon", "Echo Drops", "Remedy", "Remedy Ointment", "Vicar's Drink" };
            int silenceItemIndex = GetInt(values, "plSilenceItem", ref mapped);
            if (silenceItemIndex == 3)
            {
                result.Warnings.Add("plSilenceItem: Remedy Ointment no longer exists in the game data — using Remedy.");
                settings.Debuffs.PlSilenceItem = "Remedy";
            }
            else
            {
                string silenceItem = Tier(silenceItems, silenceItemIndex);
                if (silenceItem != null)
                {
                    settings.Debuffs.PlSilenceItem = silenceItem;
                }
            }

            settings.Debuffs.PlDoomItemEnabled = GetBool(values, "plDoomEnabled", ref mapped);
            string[] doomItems = { "Holy Water", "Hallowed Water" };
            string doomItem = Tier(doomItems, GetInt(values, "plDoomitem", ref mapped));
            if (doomItem != null)
            {
                settings.Debuffs.PlDoomItem = doomItem;
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

            settings.Bard.Enabled = GetBool(values, "enableSinging", ref mapped);

            // --- Geomancer (legacy *_Spell ints are indices into the fixed GeomancerInfo pair table) ---
            GeoSettings geo = settings.Geo;
            bool indiEnabled = GetBool(values, "EnableGeoSpells", ref mapped);
            if (values.ContainsKey("IndiWhenEngaged"))
            {
                geo.IndiOnlyWhenEngaged = GetBool(values, "IndiWhenEngaged", ref mapped);
            }

            geo.LuopanEnabled = GetBool(values, "EnableLuopanSpells", ref mapped);
            geo.LuopanOnlyWhenEngaged = GetBool(values, "GeoWhenEngaged", ref mapped);

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

            // --- Enmity assist (legacy "Hate Establisher": autoTarget*) ---
            geo.EnmityAssist = GetBool(values, "autoTarget", ref mapped);
            geo.EnmityPlayerName = GetString(values, "autoTarget_Target", ref mapped);
            string hateSpell = GetString(values, "autoTargetSpell", ref mapped);
            if (!string.IsNullOrWhiteSpace(hateSpell))
            {
                geo.EnmitySpell = hateSpell;
            }

            // Hate_SpellType: 0 = cast on the enemy, 1 = cast on the party member.
            geo.EnmityCastOnEnemy = GetInt(values, "Hate_SpellType", ref mapped) == 0 && geo.EnmityAssist;
            if (geo.EnmityAssist && !geo.EnmityCastOnEnemy && string.IsNullOrWhiteSpace(geo.EnmityPlayerName))
            {
                result.Warnings.Add("autoTarget: party-based hate needs autoTarget_Target (the tank's name) — enmity assist imported disabled.");
                geo.EnmityAssist = false;
            }

            // --- Scholar (legacy Form2 SCHOLAR STRATAGEMS group) ---
            SchSettings sch = settings.Sch;
            sch.LightArts = GetBool(values, "LightArts", ref mapped);
            sch.AddendumWhite = GetBool(values, "AddendumWhite", ref mapped);
            sch.DarkArts = GetBool(values, "DarkArts", ref mapped);
            sch.AddendumBlack = GetBool(values, "AddendumBlack", ref mapped);
            sch.Sublimation = GetBool(values, "Sublimation", ref mapped);
            uint sublimationMp = GetUInt(values, "sublimationMP", ref mapped);
            if (sublimationMp > 0)
            {
                sch.SublimationMpDeficit = sublimationMp;
            }

            sch.UseRapture = GetBool(values, "rapture", ref mapped);

            sch.Enabled = sch.LightArts || sch.AddendumWhite || sch.DarkArts || sch.AddendumBlack || sch.Sublimation
                || sch.UseRapture;

            // --- White mage / Red mage JAs (legacy Form2 enhancing group) ---
            WhmSettings whm = settings.Whm;
            whm.UseAfflatusSolace = GetBool(values, "AfflatusSolace", ref mapped);
            whm.UseAfflatusMisery = GetBool(values, "AfflatusMisery", ref mapped);
            whm.UseDevotion = GetBool(values, "Devotion", ref mapped);
            whm.DevotionOnlyWhenEngaged = GetBool(values, "DevotionWhenEngaged", ref mapped);
            whm.DevotionTarget = GetString(values, "DevotionTargetName", ref mapped);
            uint devotionMp = GetUInt(values, "DevotionMP", ref mapped);
            if (devotionMp > 0)
            {
                whm.DevotionMpBelow = devotionMp;
            }

            whm.Enabled = whm.UseAfflatusSolace || whm.UseAfflatusMisery || whm.UseDevotion;

            // Divine Caress modifies removals, so it lives with the debuff settings.
            settings.Debuffs.UseDivineCaress = GetBool(values, "DivineCaress", ref mapped);

            settings.Rdm.UseComposure = GetBool(values, "Composure", ref mapped);
            settings.Rdm.UseConvert = GetBool(values, "Convert", ref mapped);
            if (GetUInt(values, "convertMP", ref mapped) > 0)
            {
                result.Warnings.Add("convertMP: legacy used an absolute MP threshold — configure Rdm.ConvertMpBelowPercent (%) instead.");
            }

            settings.Rdm.Enabled = settings.Rdm.UseComposure || settings.Rdm.UseConvert;

            // Accession: legacy split it per feature. accessionProShell maps to
            // UseAccession on the chosen Protect/Shell upkeep entries (engine preps
            // Accession, self-cast then also hits party in range); accessionCure maps
            // to the Curaga module's Accession + Cure I–IV party heal.
            if (GetBool(values, "accessionProShell", ref mapped))
            {
                SetAccession(settings, protectSpell);
                SetAccession(settings, shellSpell);
            }

            settings.Curaga.UseAccession = GetBool(values, "accessionCure", ref mapped);

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

            if (values.ContainsKey("EnableAddOn"))
            {
                settings.Addon.Enabled = GetBool(values, "EnableAddOn", ref mapped);
                if (settings.Addon.Enabled)
                {
                    result.Warnings.Add("EnableAddOn: party debuff tracking uses the CurePlease3 addon and covers the current party, not alliance parties.");
                }
            }

            result.Warnings.Add("Buff entries were mapped with their tiers/intervals but left DISABLED — legacy per-slot buff checkboxes were not persisted, so enable the rows you want on the Buff Upkeep page. Per-member buff selections, JA toggles, and item settings are not mapped yet.");

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
        /// GeomancerInfo table in legacy Form1.cs (geo_position order), which
        /// GeoModule.KnownColureSuffixes now mirrors.
        /// </summary>
        private static string LegacyGeoSpell(int index, string prefix)
        {
            return index >= 0 && index < Core.Modules.GeoModule.KnownColureSuffixes.Count
                ? prefix + Core.Modules.GeoModule.KnownColureSuffixes[index]
                : null;
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

        /// <summary>Safe tier-list index; null when out of range.</summary>
        private static string Tier(string[] tiers, int index)
        {
            return index >= 0 && index < tiers.Length ? tiers[index] : null;
        }

        /// <summary>Sets the fallback interval on the named catalog entry (legacy minutes win when positive).</summary>
        private static void MapBuffEntry(CurePleaseSettings settings, string spell, double legacyMinutes)
        {
            if (spell == null)
            {
                return;
            }

            BuffEntry entry = settings.Buffs.Entries.Find(
                e => e.Spell.Equals(spell, StringComparison.OrdinalIgnoreCase));
            if (entry != null && legacyMinutes > 0)
            {
                entry.DurationMinutes = legacyMinutes;
            }
        }

        /// <summary>Flags the named catalog entry for Accession prep (legacy accessionProShell).</summary>
        private static void SetAccession(CurePleaseSettings settings, string spell)
        {
            if (spell == null)
            {
                return;
            }

            BuffEntry entry = settings.Buffs.Entries.Find(
                e => e.Spell.Equals(spell, StringComparison.OrdinalIgnoreCase));
            BuffCatalogEntry catalog = BuffCatalog.Find(spell);
            if (entry != null && catalog != null && catalog.AccessionCompatible)
            {
                entry.UseAccession = true;
            }
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
