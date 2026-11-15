using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Geomancer engine (docs/geo-design-notes.md).
    ///
    /// Key mechanics implemented:
    ///  - Indicolure upkeep is presence-based: while an Indi-* aura is up the GEO
    ///    carries buff 612 (Colure Active), readable in the player's own buff list
    ///    every tick. The configured Indi spell is (re)cast on &lt;me&gt; whenever the
    ///    buff is missing — no duration estimates (Indi lasts until replaced or lost).
    ///  - Luopan placement is pet-based: a luopan is up when PetIndex != 0 and the pet
    ///    entity's HealthPercent >= 1 (a luopan IS the geomancer's pet). Geo-* spells
    ///    are re-cast when the luopan dies or is reclaimed, never on a timer.
    ///  - Blaze of Glory sequencing is verified like the bard prep JAs: the JA is
    ///    offered, then the Geo-* cast is held until buff 569 is actually visible
    ///    (10 s window, then it proceeds without). Legacy fired blind.
    ///  - Entrust (buff 584) makes the next Indicolure single-target; the entrusted
    ///    cast is offered while the buff is up and the buff is consumed by the cast,
    ///    so it fires exactly once per Entrust.
    ///  - Luopan management JAs: Life Cycle (pet 5-30%, self HP >= 90%), Ecliptic
    ///    Attrition (pet >= 90%, buff 516 absent), Dematerialize (pet >= 90%), Full
    ///    Circle (pet >= 10 yalms from the fight anchor — off-position), Radial Arcana
    ///    (MP valve, pet within 9 yalms).
    ///  - Engagement gate: a named entity (EngagedTargetName) when set, else the
    ///    monitored character's status, else the local player's.
    ///
    /// Failure handling matches the bard module: transient outcomes (not started /
    /// interrupted) retry in 3 s, genuine failures back off 15 s, and three
    /// consecutive failures session-disable the spell with a log line.
    /// </summary>
    public sealed class GeoModule : IDecisionModule
    {
        private const byte JobGeomancer = 21;

        /// <summary>After a failed cast, wait this long before offering it again.</summary>
        private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(15);

        /// <summary>Transient outcomes (rejected cast, interrupted) retry this soon — no failure strike.</summary>
        private static readonly TimeSpan TransientRetry = TimeSpan.FromSeconds(3);

        /// <summary>How long to wait for a JA buff to appear before proceeding without it.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        /// <summary>Consecutive cast failures after which a spell is given up on (session).</summary>
        private const int MaxConsecutiveFailures = 3;

        /// <summary>Luopan further than this from the fight anchor is reclaimed with Full Circle.</summary>
        private const float FullCircleDistance = 10f;

        /// <summary>Radial Arcana needs the luopan this close to the player.</summary>
        private const float RadialArcanaDistance = 9f;

        private enum AbilityState
        {
            /// <summary>Buff is on the PL right now.</summary>
            Active,

            /// <summary>Off recast and known — can be offered.</summary>
            Ready,

            /// <summary>Recently executed; waiting for the buff to appear.</summary>
            PendingVerify,

            /// <summary>On recast or not learned.</summary>
            Unavailable,
        }

        private readonly GameStateService _state;

        /// <summary>"spell|target" -> last attempt (any outcome), for backoff.</summary>
        private readonly Dictionary<string, DateTime> _lastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"spell|target" -> last transient failure (NotStarted/Interrupted): short retry, never strikes.</summary>
        private readonly Dictionary<string, DateTime> _shortRetry = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"spell|target" -> consecutive failure count.</summary>
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>JA name -> last execution time.</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Skip-reason dedup: reason key -> last time it was logged.</summary>
        private readonly Dictionary<string, DateTime> _skipLogged = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan SkipLogInterval = TimeSpan.FromSeconds(30);

        /// <summary>Optional diagnostic output (why a configured spell is NOT being cast). Wired by the UI.</summary>
        public event Action<string> Log;

        public GeoModule(GameStateService state)
        {
            _state = state;
        }

        private void LogSkip(string key, string message)
        {
            if (Log == null)
            {
                return;
            }

            if (_skipLogged.TryGetValue(key, out DateTime logged) && DateTime.UtcNow - logged < SkipLogInterval)
            {
                return;
            }

            _skipLogged[key] = DateTime.UtcNow;
            Log.Invoke(message);
        }

        public string Name => "Geomancer";

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            GeoSettings geo = settings.Geo;
            PlayerState pl = snapshot.Player;

            if (!geo.Enabled || (pl.MainJob != JobGeomancer && pl.SubJob != JobGeomancer))
            {
                return;
            }

            // Healing characters stand up on any cast — legacy gated GEO on this too.
            if (pl.EntityStatus == EntityStatus.Healing)
            {
                return;
            }

            double score = settings.Priorities.GeoWeight;
            bool luopanUp = snapshot.PetIndex != 0 && snapshot.PetHealthPercent >= 1;
            bool engaged = IsEngaged(snapshot, geo);

            // 1. Entrust pending: the next Indicolure lands single-target on the entrust
            //    target. Highest GEO priority — the buff is short-lived and consumed by the cast.
            if (pl.HasBuff(StatusEffect.Entrust) && !string.IsNullOrWhiteSpace(geo.EntrustSpell))
            {
                string target = !string.IsNullOrWhiteSpace(geo.EntrustTarget)
                    ? geo.EntrustTarget.Trim()
                    : !string.IsNullOrEmpty(snapshot.MonitoredName) ? snapshot.MonitoredName : "<me>";

                OfferSpell(geo.EntrustSpell.Trim(), target, score + 5, "entrusted indi", candidates);
                return;
            }

            // 2. Indi upkeep (presence-based, buff 612).
            if (!string.IsNullOrWhiteSpace(geo.IndiSpell) && !pl.HasBuff(StatusEffect.Colure_Active)
                && (!geo.IndiOnlyWhenEngaged || engaged))
            {
                OfferSpell(geo.IndiSpell.Trim(), "<me>", score, "indi upkeep", candidates);
            }

            if (!geo.LuopanEnabled || string.IsNullOrWhiteSpace(geo.GeoSpell))
            {
                return;
            }

            // 3. Luopan placement.
            if (!luopanUp && engaged)
            {
                if (geo.UseBlazeOfGlory)
                {
                    AbilityState bog = AbilityStateOf(pl, "Blaze of Glory", StatusEffect.Blaze_of_Glory);
                    if (bog == AbilityState.Ready)
                    {
                        candidates.Add(Ja("Blaze of Glory", score + 10));
                        return;
                    }

                    if (bog == AbilityState.PendingVerify)
                    {
                        return; // hold the Geo-* cast until buff 569 is visible
                    }
                }

                if (TryResolveLuopanTarget(snapshot, geo, out string targetName, out string reason))
                {
                    OfferSpell(geo.GeoSpell.Trim(), targetName, score, "luopan", candidates);
                }
                else
                {
                    LogSkip("target", "Geo: skipping \"" + geo.GeoSpell + "\" — " + reason);
                }

                return;
            }

            // 4. Luopan management while one is up.
            if (luopanUp)
            {
                AddLuopanManagement(snapshot, geo, pl, engaged, score, candidates);
            }
        }

        private void AddLuopanManagement(
            GameSnapshot snapshot,
            GeoSettings geo,
            PlayerState pl,
            bool engaged,
            double score,
            List<ActionCandidate> candidates)
        {
            byte petHp = snapshot.PetHealthPercent;

            if (geo.UseLifeCycle && petHp <= 30 && petHp >= 5 && pl.HPP >= 90
                && OfferJa(pl, "Life Cycle", score + 10, candidates))
            {
                return;
            }

            if (geo.UseRadialArcana && pl.MP <= geo.RadialArcanaMpBelow && snapshot.PetDistance <= RadialArcanaDistance
                && OfferJa(pl, "Radial Arcana", score + 10, candidates))
            {
                return;
            }

            if (engaged && geo.UseEclipticAttrition && petHp >= 90 && !pl.HasBuff(StatusEffect.Ecliptic_Attrition))
            {
                OfferJa(pl, "Ecliptic Attrition", score + 10, candidates);
            }

            if (engaged && geo.UseDematerialize && petHp >= 90)
            {
                OfferJa(pl, "Dematerialize", score + 10, candidates);
            }

            // Entrust is offered while the luopan is up so the entrusted Indi does not
            // compete with the drop sequence (Entrust's target gets a single-target
            // Indi; the self-aura upkeep above continues independently).
            if (engaged && geo.UseEntrust && !string.IsNullOrWhiteSpace(geo.EntrustSpell)
                && !pl.HasBuff(StatusEffect.Entrust))
            {
                OfferJa(pl, "Entrust", score + 10, candidates);
            }

            // Off-position luopan: reclaim with Full Circle so the placement logic
            // re-drops it where the fight is. Anchor is the engaged-target entity when
            // named, else the monitored character, else the local player.
            if (geo.UseFullCircle && _state.GetAbilityRecast("Full Circle") == 0 && _state.HasAbility("Full Circle"))
            {
                float distance = DistanceToAnchor(snapshot, geo);
                if (distance >= FullCircleDistance && distance != float.MaxValue)
                {
                    candidates.Add(Ja("Full Circle", score + 10));
                }
            }
        }

        /// <summary>Distance from the luopan to the fight anchor; float.MaxValue when unmeasurable.</summary>
        private float DistanceToAnchor(GameSnapshot snapshot, GeoSettings geo)
        {
            int anchorIndex = -1;

            if (!string.IsNullOrWhiteSpace(geo.EngagedTargetName))
            {
                EntityInfo entity = _state.FindEntityByName(geo.EngagedTargetName);
                if (entity != null)
                {
                    anchorIndex = entity.Index;
                }
            }

            if (anchorIndex < 0 && !string.IsNullOrEmpty(snapshot.MonitoredName))
            {
                EntityInfo monitored = _state.FindEntityByName(snapshot.MonitoredName);
                if (monitored != null)
                {
                    anchorIndex = monitored.Index;
                }
            }

            if (anchorIndex < 0)
            {
                // No anchor: pet-to-player distance is the only available measure.
                return snapshot.PetDistance;
            }

            return _state.GetDistanceBetweenEntities(snapshot.PetIndex, anchorIndex);
        }

        private bool IsEngaged(GameSnapshot snapshot, GeoSettings geo)
        {
            if (!string.IsNullOrWhiteSpace(geo.EngagedTargetName))
            {
                EntityInfo entity = _state.FindEntityByName(geo.EngagedTargetName);
                return entity != null && entity.Status == (uint)EntityStatus.Engaged;
            }

            if (!string.IsNullOrEmpty(snapshot.MonitoredName))
            {
                return snapshot.MonitoredStatus == EntityStatus.Engaged;
            }

            return snapshot.Player.EntityStatus == EntityStatus.Engaged;
        }

        /// <summary>
        /// Resolves the cast target for the configured Geo-* spell. Beneficial colures
        /// (not enemy-targetable) center on LuopanTarget / the monitored character /
        /// self; offensive ones use EngagedTargetName or the PL's current target.
        /// </summary>
        private bool TryResolveLuopanTarget(GameSnapshot snapshot, GeoSettings geo, out string targetName, out string reason)
        {
            targetName = null;
            reason = null;

            EliteAPI.ISpell spell = _state.FindSpell(geo.GeoSpell);
            bool enemyTargeted = spell != null && (spell.ValidTargets & 32) != 0;

            if (!enemyTargeted)
            {
                if (!string.IsNullOrWhiteSpace(geo.LuopanTarget))
                {
                    targetName = geo.LuopanTarget.Trim();
                }
                else if (!string.IsNullOrEmpty(snapshot.MonitoredName))
                {
                    targetName = snapshot.MonitoredName;
                }
                else
                {
                    targetName = "<me>";
                }

                return true;
            }

            if (!string.IsNullOrWhiteSpace(geo.EngagedTargetName))
            {
                targetName = geo.EngagedTargetName.Trim();
                return true;
            }

            if (snapshot.Player.EntityStatus == EntityStatus.Engaged && snapshot.TargetId != 0)
            {
                targetName = "<t>";
                return true;
            }

            reason = "offensive luopan needs EngagedTargetName or an engaged local player with a target";
            return false;
        }

        /// <summary>Offers a spell candidate when it is castable, offered at most per backoff rules.</summary>
        private void OfferSpell(string spellName, string targetName, double score, string what, List<ActionCandidate> candidates)
        {
            string key = spellName + "|" + targetName;
            if (OnBackoff(key) || IsDisabled(key))
            {
                return;
            }

            if (!IsCastable(spellName, out string reason))
            {
                LogSkip("cast|" + key, "Geo: skipping \"" + spellName + "\" (" + what + ") — " + reason);
                return;
            }

            candidates.Add(new ActionCandidate
            {
                Category = ActionCategory.Geo,
                Score = score,
                ActionName = spellName,
                TargetName = targetName,
                Description = spellName + " -> " + targetName + " (" + what + ")",
            });
        }

        private bool IsCastable(string spellName, out string reason)
        {
            EliteAPI.ISpell spell = _state.FindSpell(spellName);
            if (spell == null)
            {
                reason = "not in the resource database (check the spelling)";
                return false;
            }

            if (!_state.KnowsSpell(spell.Index) || !_state.MeetsSpellLevelRequirement(spell))
            {
                reason = "not learned at this job level";
                return false;
            }

            int recast = _state.GetSpellRecast(spell.Index);
            if (recast != 0)
            {
                reason = "on recast (" + recast + ")";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>Offers a JA candidate when it is ready. True when offered.</summary>
        private bool OfferJa(PlayerState pl, string abilityName, double score, List<ActionCandidate> candidates)
        {
            if (AbilityStateOf(pl, abilityName, 0) != AbilityState.Ready)
            {
                return false;
            }

            candidates.Add(Ja(abilityName, score));
            return true;
        }

        /// <summary>
        /// JA availability. Buff 0 means "no status buff to track" (Life Cycle, Full
        /// Circle, Radial Arcana leave no marker) — those are Ready purely on
        /// known + recast, with a post-execution cooldown via <see cref="_jaAttempted"/>
        /// so they are not re-offered every tick while their effect is invisible.
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
                Description = name + " (geo JA)",
            };
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility)
            {
                _jaAttempted[candidate.ActionName] = DateTime.UtcNow;
                return;
            }

            if (candidate.Category != ActionCategory.Geo)
            {
                return;
            }

            string key = candidate.ActionName + "|" + candidate.TargetName;

            if (!result.Succeeded)
            {
                if (result.Outcome == CastOutcome.NotStarted || result.Outcome == CastOutcome.Interrupted)
                {
                    // Transient: a cast was still in progress or movement interrupted —
                    // retry soon instead of striking toward session-disable.
                    _lastAttempt.Remove(key);
                    _shortRetry[key] = DateTime.UtcNow;
                    return;
                }

                _lastAttempt[key] = DateTime.UtcNow;
                _failures[key] = (_failures.TryGetValue(key, out int f) ? f : 0) + 1;

                if (_failures[key] >= MaxConsecutiveFailures)
                {
                    LogSkip("disabled|" + key, "Geo: \"" + candidate.ActionName + "\" failed "
                        + MaxConsecutiveFailures + " times in a row — disabled for this session");
                }

                return;
            }

            _lastAttempt[key] = DateTime.UtcNow;
            _failures.Remove(key);
        }

        private bool OnBackoff(string key)
        {
            if (_lastAttempt.TryGetValue(key, out DateTime attempted)
                && DateTime.UtcNow - attempted < RetryBackoff)
            {
                return true;
            }

            return _shortRetry.TryGetValue(key, out DateTime retry)
                && DateTime.UtcNow - retry < TransientRetry;
        }

        private bool IsDisabled(string key)
        {
            return _failures.TryGetValue(key, out int f) && f >= MaxConsecutiveFailures;
        }
    }
}
