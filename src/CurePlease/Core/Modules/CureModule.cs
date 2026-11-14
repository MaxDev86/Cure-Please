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

        private readonly GameStateService _state;

        public CureModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Cures";

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            PlayerState pl = snapshot.Player;
            string monitored = snapshot.MonitoredName ?? string.Empty;

            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (!member.Active || member.CurrentHP == 0)
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

                EliteAPI.ISpell spell = SpellBook.ResolveTier(
                    _state, SpellBook.CureTierNames, settings.Cures.Tiers,
                    member.EstimatedHpLoss, pl.MP,
                    settings.Cures.Undercure,
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

                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.Cure,
                    Score = score,
                    ActionName = spell.Name[0],
                    TargetName = member.Name,
                    Description = string.Format("{0} -> {1} ({2}% HP, ~{3} missing)",
                        spell.Name[0], member.Name, member.CurrentHPP, member.EstimatedHpLoss),
                });
            }

            // Out-of-party self-heal (PL not in the party list).
            if (settings.General.EnableOutOfPartyHealing && pl.HP > 0 && !IsInParty(snapshot, pl.Name))
            {
                if (pl.HPP <= settings.Cures.MonitoredCurePercentage)
                {
                    EliteAPI.ISpell spell = SpellBook.ResolveTier(
                        _state, SpellBook.CureTierNames, settings.Cures.Tiers,
                        pl.EstimatedHpLoss, pl.MP,
                        settings.Cures.Undercure, settings.Cures.Overcure);

                    if (spell != null)
                    {
                        double urgency = (100.0 - pl.HPP) / 100.0;
                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.Cure,
                            Score = settings.Priorities.CureWeight * urgency * settings.Priorities.MonitoredWeight,
                            ActionName = spell.Name[0],
                            TargetName = pl.Name,
                            Description = string.Format("{0} -> self ({1}% HP)", spell.Name[0], pl.HPP),
                        });
                    }
                }
            }
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
