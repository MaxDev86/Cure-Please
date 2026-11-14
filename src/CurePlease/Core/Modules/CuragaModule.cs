using System;
using System.Collections.Generic;
using System.Linq;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// AoE cures. Fires when enough same-zone members are below the Curaga threshold,
    /// centered on the lowest-HPP member. Scored against single-target cures, so it
    /// no longer pre-empts an emergency heal the way the legacy fixed order did.
    /// </summary>
    public sealed class CuragaModule : IDecisionModule
    {
        private const float CuragaRange = 21f;

        private readonly GameStateService _state;

        public CuragaModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Curaga";

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            if (!settings.Curaga.Enabled)
            {
                return;
            }

            PlayerState pl = snapshot.Player;

            List<PartyMemberState> injured = snapshot.PartyMembers
                .Where(m => m.Active
                            && m.CurrentHP > 0
                            && m.Zone == pl.ZoneId
                            && m.CurrentHPP <= settings.Curaga.CurePercentage)
                .OrderBy(m => m.CurrentHPP)
                .ToList();

            if (injured.Count < settings.Curaga.RequiredMembers)
            {
                return;
            }

            PartyMemberState center = injured[0];

            if (_state.GetEntityDistance(center.TargetIndex) > CuragaRange
                && !center.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EliteAPI.ISpell spell = SpellBook.ResolveTier(
                _state, SpellBook.CuragaTierNames, settings.Curaga.Tiers,
                center.EstimatedHpLoss, pl.MP,
                settings.Cures.Undercure, settings.Cures.Overcure);

            if (spell == null)
            {
                return;
            }

            double avgUrgency = injured.Average(m => (100.0 - m.CurrentHPP) / 100.0);
            double coverage = (double)injured.Count / settings.Curaga.RequiredMembers;
            double score = settings.Priorities.CuragaWeight * avgUrgency * Math.Min(coverage, 2.0);

            candidates.Add(new ActionCandidate
            {
                Category = ActionCategory.Curaga,
                Score = score,
                ActionName = spell.Name[0],
                TargetName = center.Name,
                Description = string.Format("{0} -> {1} (+{2} injured)", spell.Name[0], center.Name, injured.Count - 1),
            });
        }
    }
}
