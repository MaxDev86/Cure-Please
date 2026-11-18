using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Buff upkeep, v2 — PL-ONLY (docs/buff-upkeep-design.md): keeps the enabled
    /// <see cref="BuffSettings.Entries"/> on the character running Cure Please.
    /// Per-member party buffs (Haste on the tank, Refresh on the mage, Accession
    /// party Protect/Shell) belong to the separate Player Buffs feature.
    ///
    /// Behavior:
    ///  - the player's own buffs are always readable, so a missing buff is recast
    ///    immediately — dispel detection for free; the entry's DurationMinutes is
    ///    only a fallback for interrupted tracking;
    ///  - timestamps record on CONFIRMED cast success (OnActionExecuted), never at
    ///    cast initiation — failed casts retry after a short backoff;
    ///  - mutually exclusive families (bar-element, bar-status, enspell, storm):
    ///    a spell is not offered while a DIFFERENT buff of the same family is up —
    ///    recasting would silently overwrite it;
    ///  - Accession (SCH main or sub, catalog-gated to the BG-wiki Accession list)
    ///    preps before the cast — the self-cast then also lands on party members in
    ///    range;
    ///  - coverage repair (needs the addon's per-member buff feed): when the PL has
    ///    a party-wide buff (-ra spell or accessioned single) but an own-party member
    ///    lacks it — out of the 10' AoE at cast time or dispelled mid-cycle — the
    ///    module patches Protect/Shell with the single-target line on that member,
    ///    or recasts the AoE for bar- lines (a fresh Accession charge when the bar
    ///    is an accessioned single);
    ///  - Perpetuance (SCH main 87+, catalog-gated to spells an SCH main can cast)
    ///    preps before the cast and the landing records the extended duration
    ///    (entry minutes x the Scholar-page multiplier).
    /// </summary>
    public sealed class BuffUpkeepModule : IDecisionModule
    {
        private const byte JobScholar = 20;

        /// <summary>After a cast attempt, wait this long before offering it again.</summary>
        private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(15);

        /// <summary>-ra / Accession AoE radius (yalms): a missed member inside it can be re-hit by recasting the AoE.</summary>
        private const float AoERadiusYalms = 10f;

        /// <summary>Party-targeted single-cast range (yalms) for Protect/Shell coverage patches.</summary>
        private const float SinglePatchRangeYalms = 21f;

        /// <summary>Buff packets lag a fresh AoE landing — wait before judging members as missed.</summary>
        private static readonly TimeSpan CoverageSettleWindow = TimeSpan.FromSeconds(6);

        /// <summary>Single-target patch lines for the -ra staples, highest tier first.</summary>
        private static readonly string[] ProtectSingles = { "Protect V", "Protect IV", "Protect III", "Protect II", "Protect" };
        private static readonly string[] ShellSingles = { "Shell V", "Shell IV", "Shell III", "Shell II", "Shell" };

        /// <summary>How long after execution before a stratagem JA may be offered again.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        private sealed class Landing
        {
            public DateTime When;
            public double Minutes;
        }

        private readonly GameStateService _state;

        /// <summary>spell -> last confirmed landing (UTC) + the duration it was cast under.</summary>
        private readonly Dictionary<string, Landing> _lastLanded = new Dictionary<string, Landing>(StringComparer.OrdinalIgnoreCase);

        /// <summary>spell -> last attempt (UTC), any outcome.</summary>
        private readonly Dictionary<string, DateTime> _lastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>spell -> duration planned at offer time (captures Perpetuance before it is consumed).</summary>
        private readonly Dictionary<string, double> _plannedDuration = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>JA name -> last execution time (Perpetuance verify window).</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public BuffUpkeepModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Buffs";

        /// <summary>Zoning strips every buff — drop all landing/attempt tracking.</summary>
        public void OnZoneChanged()
        {
            _lastLanded.Clear();
            _lastAttempt.Clear();
            _plannedDuration.Clear();
            _jaAttempted.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            PlayerState pl = snapshot.Player;
            if (pl.HP == 0)
            {
                return;
            }

            foreach (BuffEntry entry in settings.Buffs.Entries)
            {
                if (!entry.Enabled)
                {
                    continue;
                }

                BuffCatalogEntry catalog = BuffCatalog.Find(entry.Spell);
                if (catalog == null)
                {
                    continue;
                }

                EliteAPI.ISpell spell = SpellBook.IsCastable(_state, entry.Spell, pl.MP);
                if (spell == null)
                {
                    continue;
                }

                // Family exclusivity: another bar-element / bar-status / enspell /
                // storm is already up — casting would silently overwrite it.
                if (catalog.Family != BuffFamily.None && HasFamilyConflict(pl.Buffs, catalog))
                {
                    continue;
                }

                // Stratagem preps: offer Accession (main or sub SCH) and/or
                // Perpetuance (SCH main 87+) first, hold the cast until the needed
                // buffs land. Accession turns the self-cast into a party-wide one
                // (10' radius, whoever is in range). Falls through to a plain cast
                // when a stratagem is unavailable.
                bool useAccession = entry.UseAccession && catalog.AccessionCompatible
                    && (pl.MainJob == JobScholar || pl.SubJob == JobScholar);
                bool usePerpetuance = entry.UsePerpetuance && catalog.PerpetuanceCompatible
                    && pl.MainJob == JobScholar && pl.MainJobLevel >= 87;

                // The player's own buffs are always known: recast only when missing.
                bool present = HasBuff(pl.Buffs, catalog.BuffId);
                if (present)
                {
                    // PL is covered — but a party-wide cast may have missed someone
                    // (out of range) or a member may have been dispelled mid-cycle.
                    TryOfferCoverageRepair(snapshot, settings, catalog, useAccession, candidates);
                    continue;
                }

                if (_lastAttempt.TryGetValue(catalog.Spell, out DateTime attempted)
                    && DateTime.UtcNow - attempted < RetryBackoff)
                {
                    continue;
                }

                bool accessionUp = !useAccession || pl.HasBuff(StatusEffect.Accession);
                bool perpetuanceUp = !usePerpetuance || pl.HasBuff(StatusEffect.Perpetuance);

                if (!accessionUp || !perpetuanceUp)
                {
                    if ((useAccession && !accessionUp && RecentlyAttempted("Accession"))
                        || (usePerpetuance && !perpetuanceUp && RecentlyAttempted("Perpetuance")))
                    {
                        continue;
                    }

                    if (!accessionUp && StratagemReady(pl, "Accession"))
                    {
                        candidates.Add(StratagemJa(settings, "Accession", catalog.Spell));
                        continue;
                    }

                    if (accessionUp && !perpetuanceUp && StratagemReady(pl, "Perpetuance"))
                    {
                        candidates.Add(StratagemJa(settings, "Perpetuance", catalog.Spell));
                        continue;
                    }
                }

                double minutes = pl.HasBuff(StatusEffect.Perpetuance)
                    ? entry.DurationMinutes * settings.Sch.PerpetuanceMultiplier
                    : entry.DurationMinutes;
                _plannedDuration[catalog.Spell] = minutes;

                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.BuffUpkeep,
                    Score = settings.Priorities.BuffWeight,
                    ActionName = spell.Name[0],
                    TargetName = pl.Name,
                    Description = catalog.Spell
                        + (accessionUp && useAccession ? " (Accession)" : "")
                        + (minutes > entry.DurationMinutes ? " (Perpetuance)" : ""),
                });
            }
        }

        /// <summary>
        /// Stratagem availability: Light Arts up (White Grimoire), ability known, and
        /// a charge free. Charges come from the shared stratagem timer (id 231) —
        /// the name-based recast lookup resolves to that same timer and would read
        /// "on recast" whenever charges are merely not full. Charge count uses the
        /// main OR sub SCH level; the 550 JP recharge bonus is main-job only.
        /// </summary>
        private bool StratagemReady(PlayerState pl, string stratagemName)
        {
            if (!pl.HasBuff(StatusEffect.Light_Arts) || !_state.HasAbilityForUse(stratagemName))
            {
                return false;
            }

            int schLevel = pl.MainJob == JobScholar ? pl.MainJobLevel : pl.SubJobLevel;
            int maxCharges = Stratagems.MaxChargesForLevel(schLevel);
            bool reduced = pl.MainJob == JobScholar
                && _state.GetSpentJobPoints(JobScholar) >= Stratagems.ReducedRechargeJobPoints;
            int charges = Stratagems.AvailableCharges(
                _state.GetAbilityRecastByTimerId(Stratagems.TimerId), maxCharges, reduced);

            return charges > 0;
        }

        private static ActionCandidate StratagemJa(CurePleaseSettings settings, string name, string spell)
        {
            return new ActionCandidate
            {
                Category = ActionCategory.JobAbility,
                Score = settings.Priorities.BuffWeight + 5,
                ActionName = name,
                TargetName = "<me>",
                IsJobAbility = true,
                PrepBuff = name == "Accession" ? StatusEffect.Accession : StatusEffect.Perpetuance,
                PrepForSpell = spell,
                Description = name + " (before " + spell + ")",
            };
        }

        /// <summary>
        /// Party-wide entries (-ra spells, or singles running under Accession) can
        /// miss a member — they were out of the 10' AoE at cast time, or were
        /// dispelled mid-cycle. Detects the gap from the addon's per-member buff
        /// feed and repairs it:
        ///  - Protect/Shell lines: surgical single-target patch on the missed member
        ///    (those singles are party-targetable; no stratagem, no refresh of the
        ///    members who still have it);
        ///  - bar- lines: the singles are self-only, so the only repair is re-running
        ///    the AoE — a plain -ra recast, or another Accession charge when the bar
        ///    is an accessioned single (offered only with a stratagem free);
        ///  - requires at least one missed member inside the AoE radius (10'), or
        ///    within single-cast range (21') for a patch.
        /// Alliance members are never covered: -ra/Accession only hit the own party.
        /// Without the addon there is no per-member visibility and no repair.
        /// </summary>
        private void TryOfferCoverageRepair(GameSnapshot snapshot, CurePleaseSettings settings,
            BuffCatalogEntry catalog, bool useAccession, List<ActionCandidate> candidates)
        {
            bool isRa = catalog.Group == "Protectra" || catalog.Group == "Shellra"
                || catalog.Group == "Bar-ra (element)" || catalog.Group == "Bar-ra (status)";
            if (!isRa && !useAccession)
            {
                return; // self-only buff — no party coverage to repair
            }

            if (snapshot.PartyBuffs.Count == 0)
            {
                return; // no addon feed: members' buffs are invisible
            }

            // Settle window after a confirmed landing, and the shared per-spell
            // attempt backoff (which also covers the seconds right after the cast).
            if (_lastLanded.TryGetValue(catalog.Spell, out Landing landing)
                && DateTime.UtcNow - landing.When < CoverageSettleWindow)
            {
                return;
            }

            if (_lastAttempt.TryGetValue(catalog.Spell, out DateTime attempted)
                && DateTime.UtcNow - attempted < RetryBackoff)
            {
                return;
            }

            PlayerState pl = snapshot.Player;
            var missing = new List<KeyValuePair<PartyMemberState, float>>();
            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (!member.Active || member.PartyIndex != 0 || member.CurrentHP == 0
                    || member.Zone != pl.ZoneId
                    || member.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!snapshot.PartyBuffs.TryGetValue(member.Name, out short[] buffs)
                    || HasBuff(buffs, catalog.BuffId))
                {
                    continue;
                }

                float distance = member.TargetIndex != 0
                    ? _state.GetEntityDistance(member.TargetIndex)
                    : float.MaxValue;
                missing.Add(new KeyValuePair<PartyMemberState, float>(member, distance));
            }

            if (missing.Count == 0)
            {
                return;
            }

            missing.Sort((a, b) => a.Value.CompareTo(b.Value));

            // Protect/Shell: patch the nearest missed member with the highest
            // castable single (never spends a stratagem, never touches the members
            // who still have the buff).
            string[] patchLine = catalog.Group == "Protectra" || catalog.Group == "Protect" ? ProtectSingles
                : catalog.Group == "Shellra" || catalog.Group == "Shell" ? ShellSingles
                : null;
            if (patchLine != null)
            {
                EliteAPI.ISpell patch = FirstCastable(pl, patchLine);
                if (patch != null)
                {
                    KeyValuePair<PartyMemberState, float> nearest = missing[0];
                    if (nearest.Value <= SinglePatchRangeYalms
                        && !RecentlyAttemptedSpell(patch.Name[0]))
                    {
                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.BuffUpkeep,
                            Score = settings.Priorities.BuffWeight - 1,
                            ActionName = patch.Name[0],
                            TargetName = nearest.Key.Name,
                            Description = patch.Name[0] + " → " + nearest.Key.Name + " (coverage repair)",
                        });
                    }

                    return; // a patch line exists: never recast the AoE for coverage
                }
            }

            // AoE recast. Needs at least one missed member inside the radius.
            if (missing[0].Value > AoERadiusYalms)
            {
                return;
            }

            if (isRa)
            {
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.BuffUpkeep,
                    Score = settings.Priorities.BuffWeight - 1,
                    ActionName = catalog.Spell,
                    TargetName = pl.Name,
                    Description = catalog.Spell + " (coverage repair: " + missing[0].Key.Name
                        + (missing.Count > 1 ? " +" + (missing.Count - 1) : "") + ")",
                });
                return;
            }

            // Accessioned single (e.g. a bar- spell): the repair costs another
            // stratagem charge — the single is self-only, so the AoE extension is
            // the only way to reach the missed member.
            if (!pl.HasBuff(StatusEffect.Accession))
            {
                if (!RecentlyAttempted("Accession") && StratagemReady(pl, "Accession"))
                {
                    ActionCandidate ja = StratagemJa(settings, "Accession", catalog.Spell);
                    ja.Score = settings.Priorities.BuffWeight + 4;
                    ja.Description = "Accession (coverage repair for " + catalog.Spell + ")";
                    candidates.Add(ja);
                }

                return;
            }

            EliteAPI.ISpell selfCast = SpellBook.IsCastable(_state, catalog.Spell, pl.MP);
            if (selfCast != null)
            {
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.BuffUpkeep,
                    Score = settings.Priorities.BuffWeight - 1,
                    ActionName = selfCast.Name[0],
                    TargetName = pl.Name,
                    Description = catalog.Spell + " (Accession coverage repair: " + missing[0].Key.Name + ")",
                });
            }
        }

        private EliteAPI.ISpell FirstCastable(PlayerState pl, string[] names)
        {
            foreach (string name in names)
            {
                EliteAPI.ISpell spell = SpellBook.IsCastable(_state, name, pl.MP);
                if (spell != null)
                {
                    return spell;
                }
            }

            return null;
        }

        private bool RecentlyAttemptedSpell(string spellName)
        {
            return _lastAttempt.TryGetValue(spellName, out DateTime attempted)
                && DateTime.UtcNow - attempted < RetryBackoff;
        }

        private bool RecentlyAttempted(string abilityName)
        {
            return _jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow;
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility)
            {
                _jaAttempted[candidate.ActionName] = DateTime.UtcNow;
                return;
            }

            if (candidate.Category != ActionCategory.BuffUpkeep)
            {
                return;
            }

            BuffCatalogEntry catalog = BuffCatalog.Find(candidate.ActionName);
            if (catalog == null)
            {
                return;
            }

            _lastAttempt[catalog.Spell] = DateTime.UtcNow;

            if (result.Succeeded)
            {
                _plannedDuration.TryGetValue(catalog.Spell, out double minutes);
                _lastLanded[catalog.Spell] = new Landing
                {
                    When = DateTime.UtcNow,
                    Minutes = minutes > 0 ? minutes : catalog.DefaultMinutes,
                };
            }
        }

        /// <summary>True when a DIFFERENT buff of the same mutually exclusive family is present.</summary>
        private static bool HasFamilyConflict(short[] buffs, BuffCatalogEntry catalog)
        {
            HashSet<short> familyIds = BuffCatalog.BuffIdsOfFamily(catalog.Family);
            foreach (short id in buffs)
            {
                if (id != catalog.BuffId && familyIds.Contains(id))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasBuff(short[] buffs, short effect)
        {
            foreach (short buff in buffs)
            {
                if (buff == effect)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
