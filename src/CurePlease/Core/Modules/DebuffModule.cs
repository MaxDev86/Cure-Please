using System;
using System.Collections.Generic;
using System.Linq;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Debuff removal for the local and monitored characters from memory, plus
    /// own-party members from the addon's 0x076 buff reports. Alliance parties
    /// receive no buff reports from the server; see docs/legacy-assessment.md
    /// section 3.
    ///
    /// Legacy parity, three removal classes:
    ///  - ailments removable with -na spells (Poisona, Silena, Paralyna, Blindna,
    ///    Stona, Viruna, Cursna);
    ///  - status effects removable only with Erase (stat-downs, elemental DoTs,
    ///    Dia/Bio, Helix, Elegy/Threnody/Requiem, ...);
    ///  - Sleep/Sleep2, woken with a plain cure spell (Cure/Cura/Curaga — user
    ///    setting). Sleep is never woken on the PL: a sleeping PL cannot cast.
    ///
    /// PL self-removal while silenced falls to ITEMS (Echo Drops / Catholicon /
    /// Remedy / Vicar's Drink) since the PL cannot cast Silena on themselves;
    /// doomed PL can use Holy Water / Hallowed Water when Cursna is not trusted.
    /// Mute and Amnesia are intentionally not handled — neither can be removed
    /// by any means (Amnesia was Esuna-removable once; SE removed that).
    ///
    /// Group removals (N+ members sharing an ailment) are handled by one AoE cast,
    /// mechanism picked automatically: Yagrush's always-on Divine Veil (free) when
    /// equipped, then Accession (SCH, 1 stratagem charge), then Divine Seal (WHM
    /// main 50+ Divine Veil trait, 10-minute recast shared with the critical-cure
    /// use in CureModule).
    ///
    /// SCH main note: every -na/Erase spell is Addendum: White-gated (BG-wiki) —
    /// the resource DB lists them with SCH levels, so the level gate alone would
    /// attempt them and fail in-game. The module preps Addendum: White first and
    /// holds the removals (unless the sub job provides the spell natively).
    ///
    /// Unlike legacy, severity is scored and competes with cures directly, so a
    /// Doom outranks a medium cure instead of only being checked before small ones.
    /// Also unlike legacy, each Erase debuff is gated by its OWN checkbox (legacy
    /// copy-pasted `&amp;&amp; plAttackDown` onto nearly every Erase branch).
    /// </summary>
    public sealed class DebuffModule : IDecisionModule
    {
        private const byte JobWhiteMage = 3;
        private const byte JobScholar = 20;

        private sealed class Removal
        {
            public StatusEffect Debuff;
            public string Spell;
            public double Severity;
        }

        private static readonly Removal[] Removals =
        {
            // --- Ailments (-na spells, Cursna) ---
            new Removal { Debuff = StatusEffect.Doom,           Spell = "Cursna",   Severity = 1.00 },
            new Removal { Debuff = StatusEffect.Petrification,  Spell = "Stona",    Severity = 0.95 },
            new Removal { Debuff = StatusEffect.Paralysis,      Spell = "Paralyna", Severity = 0.90 },
            new Removal { Debuff = StatusEffect.Silence,        Spell = "Silena",   Severity = 0.80 },
            new Removal { Debuff = StatusEffect.Curse,          Spell = "Cursna",   Severity = 0.75 },
            new Removal { Debuff = StatusEffect.Curse2,         Spell = "Cursna",   Severity = 0.75 },
            new Removal { Debuff = StatusEffect.Bane,           Spell = "Cursna",   Severity = 0.75 },
            new Removal { Debuff = StatusEffect.Disease,        Spell = "Viruna",   Severity = 0.70 },
            new Removal { Debuff = StatusEffect.Plague,         Spell = "Viruna",   Severity = 0.70 },
            new Removal { Debuff = StatusEffect.Blindness,      Spell = "Blindna",  Severity = 0.60 },
            new Removal { Debuff = StatusEffect.Poison,         Spell = "Poisona",  Severity = 0.50 },

            // --- Status effects (Erase only) ---
            new Removal { Debuff = StatusEffect.Slow,           Spell = "Erase",    Severity = 0.40 },
            new Removal { Debuff = StatusEffect.Addle,          Spell = "Erase",    Severity = 0.38 },
            new Removal { Debuff = StatusEffect.Bind,           Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Weight,         Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Bio,            Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Dia,            Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Attack_Down,    Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Accuracy_Down,  Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Evasion_Down,   Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Defense_Down,   Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Magic_Acc_Down, Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Magic_Atk_Down, Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Magic_Def_Down, Spell = "Erase",    Severity = 0.33 },
            new Removal { Debuff = StatusEffect.Burn,           Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Frost,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Choke,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Rasp,           Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Shock,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Drown,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Flash,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Helix,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Max_HP_Down,    Spell = "Erase",    Severity = 0.28 },
            new Removal { Debuff = StatusEffect.Max_MP_Down,    Spell = "Erase",    Severity = 0.28 },
            new Removal { Debuff = StatusEffect.Max_TP_Down,    Spell = "Erase",    Severity = 0.28 },
            new Removal { Debuff = StatusEffect.STR_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.DEX_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.VIT_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.AGI_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.INT_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.MND_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.CHR_Down,       Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.Requiem,        Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.Elegy,          Spell = "Erase",    Severity = 0.25 },
            new Removal { Debuff = StatusEffect.Threnody,       Spell = "Erase",    Severity = 0.25 },
        };

        /// <summary>
        /// Windower items.lua ids of every Yagrush carrying the always-on Divine Veil
        /// ("Enhances Divine Veil effect" — every status-removal spell lands AoE,
        /// 100% per BG-wiki). Covers level 75 through 119 III; the Incomplete version
        /// (18973) has no Divine Veil line and is deliberately excluded.
        /// </summary>
        private static readonly HashSet<int> YagrushItemIds = new HashSet<int>
        {
            18993, 19062, 19082, 19614, 19712, 19821, 19950, 21062, 21063, 21078,
        };

        /// <summary>
        /// Spells an SCH main can only cast under Addendum: White
        /// (https://www.bg-wiki.com/ffxi/Addendum:_White). Raise/Reraise tiers are
        /// listed for completeness — no module raises yet.
        /// </summary>
        public static readonly IReadOnlyCollection<string> AddendumWhiteSpells =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Poisona", "Paralyna", "Blindna", "Silena", "Cursna", "Viruna", "Stona", "Erase",
                "Reraise", "Raise II", "Raise III", "Reraise II", "Reraise III",
            };

        /// <summary>Item ids (Windower items.lua) for the PL self-removal options.</summary>
        public static readonly IReadOnlyDictionary<string, int> RemovalItemIds =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Echo Drops", 4151 },
                { "Catholicon", 4206 },
                { "Remedy", 4155 },
                { "Vicar's Drink", 5439 },
                { "Holy Water", 4154 },
                { "Hallowed Water", 5306 },
            };

        /// <summary>Dropdown content for the PL silence item setting.</summary>
        public static readonly IReadOnlyList<string> SilenceItems =
            new[] { "Echo Drops", "Catholicon", "Remedy", "Vicar's Drink" };

        /// <summary>Dropdown content for the PL doom item setting.</summary>
        public static readonly IReadOnlyList<string> DoomItems =
            new[] { "Holy Water", "Hallowed Water" };

        /// <summary>Dropdown content for the wake-sleep spell setting.</summary>
        public static readonly IReadOnlyList<string> WakeSleepSpells =
            new[] { "Cure", "Cura", "Curaga" };

        /// <summary>
        /// (settings name, removal spell) for every removal the module can perform —
        /// drives the settings-page checkboxes. Names match <see cref="DebuffSettings.EnabledRemovals"/>.
        /// </summary>
        public static readonly IReadOnlyList<(string Name, string Spell)> KnownRemovals = BuildKnownRemovals();

        private static string SettingName(Removal removal)
        {
            return removal.Debuff.ToString();
        }

        private static IReadOnlyList<(string Name, string Spell)> BuildKnownRemovals()
        {
            var list = new List<(string Name, string Spell)>(Removals.Length + 1)
            {
                ("Sleep", "Cure/Cura/Curaga"),
            };
            foreach (Removal removal in Removals)
            {
                list.Add((SettingName(removal), removal.Spell));
            }

            return list;
        }

        private const float RemovalRange = 21f;

        private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan TransientRetry = TimeSpan.FromSeconds(3);

        private readonly GameStateService _state;

        /// <summary>How long after execution before a JA may be offered again.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        /// <summary>JA name -> last execution time (prep verify windows).</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, DateTime> _lastFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _shortRetry = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public DebuffModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Debuffs";

        /// <summary>Zone hygiene: JA verify windows and failed-removal retries never survive a zone.</summary>
        public void OnZoneChanged()
        {
            _jaAttempted.Clear();
            _lastFailure.Clear();
            _shortRetry.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            if (!settings.Debuffs.Enabled)
            {
                return;
            }

            PlayerState pl = snapshot.Player;

            // Stage removal candidates so the addendum gate and the group-AoE pass
            // can rewrite them before they join the shared list.
            var staged = new List<ActionCandidate>();
            var addendumBlocked = new List<ActionCandidate>();

            ScanTarget(snapshot, settings, staged, addendumBlocked, pl.Name, pl.Buffs, isMonitored: false);

            if (!string.IsNullOrEmpty(snapshot.MonitoredName) && snapshot.MonitoredBuffs.Length > 0)
            {
                ScanTarget(snapshot, settings, staged, addendumBlocked, snapshot.MonitoredName, snapshot.MonitoredBuffs, isMonitored: true);
            }

            // Addon-provided party buffs (own party only). Skip names already scanned
            // from memory to avoid duplicate candidates.
            foreach (KeyValuePair<string, short[]> pair in snapshot.PartyBuffs)
            {
                if (pair.Key.Equals(pl.Name, StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals(snapshot.MonitoredName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ScanTarget(snapshot, settings, staged, addendumBlocked, pair.Key, pair.Value, isMonitored: false);
            }

            // Addendum: White gate (SCH main only, and only when the sub job does not
            // provide the spell natively): without the buff the cast fails in-game.
            // Prep it (Light Arts + a charge) and hold the blocked removals.
            if (addendumBlocked.Count > 0
                && pl.HasBuff(StatusEffect.Light_Arts)
                && !RecentlyAttempted("Addendum: White")
                && _state.HasAbility("Addendum: White")
                && Stratagems.AvailableCharges(_state, pl) > 0)
            {
                double best = addendumBlocked.Max(c => c.Score);
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.JobAbility,
                    Score = best + 5,
                    ActionName = "Addendum: White",
                    TargetName = "<me>",
                    IsJobAbility = true,
                    Description = "Addendum: White (before -na removal)",
                });
            }

            ApplyGroupRemoval(snapshot, settings, pl, staged, candidates);
            candidates.AddRange(staged);

            // PL self-removal items: usable while silenced/doomed, when nothing the
            // PL could cast would work (a silenced PL cannot cast Silena on itself).
            if (pl.HasBuff(StatusEffect.Silence) && settings.Debuffs.PlSilenceItemEnabled)
            {
                OfferItem(snapshot, settings, candidates, settings.Debuffs.PlSilenceItem, 1.10, "Silence");
            }
            else if (pl.HasBuff(StatusEffect.Doom) && settings.Debuffs.PlDoomItemEnabled)
            {
                OfferItem(snapshot, settings, candidates, settings.Debuffs.PlDoomItem, 1.05, "Doom");
            }

            // Divine Caress (WHM JA): fire before a removal so the cured target also
            // gains immunity. Offered just above the best removal candidate so it
            // executes first; the removal is offered again next tick. Presence-gated
            // on the buff, so it does not stack or spam.
            if (settings.Debuffs.UseDivineCaress)
            {
                double best = double.MinValue;
                foreach (ActionCandidate c in candidates)
                {
                    if (c.Category == ActionCategory.DebuffRemoval && c.Score > best)
                    {
                        best = c.Score;
                    }
                }

                if (best != double.MinValue
                    && !pl.HasBuff(StatusEffect.Divine_Caress)
                    && _state.HasAbility("Divine Caress")
                    && _state.GetAbilityRecast("Divine Caress") == 0
                    && !RecentlyAttempted("Divine Caress"))
                {
                    candidates.Add(new ActionCandidate
                    {
                        Category = ActionCategory.JobAbility,
                        Score = best + 5,
                        ActionName = "Divine Caress",
                        TargetName = "<me>",
                        IsJobAbility = true,
                        Description = "Divine Caress (before removal)",
                    });
                }
            }
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility)
            {
                _jaAttempted[candidate.ActionName] = DateTime.UtcNow;
                return;
            }

            if (candidate.Category != ActionCategory.DebuffRemoval)
            {
                return;
            }

            string key = candidate.ActionName + "|" + candidate.TargetName;
            if (result.Succeeded)
            {
                _lastFailure.Remove(key);
                _shortRetry.Remove(key);
            }
            else if (result.Outcome == CastOutcome.NotStarted || result.Outcome == CastOutcome.Interrupted
                || result.Outcome == CastOutcome.Unknown)
            {
                _lastFailure.Remove(key);
                _shortRetry[key] = DateTime.UtcNow;
            }
            else
            {
                _shortRetry.Remove(key);
                _lastFailure[key] = DateTime.UtcNow;
            }
        }

        private bool RecentlyAttempted(string abilityName)
        {
            return _jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow;
        }

        private bool RemovalOnBackoff(string spellName, string targetName)
        {
            string key = spellName + "|" + targetName;
            if (_lastFailure.TryGetValue(key, out DateTime failed)
                && DateTime.UtcNow - failed < RetryBackoff)
            {
                return true;
            }

            return _shortRetry.TryGetValue(key, out DateTime retry)
                && DateTime.UtcNow - retry < TransientRetry;
        }

        /// <summary>
        /// Group removals: when enough distinct members share an ailment removable by
        /// the same -na/Erase spell, one AoE cast clears them all. Mechanism order:
        /// Yagrush (free) -> Accession (1 stratagem charge) -> Divine Seal (WHM main
        /// 50+, 10-minute recast). While a prep JA is pending the individual casts
        /// for that spell are held; with no mechanism available they fall back to
        /// plain per-member removal.
        /// </summary>
        private void ApplyGroupRemoval(
            GameSnapshot snapshot,
            CurePleaseSettings settings,
            PlayerState pl,
            List<ActionCandidate> staged,
            List<ActionCandidate> candidates)
        {
            int min = settings.Debuffs.GroupRemovalMinCount;
            if (min < 2 || snapshot.PartyFromMonitored)
            {
                return;
            }

            foreach (IGrouping<string, ActionCandidate> group in staged
                .Where(c => AddendumWhiteSpells.Contains(c.ActionName))
                .GroupBy(c => c.ActionName, StringComparer.OrdinalIgnoreCase)
                .ToList())
            {
                if (group.Select(c => c.TargetName).Distinct(StringComparer.OrdinalIgnoreCase).Count() < min)
                {
                    continue;
                }

                ActionCandidate best = group.OrderByDescending(c => c.Score).First();

                // Yagrush: always-on Divine Veil, no JA — the plain cast is the AoE.
                if (HasYagrush())
                {
                    DropGroup(staged, group, best, " (AoE: Yagrush)");
                    continue;
                }

                bool hasScholar = pl.MainJob == JobScholar || pl.SubJob == JobScholar;
                if (settings.Debuffs.GroupRemovalUseAccession && hasScholar)
                {
                    if (pl.HasBuff(StatusEffect.Accession))
                    {
                        DropGroup(staged, group, best, " (Accession)");
                        continue;
                    }

                    if (RecentlyAttempted("Accession"))
                    {
                        // JA fired — hold the group until the buff lands.
                        staged.RemoveAll(c => group.Contains(c));
                        continue;
                    }

                    if (AccessionReady(pl))
                    {
                        staged.RemoveAll(c => group.Contains(c));
                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.JobAbility,
                            Score = best.Score + 5,
                            ActionName = "Accession",
                            TargetName = "<me>",
                            IsJobAbility = true,
                            PrepBuff = StatusEffect.Accession,
                            PrepForSpell = best.ActionName,
                            Description = "Accession (before group " + best.ActionName + ")",
                        });
                        continue;
                    }

                    continue; // no charge: plain per-member removals
                }

                if (settings.Debuffs.GroupRemovalUseDivineSeal
                    && pl.MainJob == JobWhiteMage && pl.MainJobLevel >= 50
                    && !CureCriticalPending(snapshot, settings))
                {
                    if (pl.HasBuff(StatusEffect.Divine_Seal))
                    {
                        DropGroup(staged, group, best, " (Divine Seal)");
                        continue;
                    }

                    if (RecentlyAttempted("Divine Seal"))
                    {
                        // JA fired — hold the group until the buff lands.
                        staged.RemoveAll(c => group.Contains(c));
                        continue;
                    }

                    if (_state.HasAbility("Divine Seal")
                        && _state.GetAbilityRecast("Divine Seal") == 0)
                    {
                        staged.RemoveAll(c => group.Contains(c));
                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.JobAbility,
                            Score = best.Score + 5,
                            ActionName = "Divine Seal",
                            TargetName = "<me>",
                            IsJobAbility = true,
                            PrepBuff = StatusEffect.Divine_Seal,
                            PrepForSpell = best.ActionName,
                            Description = "Divine Seal (before group " + best.ActionName + ")",
                        });
                    }
                }
            }
        }

        /// <summary>Keeps one cast of a group (it lands AoE) and drops the rest.</summary>
        private static void DropGroup(
            List<ActionCandidate> staged, IEnumerable<ActionCandidate> group, ActionCandidate keep, string note)
        {
            staged.RemoveAll(c => group.Contains(c));
            keep.Description += note;
            staged.Add(keep);
        }

        /// <summary>Yagrush in any equipment slot (it is WHM-only, so no job gate needed).</summary>
        private bool HasYagrush()
        {
            foreach (int id in _state.GetEquippedItemIds().Values)
            {
                if (YagrushItemIds.Contains(id))
                {
                    return true;
                }
            }

            return false;
        }

        private bool AccessionReady(PlayerState pl)
        {
            bool whiteGrimoire = pl.HasBuff(StatusEffect.Light_Arts)
                || pl.HasBuff(StatusEffect.Addendum_White);
            return whiteGrimoire
                && _state.HasAbility("Accession")
                && Stratagems.AvailableCharges(_state, pl) > 0;
        }

        /// <summary>
        /// True when the spell is Addendum: White-gated for this player: SCH main,
        /// the spell is on the BG-wiki addendum list, and the sub job does not
        /// provide it natively (SCH/WHM gets -na spells from /WHM, no addendum).
        /// </summary>
        private static bool NeedsAddendum(PlayerState pl, EliteAPI.ISpell spell)
        {
            if (pl.MainJob != JobScholar || !AddendumWhiteSpells.Contains(spell.Name[0]))
            {
                return false;
            }

            short[] levels = spell.LevelRequired;
            if (levels == null)
            {
                return true; // unreadable levels: treat as gated (a wrong cast fails in-game)
            }

            int sub = pl.SubJob;
            bool subProvides = sub >= 0 && sub < levels.Length
                && levels[sub] > 0 && levels[sub] <= pl.SubJobLevel;
            return !subProvides;
        }

        /// <summary>
        /// Divine Seal's 10-minute recast is shared between group removal and the
        /// critical-cure doubling — the life-saver wins: when a cure target is at or
        /// below the critical threshold, group removal does not spend it.
        /// </summary>
        private static bool CureCriticalPending(GameSnapshot snapshot, CurePleaseSettings settings)
        {
            if (!settings.Cures.UseDivineSeal)
            {
                return false;
            }

            byte threshold = settings.Cures.DivineSealHpBelowPercent;
            if (snapshot.Player.HPP <= threshold)
            {
                return true;
            }

            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (member.Active && member.CurrentHP > 0 && member.CurrentHPP <= threshold
                    && snapshot.PartyMemberSelections.IsEnabled(member.Name))
                {
                    return true;
                }
            }

            return false;
        }

        private void OfferItem(
            GameSnapshot snapshot,
            CurePleaseSettings settings,
            List<ActionCandidate> candidates,
            string itemName,
            double severity,
            string debuff)
        {
            if (!RemovalItemIds.TryGetValue(itemName, out int itemId)
                || !_state.GetOwnedItemIds().Contains(itemId))
            {
                return;
            }

            candidates.Add(new ActionCandidate
            {
                Category = ActionCategory.Item,
                Score = settings.Priorities.DebuffWeight * severity,
                ActionName = itemName,
                TargetName = "<me>",
                Description = string.Format("{0} -> <me> ({1})", itemName, debuff),
            });
        }

        private void ScanTarget(
            GameSnapshot snapshot,
            CurePleaseSettings settings,
            List<ActionCandidate> candidates,
            List<ActionCandidate> addendumBlocked,
            string targetName,
            short[] buffs,
            bool isMonitored)
        {
            PlayerState pl = snapshot.Player;
            if (!snapshot.PartyMemberSelections.IsEnabled(targetName))
            {
                return;
            }

            bool isSelf = targetName.Equals(pl.Name, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !InRemovalRange(snapshot, targetName))
            {
                return;
            }

            double priority = isMonitored ? settings.Priorities.MonitoredWeight : 1.0;

            // Sleep/Sleep2: woken by any cure landing on the target. Never on the
            // PL — a sleeping PL cannot cast (the rest of the scan is pointless
            // then too: asleep means no spells at all).
            if (!isSelf
                && settings.Debuffs.WakeSleepEnabled
                && settings.Debuffs.EnabledRemovals.Contains("Sleep")
                && (HasBuff(buffs, StatusEffect.Sleep) || HasBuff(buffs, StatusEffect.Sleep2)))
            {
                EliteAPI.ISpell wake = SpellBook.IsCastable(_state, settings.Debuffs.WakeSleepSpell, pl.MP);
                if (wake != null && !RemovalOnBackoff(wake.Name[0], targetName))
                {
                    candidates.Add(new ActionCandidate
                    {
                        Category = ActionCategory.DebuffRemoval,
                        Score = settings.Priorities.DebuffWeight * 0.92 * priority,
                        ActionName = wake.Name[0],
                        TargetName = targetName,
                        Description = string.Format("{0} -> {1} (wake)", wake.Name[0], targetName),
                    });
                }
            }

            foreach (Removal removal in Removals)
            {
                if (!settings.Debuffs.EnabledRemovals.Contains(SettingName(removal)))
                {
                    continue;
                }

                if (!HasBuff(buffs, removal.Debuff))
                {
                    continue;
                }

                // A silenced PL cannot cast Silena on themselves — the item path
                // (or another healer) covers it. Don't offer a doomed-to-fail cast.
                if (isSelf && removal.Debuff == StatusEffect.Silence)
                {
                    continue;
                }

                // Correct spell gate (legacy bug: Poisona branch checked JobChecker("Erase")).
                EliteAPI.ISpell spell = SpellBook.IsCastable(_state, removal.Spell, pl.MP);
                if (spell == null || RemovalOnBackoff(spell.Name[0], targetName))
                {
                    continue;
                }

                var candidate = new ActionCandidate
                {
                    Category = ActionCategory.DebuffRemoval,
                    Score = settings.Priorities.DebuffWeight * removal.Severity * priority,
                    ActionName = spell.Name[0],
                    TargetName = targetName,
                    Description = string.Format("{0} -> {1} ({2})", spell.Name[0], targetName, removal.Debuff),
                };

                // SCH main without the sub job providing the spell: Addendum: White
                // must be up or the cast fails in-game (the resource DB lists -na
                // spells with SCH levels, so the level gate alone cannot tell).
                if (!pl.HasBuff(StatusEffect.Addendum_White) && NeedsAddendum(pl, spell))
                {
                    addendumBlocked.Add(candidate);
                    continue;
                }

                candidates.Add(candidate);
            }
        }

        private bool InRemovalRange(GameSnapshot snapshot, string targetName)
        {
            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (member.Active && member.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return member.CurrentHP > 0 && member.Zone == snapshot.Player.ZoneId
                        && _state.GetEntityDistance(member.TargetIndex) <= RemovalRange;
                }
            }

            EntityInfo entity = _state.FindEntityByName(targetName);
            return entity != null && entity.Distance <= RemovalRange;
        }

        private static bool HasBuff(short[] buffs, StatusEffect effect)
        {
            foreach (short buff in buffs)
            {
                if (buff == (short)effect)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
