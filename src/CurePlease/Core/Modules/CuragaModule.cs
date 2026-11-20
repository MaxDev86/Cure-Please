using System;
using System.Collections.Generic;
using System.Linq;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Party-wide cures. Fires when enough reachable, same-zone, own-party members
    /// are below the Curaga threshold, centered on the lowest-HPP member. Scored against single-target
    /// cures, so it no longer pre-empts an emergency heal the way the legacy fixed
    /// order did.
    ///
    /// Two ways to heal the party: the native Curaga line (WHM), or — on SCH main
    /// or sub — Accession + Cure I–IV (Cure V/VI are not Accession-compatible).
    /// The Accession path preps the JA first and holds the cast until the buff
    /// lands (10s verify window), then casts the cure on the center member;
    /// whoever is in range gets healed. Falls back to the Curaga line when no
    /// stratagem charge is available.
    /// </summary>
    public sealed class CuragaModule : IDecisionModule
    {
        private const float CuragaRange = 21f;

        private const byte JobScholar = 20;

        /// <summary>How long after execution before a prep JA may be offered again.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        private readonly GameStateService _state;

        /// <summary>JA name -> last execution time (Accession verify window).</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public CuragaModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Curaga";

        /// <summary>Zone hygiene: the JA verify window (seconds) never survives a zone anyway.</summary>
        public void OnZoneChanged()
        {
            _jaAttempted.Clear();
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility)
            {
                _jaAttempted[candidate.ActionName] = DateTime.UtcNow;
            }
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            if (!settings.Curaga.Enabled)
            {
                return;
            }

            PlayerState pl = snapshot.Player;

            // AoE cures only land on the OWN party — alliance members (slots 6-17)
            // neither count toward the threshold nor receive the heal.
            List<PartyMemberState> injured = snapshot.PartyMembers
                .Where(m => m.Active
                            && m.PartyIndex == 0
                            && m.CurrentHP > 0
                            && m.Zone == pl.ZoneId
                            && m.CurrentHPP <= settings.Curaga.CurePercentage
                            && (m.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase)
                                || _state.GetEntityDistance(m.TargetIndex) <= CuragaRange))
                .OrderBy(m => m.CurrentHPP)
                .ToList();

            int requiredMembers = Math.Max(1, settings.Curaga.RequiredMembers);
            if (injured.Count < requiredMembers)
            {
                return;
            }

            PartyMemberState center = injured[0];

            if (_state.GetEntityDistance(center.TargetIndex) > CuragaRange
                && !center.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            double avgUrgency = injured.Average(m => (100.0 - m.CurrentHPP) / 100.0);
            double coverage = (double)injured.Count / requiredMembers;
            double score = settings.Priorities.CuragaWeight * avgUrgency * Math.Min(coverage, 2.0);

            // Accession path (SCH main or sub): Cure I–IV as the party heal. The
            // cure must actually be castable before a charge is prepped.
            bool accessionMode = settings.Curaga.UseAccession
                && (pl.MainJob == JobScholar || pl.SubJob == JobScholar);
            if (accessionMode)
            {
                EliteAPI.ISpell cureSpell = SpellBook.ResolveTier(
                    _state, SpellBook.AccessionCureTierNames, SpellBook.AccessionCureTiers(settings.Cures.Tiers),
                    center.EstimatedHpLoss, pl.MP,
                    settings.Cures.Undercure, settings.Cures.Overcure);

                if (cureSpell != null)
                {
                    if (pl.HasBuff(StatusEffect.Accession))
                    {
                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.Curaga,
                            Score = score,
                            ActionName = cureSpell.Name[0],
                            TargetName = center.Name,
                            Description = string.Format("{0} -> {1} (Accession, +{2} injured)",
                                cureSpell.Name[0], center.Name, injured.Count - 1),
                        });
                        return; // Accession is up: never fire a native Curaga under it.
                    }

                    if (RecentlyAttempted("Accession"))
                    {
                        return; // JA fired — hold the party heal until the buff lands.
                    }

                    if (StratagemReady(pl))
                    {
                        candidates.Add(new ActionCandidate
                        {
                            Category = ActionCategory.JobAbility,
                            Score = score + 5,
                            ActionName = "Accession",
                            TargetName = "<me>",
                            IsJobAbility = true,
                            PrepBuff = StatusEffect.Accession,
                            Description = "Accession (before party cure)",
                        });
                        return;
                    }
                }
                // No castable cure or no stratagem: fall through to the Curaga line.
            }

            EliteAPI.ISpell spell = SpellBook.ResolveTier(
                _state, SpellBook.CuragaTierNames, settings.Curaga.Tiers,
                center.EstimatedHpLoss, pl.MP,
                settings.Cures.Undercure, settings.Cures.Overcure);

            if (spell == null)
            {
                return;
            }

            candidates.Add(new ActionCandidate
            {
                Category = ActionCategory.Curaga,
                Score = score,
                ActionName = spell.Name[0],
                TargetName = center.Name,
                Description = string.Format("{0} -> {1} (+{2} injured)", spell.Name[0], center.Name, injured.Count - 1),
            });
        }

        /// <summary>
        /// Stratagem availability: White Grimoire up, ability known, and a charge
        /// free on the shared stratagem timer (id 231).
        /// </summary>
        private bool StratagemReady(PlayerState pl)
        {
            bool whiteGrimoire = pl.HasBuff(StatusEffect.Light_Arts)
                || pl.HasBuff(StatusEffect.Addendum_White);
            if (!whiteGrimoire || !_state.HasAbilityForUse("Accession"))
            {
                return false;
            }

            return Stratagems.AvailableCharges(_state, pl) > 0;
        }

        private bool RecentlyAttempted(string abilityName)
        {
            return _jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow;
        }
    }
}
