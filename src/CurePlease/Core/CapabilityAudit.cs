using System;
using System.Collections.Generic;
using System.Linq;
using CurePlease.Core.Modules;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core
{
    /// <summary>
    /// Reports enabled actions that the current character/job cannot use. This is
    /// deliberately separate from recast, MP, and range checks: those are temporary
    /// conditions, while this audit explains configuration/capability mismatches.
    /// </summary>
    public static class CapabilityAudit
    {
        private const byte JobWhiteMage = 3;
        private const byte JobRedMage = 5;
        private const byte JobPaladin = 7;
        private const byte JobBard = 10;
        private const byte JobScholar = 20;
        private const byte JobGeomancer = 21;

        private sealed class RequestedAction
        {
            public string Source;
            public string Name;
            public bool IsAbility;
            public bool InstrumentGranted;
        }

        public static IReadOnlyList<string> FindIssues(
            GameStateService state, CurePleaseSettings settings, PlayerState player)
        {
            var requested = new List<RequestedAction>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasScholar = HasJob(player, JobScholar);

            for (int i = 0; i < SpellBook.CureTierNames.Length && i < settings.Cures.Tiers.Count; i++)
            {
                if (settings.Cures.Tiers[i].Enabled)
                {
                    AddSpell(requested, seen, "Cures", SpellBook.CureTierNames[i]);
                }
            }

            if (settings.Curaga.Enabled)
            {
                for (int i = 0; i < SpellBook.CuragaTierNames.Length && i < settings.Curaga.Tiers.Count; i++)
                {
                    if (settings.Curaga.Tiers[i].Enabled)
                    {
                        AddSpell(requested, seen, "Curaga", SpellBook.CuragaTierNames[i]);
                    }
                }

                if (settings.Curaga.UseAccession && hasScholar)
                {
                    AddAbility(requested, seen, "Curaga", "Accession");
                }
            }

            if (settings.Cures.UseMajesty && HasJob(player, JobPaladin))
            {
                AddAbility(requested, seen, "Cures", "Majesty");
            }

            if ((settings.Cures.UseDivineSeal
                    || (settings.Debuffs.Enabled && settings.Debuffs.GroupRemovalUseDivineSeal))
                && HasJob(player, JobWhiteMage))
            {
                AddAbility(requested, seen, "Cures", "Divine Seal");
            }

            if (settings.Debuffs.Enabled)
            {
                foreach ((string Name, string Spell) removal in DebuffModule.KnownRemovals)
                {
                    if (settings.Debuffs.EnabledRemovals.Contains(removal.Name)
                        && removal.Spell.IndexOf('/', StringComparison.Ordinal) < 0)
                    {
                        AddSpell(requested, seen, "Debuff Removal", removal.Spell);
                    }
                }

                if (settings.Debuffs.WakeSleepEnabled && settings.Debuffs.EnabledRemovals.Contains("Sleep"))
                {
                    AddSpell(requested, seen, "Debuff Removal", settings.Debuffs.WakeSleepSpell);
                }

                if (settings.Debuffs.UseDivineCaress)
                {
                    AddAbility(requested, seen, "Debuff Removal", "Divine Caress");
                }
            }

            foreach (BuffEntry entry in settings.Buffs.Entries)
            {
                if (!entry.Enabled)
                {
                    continue;
                }

                AddSpell(requested, seen, "Buff Upkeep", entry.Spell);
                BuffCatalogEntry catalog = BuffCatalog.Find(entry.Spell);
                if (catalog != null && hasScholar && entry.UseAccession && catalog.AccessionCompatible)
                {
                    AddAbility(requested, seen, "Buff Upkeep", "Accession");
                }

                if (catalog != null && player.MainJob == JobScholar
                    && entry.UsePerpetuance && catalog.PerpetuanceCompatible)
                {
                    AddAbility(requested, seen, "Buff Upkeep", "Perpetuance");
                }
            }

            if (settings.Bard.Enabled && HasJob(player, JobBard))
            {
                AddBard(requested, seen, settings.Bard);
            }

            if (settings.Geo.Enabled && HasJob(player, JobGeomancer))
            {
                AddGeo(requested, seen, settings.Geo);
            }

            if (settings.Sch.Enabled && hasScholar)
            {
                AddAbility(requested, seen, "Scholar", settings.Sch.LightArts ? "Light Arts" : null);
                AddAbility(requested, seen, "Scholar", settings.Sch.AddendumWhite ? "Addendum: White" : null);
                AddAbility(requested, seen, "Scholar", settings.Sch.DarkArts ? "Dark Arts" : null);
                AddAbility(requested, seen, "Scholar", settings.Sch.AddendumBlack ? "Addendum: Black" : null);
                AddAbility(requested, seen, "Scholar", settings.Sch.Sublimation ? "Sublimation" : null);
                AddAbility(requested, seen, "Scholar", settings.Sch.UseRapture ? "Rapture" : null);
            }

            if (settings.Whm.Enabled && player.MainJob == JobWhiteMage)
            {
                AddAbility(requested, seen, "White Mage", settings.Whm.UseAfflatusSolace ? "Afflatus Solace" : null);
                AddAbility(requested, seen, "White Mage", settings.Whm.UseAfflatusMisery ? "Afflatus Misery" : null);
                AddAbility(requested, seen, "White Mage", settings.Whm.UseDevotion ? "Devotion" : null);
            }

            if (settings.Rdm.Enabled && HasJob(player, JobRedMage))
            {
                AddAbility(requested, seen, "Red Mage", settings.Rdm.UseConvert ? "Convert" : null);
                if (player.MainJob == JobRedMage)
                {
                    AddAbility(requested, seen, "Red Mage", settings.Rdm.UseComposure ? "Composure" : null);
                }
            }

            var issues = new List<string>();
            foreach (RequestedAction action in requested)
            {
                string reason = action.IsAbility
                    ? state.HasAbility(action.Name) ? null : "not available to the current job"
                    : SpellProblem(state, action.Name, action.InstrumentGranted);
                if (reason != null)
                {
                    issues.Add(action.Source + ": " + action.Name + " — " + reason + "; it will not be attempted");
                }
            }

            // Config warning (not an availability check): on SCH main every -na/Erase
            // spell is Addendum: White-gated — with the addendum upkeep off, removals
            // would fail in-game. (WHM subs provide them natively; this is the
            // no-native-source case the module cannot self-heal without the JA.)
            if (player.MainJob == JobScholar && settings.Debuffs.Enabled && !settings.Sch.AddendumWhite)
            {
                bool anyGatedRemoval = false;
                foreach ((string Name, string Spell) removal in DebuffModule.KnownRemovals)
                {
                    if (settings.Debuffs.EnabledRemovals.Contains(removal.Name)
                        && DebuffModule.AddendumWhiteSpells.Contains(removal.Spell))
                    {
                        anyGatedRemoval = true;
                        break;
                    }
                }

                if (anyGatedRemoval)
                {
                    issues.Add("Debuff Removal: -na/Erase spells require Addendum: White on SCH main — "
                        + "enable it on the Scholar page or removals will fail in-game");
                }
            }

            return issues;
        }

        /// <summary>Final engine-side guard against sending an unusable spell or JA.</summary>
        public static bool IsAvailable(GameStateService state, ActionCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.ActionName))
            {
                return false;
            }

            if (candidate.Category == ActionCategory.Item)
            {
                return true;
            }

            if (candidate.IsJobAbility)
            {
                return state.HasAbility(candidate.ActionName);
            }

            bool instrumentGranted = candidate.Category == ActionCategory.Song
                && (candidate.ActionName.Equals("Honor March", StringComparison.OrdinalIgnoreCase)
                    || candidate.ActionName.Equals("Aria of Passion", StringComparison.OrdinalIgnoreCase));
            return SpellProblem(state, candidate.ActionName, instrumentGranted) == null;
        }

        private static void AddBard(
            List<RequestedAction> requested, HashSet<string> seen, BardSettings bard)
        {
            AddSpells(requested, seen, "Bard", bard.PartySongs);
            AddSpells(requested, seen, "Bard", bard.DummySongs);
            AddSpells(requested, seen, "Bard", bard.EnemyDebuffSongs);
            foreach (PianissimoTarget pair in bard.PianissimoTargets)
            {
                if (!string.IsNullOrWhiteSpace(pair.Target))
                {
                    AddSpell(requested, seen, "Bard", pair.Song);
                }
            }

            AddAbility(requested, seen, "Bard", bard.PianissimoTargets.Count > 0 ? "Pianissimo" : null);
            AddAbility(requested, seen, "Bard", bard.UseSoulVoice ? "Soul Voice" : null);
            AddAbility(requested, seen, "Bard", bard.UseMarcato ? "Marcato" : null);
            AddAbility(requested, seen, "Bard", bard.UseClarionCall ? "Clarion Call" : null);
            if (bard.UseNitro)
            {
                AddAbility(requested, seen, "Bard", "Nightingale");
                AddAbility(requested, seen, "Bard", "Troubadour");
            }

            if (bard.UseMarcato)
            {
                AddSpell(requested, seen, "Bard", bard.MarcatoSong, instrumentGranted: true);
            }

            if (bard.UseClarionCall)
            {
                AddSpell(requested, seen, "Bard", bard.ClarionSong, instrumentGranted: true);
            }

            MarkInstrumentSongs(requested, bard);
        }

        private static void MarkInstrumentSongs(List<RequestedAction> requested, BardSettings bard)
        {
            foreach (RequestedAction action in requested)
            {
                if (action.Source == "Bard"
                    && (action.Name.Equals("Honor March", StringComparison.OrdinalIgnoreCase)
                        || action.Name.Equals("Aria of Passion", StringComparison.OrdinalIgnoreCase)))
                {
                    action.InstrumentGranted = true;
                }
            }
        }

        private static void AddGeo(
            List<RequestedAction> requested, HashSet<string> seen, GeoSettings geo)
        {
            AddSpell(requested, seen, "Geomancy", geo.IndiSpell);
            if (geo.LuopanEnabled)
            {
                AddSpell(requested, seen, "Geomancy", geo.GeoSpell);
            }

            AddAbility(requested, seen, "Geomancy", geo.UseBlazeOfGlory ? "Blaze of Glory" : null);
            AddAbility(requested, seen, "Geomancy", geo.UseEclipticAttrition ? "Ecliptic Attrition" : null);
            AddAbility(requested, seen, "Geomancy", geo.UseDematerialize ? "Dematerialize" : null);
            AddAbility(requested, seen, "Geomancy", geo.UseLifeCycle ? "Life Cycle" : null);
            AddAbility(requested, seen, "Geomancy", geo.UseFullCircle ? "Full Circle" : null);
            AddAbility(requested, seen, "Geomancy", geo.UseRadialArcana ? "Radial Arcana" : null);
            if (geo.UseEntrust)
            {
                AddAbility(requested, seen, "Geomancy", "Entrust");
                AddSpell(requested, seen, "Geomancy", geo.EntrustSpell);
            }

            if (geo.EnmityAssist)
            {
                AddSpell(requested, seen, "Geomancy enmity", geo.EnmitySpell);
            }
        }

        private static bool HasJob(PlayerState player, byte job)
        {
            return player.MainJob == job || player.SubJob == job;
        }

        private static void AddSpells(
            List<RequestedAction> requested, HashSet<string> seen, string source, IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                AddSpell(requested, seen, source, name);
            }
        }

        private static void AddSpell(
            List<RequestedAction> requested, HashSet<string> seen, string source, string name,
            bool instrumentGranted = false)
        {
            Add(requested, seen, source, name, false, instrumentGranted);
        }

        private static void AddAbility(
            List<RequestedAction> requested, HashSet<string> seen, string source, string name)
        {
            Add(requested, seen, source, name, true, false);
        }

        private static void Add(
            List<RequestedAction> requested, HashSet<string> seen, string source, string name,
            bool isAbility, bool instrumentGranted)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string trimmed = name.Trim();
            string key = (isAbility ? "JA:" : "MA:") + trimmed;
            if (seen.Add(key))
            {
                requested.Add(new RequestedAction
                {
                    Source = source,
                    Name = trimmed,
                    IsAbility = isAbility,
                    InstrumentGranted = instrumentGranted,
                });
            }
        }

        private static string SpellProblem(GameStateService state, string name, bool instrumentGranted)
        {
            if (instrumentGranted)
            {
                return null;
            }

            EliteAPI.ISpell spell = state.FindSpell(name);
            if (spell == null)
            {
                return "not found in spell resources";
            }

            if (!state.KnowsSpell(spell.Index))
            {
                return "not learned";
            }

            if (!state.MeetsSpellLevelRequirement(spell))
            {
                return "not available at the current main/sub job levels";
            }

            return null;
        }
    }
}
