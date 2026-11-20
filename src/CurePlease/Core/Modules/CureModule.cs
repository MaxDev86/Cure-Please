using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Single-target cures. Replaces the legacy monitored-veto + high-priority-first +
    /// everyone-else ordering with scored candidates: urgency (how low the target is)
    /// times priority weight (monitored / high-priority / normal). Highest score wins,
    /// so a dying normal member outranks a scratched monitored one — no starvation.
    /// </summary>
    public sealed class CureModule : IDecisionModule
    {
        private const float CureRange = 21f;

        private const byte JobPaladin = 7;
        private const byte JobScholar = 20;
        private const byte JobWhiteMage = 3;

        /// <summary>How long after execution before a prep JA may be offered again.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        private readonly GameStateService _state;

        /// <summary>JA name -> last execution time (Majesty verify window).</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public CureModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Cures";

        /// <summary>Zone hygiene: the JA verify window (seconds) never survives a zone anyway.</summary>
        public void OnZoneChanged()
        {
            _jaAttempted.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            PlayerState pl = snapshot.Player;
            string monitored = snapshot.MonitoredName ?? string.Empty;

            // Cure V/VI are not Accession-compatible: while the buff is up, resolve
            // from Cure IV down so any cure the engine picks spreads to the party
            // instead of wasting the stratagem on a single target.
            bool accessionUp = pl.HasBuff(StatusEffect.Accession);
            EliteAPI.ISpell ResolveCure(uint hpLoss, uint mp, bool overcure)
            {
                return accessionUp
                    ? SpellBook.ResolveTier(_state, SpellBook.AccessionCureTierNames,
                        SpellBook.AccessionCureTiers(settings.Cures.Tiers), hpLoss, mp,
                        settings.Cures.Undercure, overcure)
                    : SpellBook.ResolveTier(_state, SpellBook.CureTierNames,
                        settings.Cures.Tiers, hpLoss, mp,
                        settings.Cures.Undercure, overcure);
            }

            int cureCandidates = 0;
            double bestCureScore = 0;
            bool bestCureQualifiesForRapture = false;
            byte bestCureTargetHpp = 100;

            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (!member.Active || member.CurrentHP == 0
                    || !snapshot.PartyMemberSelections.IsEnabled(member.Name))
                {
                    continue;
                }

                // Alliance parties: cures only (buffs unreadable), and only when enabled.
                if (member.PartyIndex > 0 && !settings.Cures.CureAllianceEnabled)
                {
                    continue;
                }

                // Same-zone check mirrors legacy semantics (Zone equals player zone when in-zone).
                if (member.Zone != pl.ZoneId && !member.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isMonitored = monitored.Length != 0
                                   && member.Name.Equals(monitored, StringComparison.OrdinalIgnoreCase);
                bool isHighPriority = ContainsName(settings.Priorities.HighPriorityMembers, member.Name);

                byte threshold = isMonitored
                    ? settings.Cures.MonitoredCurePercentage
                    : isHighPriority
                        ? settings.Cures.PriorityCurePercentage
                        : settings.Cures.CurePercentage;

                if (member.CurrentHPP > threshold)
                {
                    continue;
                }

                if (_state.GetEntityDistance(member.TargetIndex) > CureRange
                    && !member.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EliteAPI.ISpell spell = ResolveCure(
                    member.EstimatedHpLoss, pl.MP,
                    settings.Cures.Overcure && (!settings.Cures.OvercureOnHighPriority || isHighPriority));

                if (spell == null)
                {
                    continue;
                }

                double priority = isMonitored
                    ? settings.Priorities.MonitoredWeight
                    : isHighPriority
                        ? settings.Priorities.HighPriorityWeight
                        : 1.0;

                double urgency = (100.0 - member.CurrentHPP) / 100.0;
                double score = settings.Priorities.CureWeight * urgency * priority;

                if (member.PartyIndex == 0 && !snapshot.PartyFromMonitored)
                {
                    cureCandidates++; // Majesty only reaches the PL's own party.
                }

                if (score > bestCureScore)
                {
                    bestCureScore = score;
                    bestCureTargetHpp = member.CurrentHPP;
                    bestCureQualifiesForRapture = member.CurrentHPP <= settings.Sch.RaptureCureHpBelowPercent
                        && member.EstimatedHpLoss >= settings.Sch.RaptureMinimumHpLoss;
                }

                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.Cure,
                    Score = score,
                    ActionName = spell.Name[0],
                    TargetName = member.Name,
                    IsEmergency = member.CurrentHPP <= settings.Cures.IntentEmergencyHpPercent,
                    Description = string.Format("{0} -> {1} ({2}% HP, ~{3} missing)",
                        spell.Name[0], member.Name, member.CurrentHPP, member.EstimatedHpLoss),
                });
            }

            // Out-of-party self-heal (PL not in the party list).
            if (settings.General.EnableOutOfPartyHealing && pl.HP > 0 && !IsInParty(snapshot, pl.Name))
            {
                if (pl.HPP <= settings.Cures.MonitoredCurePercentage)
                {
                    EliteAPI.ISpell spell = ResolveCure(
                        pl.EstimatedHpLoss, pl.MP, settings.Cures.Overcure);

                    if (spell != null)
                    {
                        double urgency = (100.0 - pl.HPP) / 100.0;
                        double score = settings.Priorities.CureWeight * urgency * settings.Priorities.MonitoredWeight;
                        if (score > bestCureScore)
                        {
                            bestCureScore = score;
                            bestCureTargetHpp = (byte)Math.Min(pl.HPP, 100);
                            bestCureQualifiesForRapture = pl.HPP <= settings.Sch.RaptureCureHpBelowPercent
                                && pl.EstimatedHpLoss >= settings.Sch.RaptureMinimumHpLoss;
                        }

                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.Cure,
                            Score = score,
                            ActionName = spell.Name[0],
                            TargetName = pl.Name,
                            IsEmergency = pl.HPP <= settings.Cures.IntentEmergencyHpPercent,
                            Description = string.Format("{0} -> self ({1}% HP)", spell.Name[0], pl.HPP),
                        });
                    }
                }
            }

            // Rapture (SCH stratagem) before a qualifying cure. Regular use leaves
            // the configured reserve untouched; at maximum charges the optional
            // harvest pre-charges it instead of wasting recharge progress.
            if (settings.Sch.Enabled && settings.Sch.UseRapture
                && !pl.HasBuff(StatusEffect.Rapture)
                && RaptureReady(pl, settings.Sch, out int maxCharges, out int charges)
                && !RecentlyAttempted("Rapture"))
            {
                bool harvest = settings.Sch.RaptureHarvestAtFullCharge && maxCharges > 0 && charges == maxCharges;
                if (bestCureQualifiesForRapture || harvest)
                {
                    candidates.Add(new ActionCandidate
                    {
                        Category = ActionCategory.JobAbility,
                        Score = bestCureScore > 0 ? bestCureScore + 5 : settings.Priorities.SchWeight + 1,
                        ActionName = "Rapture",
                        TargetName = "<me>",
                        IsJobAbility = true,
                        PrepBuff = StatusEffect.Rapture,
                        Description = harvest && !bestCureQualifiesForRapture
                            ? "Rapture (full-charge harvest)"
                            : "Rapture (before cure)",
                    });
                }
            }

            // Divine Seal (WHM 15, sub-job accessible): doubles the next cure. Fires
            // when the best cure target is critically low. Shares the 10-minute
            // recast with the group-removal use — DebuffModule suppresses its side
            // while a cure target is critical, so the life-saver use wins.
            if (settings.Cures.UseDivineSeal && bestCureScore > 0
                && bestCureTargetHpp <= settings.Cures.DivineSealHpBelowPercent
                && (pl.MainJob == JobWhiteMage || pl.SubJob == JobWhiteMage)
                && !pl.HasBuff(StatusEffect.Divine_Seal)
                && _state.HasAbility("Divine Seal")
                && _state.GetAbilityRecast("Divine Seal") == 0
                && !RecentlyAttempted("Divine Seal"))
            {
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.JobAbility,
                    Score = bestCureScore + 5,
                    ActionName = "Divine Seal",
                    TargetName = "<me>",
                    IsJobAbility = true,
                    PrepBuff = StatusEffect.Divine_Seal,
                    Description = "Divine Seal (doubled cure)",
                });
            }

            // Majesty (PLD JA): the next cure lands AoE, so fire it when at least 2
            // members are cure candidates. Offered just above the best cure so it
            // executes first; the cure is offered again next tick. Presence-gated on
            // the buff, so it does not stack or spam. (Majesty also makes Protect and
            // Shell AoE — the buff upkeep module does not sequence it deliberately.)
            if (settings.Cures.UseMajesty
                && cureCandidates >= 2
                && (pl.MainJob == JobPaladin || pl.SubJob == JobPaladin)
                && !pl.HasBuff(StatusEffect.Majesty)
                && _state.HasAbility("Majesty")
                && _state.GetAbilityRecast("Majesty") == 0
                && !RecentlyAttempted("Majesty"))
            {
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.JobAbility,
                    Score = bestCureScore + 5,
                    ActionName = "Majesty",
                    TargetName = "<me>",
                    IsJobAbility = true,
                    PrepBuff = StatusEffect.Majesty,
                    Description = "Majesty (before AoE cure)",
                });
            }
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility)
            {
                _jaAttempted[candidate.ActionName] = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Rapture is usable on SCH main at 55+, or from /SCH only when the effective
        /// sub level is 55+ and the main job is master-leveled. It also needs White
        /// Grimoire active and enough stratagem charges to leave the configured reserve.
        /// </summary>
        private bool RaptureReady(PlayerState pl, SchSettings sch, out int maxCharges, out int charges)
        {
            maxCharges = 0;
            charges = 0;

            bool hasScholar = pl.MainJob == JobScholar || pl.SubJob == JobScholar;
            if (!hasScholar)
            {
                return false;
            }

            bool levelOk = pl.MainJob == JobScholar
                ? pl.MainJobLevel >= 55
                : pl.SubJobLevel >= 55 && pl.MainJobMasterLevel > 0;
            if (!levelOk)
            {
                return false;
            }

            bool whiteGrimoire = pl.HasBuff(StatusEffect.Light_Arts)
                || pl.HasBuff(StatusEffect.Addendum_White);
            if (!whiteGrimoire || !_state.HasAbilityForUse("Rapture"))
            {
                return false;
            }

            maxCharges = Stratagems.MaxChargesForLevel(Stratagems.ScholarLevel(pl));
            charges = Stratagems.AvailableCharges(_state, pl);
            return charges > sch.RaptureReserveCharges;
        }

        private bool RecentlyAttempted(string abilityName)
        {
            return _jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow;
        }

        private static bool ContainsName(List<string> names, string name)
        {
            foreach (string entry in names)
            {
                if (entry.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInParty(GameSnapshot snapshot, string name)
        {
            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (member.Active && member.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
