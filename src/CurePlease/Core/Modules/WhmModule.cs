using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// White mage job abilities: Afflatus stance upkeep and Devotion. Mirrors the
    /// legacy Form1 JA block with the same fixes as the other modules:
    ///  - stances are presence-based off the player's own buffs, so a dispelled
    ///    Afflatus is re-upped immediately instead of on a timer;
    ///  - Afflatus Solace and Misery overwrite each other in-game: when both are
    ///    enabled the module keeps whichever stance is currently up instead of
    ///    alternating every recast;
    ///  - JAs are verified like the bard/geo prep JAs: after execution the module
    ///    waits for the buff/effect window instead of re-offering every tick.
    ///
    /// Everything here is MAIN JOB ONLY: Afflatus stances are unavailable from a
    /// sub job, and Devotion is a merit ability (merits never work from a sub job),
    /// so the module gates on main job WHM rather than main-or-sub.
    ///
    /// Divine Caress is NOT here — it modifies debuff removals and lives in
    /// DebuffModule (DebuffSettings.UseDivineCaress).
    /// </summary>
    public sealed class WhmModule : IDecisionModule
    {
        private const byte JobWhiteMage = 3;

        /// <summary>How long after execution before a JA may be offered again.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        /// <summary>Devotion needs the target within this range (legacy: 10 yalms).</summary>
        private const float DevotionRange = 10f;

        /// <summary>Devotion costs the caster HP — only fires above this HPP (legacy: 80).</summary>
        private const uint DevotionMinSelfHpp = 80;

        /// <summary>Devotion only fires when the target is this low on MPP (legacy: 30).</summary>
        private const byte DevotionMaxTargetMpp = 30;

        private enum AbilityState
        {
            Active,
            Ready,
            PendingVerify,
            Unavailable,
        }

        private readonly GameStateService _state;

        /// <summary>JA name -> last execution time.</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public WhmModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "White Mage";

        /// <summary>Zone hygiene: the JA verify window (seconds) never survives a zone anyway.</summary>
        public void OnZoneChanged()
        {
            _jaAttempted.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            WhmSettings whm = settings.Whm;
            PlayerState pl = snapshot.Player;

            // Afflatus stances and Devotion are all main-job only (Devotion is a
            // merit ability), so this gates on the MAIN job, unlike other modules.
            if (!whm.Enabled || pl.MainJob != JobWhiteMage)
            {
                return;
            }

            if (pl.EntityStatus == EntityStatus.Healing)
            {
                return;
            }

            double score = settings.Priorities.WhmWeight;

            bool solace = pl.HasBuff(StatusEffect.Afflatus_Solace);
            bool misery = pl.HasBuff(StatusEffect.Afflatus_Misery);

            // Stances. The other stance being up suppresses the offer, so enabling
            // both keeps the current stance instead of alternating every recast.
            if (whm.UseAfflatusSolace && !solace && !misery
                && AbilityStateOf(pl, "Afflatus Solace", 0) == AbilityState.Ready)
            {
                candidates.Add(Ja("Afflatus Solace", score));
            }

            if (whm.UseAfflatusMisery && !misery && !solace
                && AbilityStateOf(pl, "Afflatus Misery", 0) == AbilityState.Ready)
            {
                candidates.Add(Ja("Afflatus Misery", score));
            }

            if (whm.UseDevotion)
            {
                ConsiderDevotion(snapshot, whm, score + 1, candidates);
            }
        }

        /// <summary>
        /// Devotion (legacy rules): self HPP &gt; 80, optional engagement gate
        /// (monitored character when attached, else self), target is the named
        /// member or any member at MP &lt;= threshold and MPP &lt;= 30 within 10 yalms.
        /// </summary>
        private void ConsiderDevotion(GameSnapshot snapshot, WhmSettings whm, double score, List<ActionCandidate> candidates)
        {
            PlayerState pl = snapshot.Player;

            if (pl.HPP <= DevotionMinSelfHpp
                || AbilityStateOf(pl, "Devotion", 0) != AbilityState.Ready)
            {
                return;
            }

            if (whm.DevotionOnlyWhenEngaged)
            {
                bool engaged = !string.IsNullOrEmpty(snapshot.MonitoredName)
                    ? snapshot.MonitoredStatus == EntityStatus.Engaged
                    : pl.EntityStatus == EntityStatus.Engaged;
                if (!engaged)
                {
                    return;
                }
            }

            string namedTarget = whm.DevotionTarget?.Trim() ?? string.Empty;

            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (!member.Active || member.CurrentHP == 0 || member.Zone != pl.ZoneId
                    || !snapshot.PartyMemberSelections.IsEnabled(member.Name)
                    || member.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (namedTarget.Length != 0
                    && !member.Name.Equals(namedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (member.CurrentMP > whm.DevotionMpBelow || member.CurrentMPP > DevotionMaxTargetMpp)
                {
                    continue;
                }

                if (_state.GetEntityDistance(member.TargetIndex) >= DevotionRange)
                {
                    continue;
                }

                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.JobAbility,
                    Score = score,
                    ActionName = "Devotion",
                    TargetName = member.Name,
                    IsJobAbility = true,
                    Description = "Devotion -> " + member.Name + " (whm JA, " + member.CurrentMPP + "% MP)",
                });

                return;
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
        /// JA availability, same contract as SchModule: buff 0 means the buffs are
        /// the offer conditions themselves, so this is known + recast plus the
        /// post-execution verify window.
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

        private static ActionCandidate Ja(string name, double score)
        {
            return new ActionCandidate
            {
                Category = ActionCategory.JobAbility,
                Score = score,
                ActionName = name,
                TargetName = "<me>",
                IsJobAbility = true,
                Description = name + " (whm JA)",
            };
        }
    }
}
