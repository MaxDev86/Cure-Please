using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Red mage job abilities: Composure upkeep and Convert.
    ///  - Composure is presence-based off the player's own buffs and MAIN JOB ONLY
    ///    in-game (a RDM sub job never has it);
    ///  - Convert (swap HP and MP) fires when MPP drops to the configured percent,
    ///    works from a sub job, and never fires while weakened (legacy rule —
    ///    converting at weakness HP is usually fatal).
    /// Verified like the other JA modules (see SchModule).
    /// </summary>
    public sealed class RdmModule : IDecisionModule
    {
        private const byte JobRedMage = 5;

        /// <summary>How long after execution before a JA may be offered again.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        private readonly GameStateService _state;

        /// <summary>JA name -> last execution time.</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public RdmModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Red Mage";

        /// <summary>Zone hygiene: the JA verify window (seconds) never survives a zone anyway.</summary>
        public void OnZoneChanged()
        {
            _jaAttempted.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            RdmSettings rdm = settings.Rdm;
            PlayerState pl = snapshot.Player;

            if (!rdm.Enabled || (pl.MainJob != JobRedMage && pl.SubJob != JobRedMage))
            {
                return;
            }

            if (pl.EntityStatus == EntityStatus.Healing)
            {
                return;
            }

            // Convert first: at single-digit MP nothing else matters.
            if (rdm.UseConvert
                && pl.MPP <= rdm.ConvertMpBelowPercent
                && !pl.HasBuff(StatusEffect.Weakness)
                && Ready("Convert"))
            {
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.JobAbility,
                    Score = settings.Priorities.RdmWeight + 2,
                    ActionName = "Convert",
                    TargetName = "<me>",
                    IsJobAbility = true,
                    Description = "Convert (rdm JA, " + pl.MPP + "% MP)",
                });
            }

            // Composure is main-job only in-game, sub job RDM never has it.
            if (rdm.UseComposure && pl.MainJob == JobRedMage
                && !pl.HasBuff(StatusEffect.Composure) && Ready("Composure"))
            {
                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.JobAbility,
                    Score = settings.Priorities.RdmWeight,
                    ActionName = "Composure",
                    TargetName = "<me>",
                    IsJobAbility = true,
                    Description = "Composure (rdm JA)",
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

        private bool Ready(string abilityName)
        {
            if (_state.GetAbilityRecast(abilityName) != 0 || !_state.HasAbility(abilityName))
            {
                return false;
            }

            return !_jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                || DateTime.UtcNow - attempted >= JaVerifyWindow;
        }
    }
}
