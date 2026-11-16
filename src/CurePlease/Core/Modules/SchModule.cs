using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Scholar upkeep: Light/Dark Arts, the Addenda, and Sublimation. Mirrors the
    /// legacy Form1 JA block, with the same fixes as the other modules:
    ///  - everything is presence-based off the player's own buffs (always readable),
    ///    so a dispelled art is re-upped immediately instead of on a timer;
    ///  - JAs are verified like the bard/geo prep JAs: after execution the module
    ///    waits for the buff to appear (10 s window) instead of re-offering every tick;
    ///  - Light Arts and Dark Arts are mutually exclusive in-game (each overwrites
    ///    the other): when both are enabled the module keeps whichever art is
    ///    currently up instead of flip-flopping every recast;
    ///  - the Addenda are ordinary stratagems (SCH level 10 / 30 per the Grimoire
    ///    tables) and work from a sub job, level permitting — unlike the actual
    ///    SCH merit stratagems (Altruism, Focalization, ...), which this module
    ///    does not manage. Their availability is gated on stratagem CHARGES
    ///    (shared timer 231, see <see cref="Stratagems"/>), not on the name-based
    ///    recast lookup, which reads "on recast" whenever charges are not full;
    ///
    ///
    /// Sublimation serves both purposes (charge and recover) with one JA, so it is
    /// handled explicitly: charge when neither Sublimation buff is up (and Refresh
    /// is not blocking it), recover when Sublimation Complete is up and the MP
    /// deficit has reached the configured threshold.
    /// </summary>
    public sealed class SchModule : IDecisionModule
    {
        private const byte JobScholar = 20;

        /// <summary>How long to wait for a JA buff to appear before proceeding without it.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        private enum AbilityState
        {
            Active,
            Ready,
            PendingVerify,
            Unavailable,
        }

        private readonly GameStateService _state;

        /// <summary>JA name -> last execution time (post-execution cooldown for the verify window).</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public SchModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Scholar";

        /// <summary>Zone hygiene: the JA verify window (seconds) never survives a zone anyway.</summary>
        public void OnZoneChanged()
        {
            _jaAttempted.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            SchSettings sch = settings.Sch;
            PlayerState pl = snapshot.Player;

            if (!sch.Enabled || (pl.MainJob != JobScholar && pl.SubJob != JobScholar))
            {
                return;
            }

            // Healing characters stand up on any JA — matches the geo gate.
            if (pl.EntityStatus == EntityStatus.Healing)
            {
                return;
            }

            double score = settings.Priorities.SchWeight;

            bool lightArts = pl.HasBuff(StatusEffect.Light_Arts);
            bool darkArts = pl.HasBuff(StatusEffect.Dark_Arts);
            bool addendumWhite = pl.HasBuff(StatusEffect.Addendum_White);
            bool addendumBlack = pl.HasBuff(StatusEffect.Addendum_Black);

            // Arts. The other art being up suppresses the offer, so enabling both
            // keeps the current stance instead of alternating every recast.
            if (sch.LightArts && !lightArts && !addendumWhite && !darkArts && !addendumBlack
                && AbilityStateOf(pl, "Light Arts", 0) == AbilityState.Ready)
            {
                candidates.Add(Ja("Light Arts", score));
            }

            if (sch.AddendumWhite && lightArts && !addendumWhite
                && StratagemState(pl, "Addendum: White") == AbilityState.Ready)
            {
                candidates.Add(Ja("Addendum: White", score));
            }

            if (sch.DarkArts && !darkArts && !addendumBlack && !lightArts && !addendumWhite
                && AbilityStateOf(pl, "Dark Arts", 0) == AbilityState.Ready)
            {
                candidates.Add(Ja("Dark Arts", score));
            }

            if (sch.AddendumBlack && darkArts && !addendumBlack
                && StratagemState(pl, "Addendum: Black") == AbilityState.Ready)
            {
                candidates.Add(Ja("Addendum: Black", score));
            }

            // Sublimation: one JA, two purposes.
            if (sch.Sublimation)
            {
                bool charging = pl.HasBuff(StatusEffect.Sublimation_Activated);
                bool complete = pl.HasBuff(StatusEffect.Sublimation_Complete);

                bool charge = !charging && !complete && !pl.HasBuff(StatusEffect.Refresh);
                bool recover = complete && pl.MPMax - pl.MP >= sch.SublimationMpDeficit;

                if ((charge || recover)
                    && AbilityStateOf(pl, "Sublimation", 0) == AbilityState.Ready)
                {
                    candidates.Add(Ja("Sublimation", score, charge ? "charge" : "recover MP"));
                }
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
        /// JA availability. Scholar JAs are always called with buff 0 ("no status buff
        /// to consult"): the buffs ARE the offer conditions above, so this is purely
        /// known + recast, with a post-execution cooldown via <see cref="_jaAttempted"/>
        /// so a just-used JA is not re-offered every tick while its buff lands.
        /// </summary>
        private AbilityState AbilityStateOf(PlayerState pl, string abilityName, StatusEffect buff)
        {
            if (buff != 0 && pl.HasBuff(buff))
            {
                return AbilityState.Active;
            }

            if (_state.GetAbilityRecast(abilityName) != 0 || !_state.HasAbility(abilityName))
            {
                return AbilityState.Unavailable;
            }

            if (_jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow)
            {
                return AbilityState.PendingVerify;
            }

            return AbilityState.Ready;
        }

        /// <summary>
        /// Stratagem availability (Addenda, and any future stratagem the module
        /// manages): known ability + at least one charge available. Charges come
        /// from the shared stratagem timer via <see cref="Stratagems"/> — the
        /// name-based recast lookup cannot be used here because it resolves to
        /// that same shared timer and reads "on recast" while charges recharge.
        /// The 550 JP recharge bonus only applies on SCH main.
        /// </summary>
        private AbilityState StratagemState(PlayerState pl, string abilityName)
        {
            if (!_state.HasAbility(abilityName))
            {
                return AbilityState.Unavailable;
            }

            int schLevel = pl.MainJob == JobScholar ? pl.MainJobLevel : pl.SubJobLevel;
            int maxCharges = Stratagems.MaxChargesForLevel(schLevel);
            bool reduced = pl.MainJob == JobScholar
                && _state.GetSpentJobPoints(JobScholar) >= Stratagems.ReducedRechargeJobPoints;
            int charges = Stratagems.AvailableCharges(
                _state.GetAbilityRecastByTimerId(Stratagems.TimerId), maxCharges, reduced);

            if (charges <= 0)
            {
                return AbilityState.Unavailable;
            }

            if (_jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow)
            {
                return AbilityState.PendingVerify;
            }

            return AbilityState.Ready;
        }

        private static ActionCandidate Ja(string name, double score, string what = null)
        {
            return new ActionCandidate
            {
                Category = ActionCategory.JobAbility,
                Score = score,
                ActionName = name,
                TargetName = "<me>",
                IsJobAbility = true,
                Description = what == null ? name + " (sch JA)" : name + " (sch JA, " + what + ")",
            };
        }
    }
}
