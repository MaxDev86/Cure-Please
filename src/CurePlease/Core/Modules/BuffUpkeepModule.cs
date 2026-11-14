using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Party buff upkeep (Haste / Protect / Shell for now).
    ///
    /// Fixes from the legacy assessment baked in:
    ///  - timestamps are recorded on CONFIRMED cast success (OnActionExecuted),
    ///    never at cast initiation — interrupted/failed casts retry quickly
    ///    instead of waiting out the whole refresh window;
    ///  - all duration math uses TotalMinutes (legacy used TimeSpan.Minutes,
    ///    which breaks past one hour);
    ///  - when buff presence is known (PL from memory, party via the addon),
    ///    a missing buff is recast immediately — dispel detection for free.
    ///  - when presence is unknown (no addon data for that member), falls back
    ///    to the configured refresh interval.
    /// </summary>
    public sealed class BuffUpkeepModule : IDecisionModule
    {
        private sealed class TrackedBuff
        {
            public string Key;          // logical key, e.g. "Haste"
            public string Spell;        // configured spell name
            public StatusEffect Effect; // buff id to detect
            public double Minutes;      // fallback refresh interval
            public bool PartyWide;      // true: everyone; false: HasteTargets only
        }

        private const float BuffRange = 21f;

        /// <summary>After a failed cast attempt, wait this long before offering it again.</summary>
        private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(15);

        private readonly GameStateService _state;

        /// <summary>"key|name" -> last confirmed landing (UTC).</summary>
        private readonly Dictionary<string, DateTime> _lastLanded = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"key|name" -> last attempt (UTC), any outcome.</summary>
        private readonly Dictionary<string, DateTime> _lastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public BuffUpkeepModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Buffs";

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            PlayerState pl = snapshot.Player;

            TrackedBuff[] tracked =
            {
                new TrackedBuff { Key = "Haste", Spell = settings.Buffs.HasteSpell, Effect = StatusEffect.Haste,
                                  Minutes = settings.Buffs.AutoHasteMinutes, PartyWide = false },
                new TrackedBuff { Key = "Protect", Spell = settings.Buffs.ProtectSpell, Effect = StatusEffect.Protect,
                                  Minutes = settings.Buffs.AutoProtectMinutes, PartyWide = true },
                new TrackedBuff { Key = "Shell", Spell = settings.Buffs.ShellSpell, Effect = StatusEffect.Shell,
                                  Minutes = settings.Buffs.AutoShellMinutes, PartyWide = true },
            };

            bool[] enabled = { settings.Buffs.AutoHaste, settings.Buffs.AutoProtect, settings.Buffs.AutoShell };

            for (int t = 0; t < tracked.Length; t++)
            {
                if (!enabled[t])
                {
                    continue;
                }

                TrackedBuff buff = tracked[t];
                EliteAPI.ISpell spell = SpellBook.IsCastable(_state, buff.Spell, pl.MP);
                if (spell == null)
                {
                    continue;
                }

                bool selfCovered = false;

                foreach (PartyMemberState member in snapshot.PartyMembers)
                {
                    if (!member.Active || member.CurrentHP == 0 || member.Zone != pl.ZoneId)
                    {
                        continue;
                    }

                    bool isSelf = member.Name.Equals(pl.Name, StringComparison.OrdinalIgnoreCase);
                    if (isSelf)
                    {
                        selfCovered = true;
                    }

                    if (!buff.PartyWide && !ContainsName(settings.Buffs.HasteTargets, member.Name))
                    {
                        continue;
                    }

                    if (!isSelf && _state.GetEntityDistance(member.TargetIndex) > BuffRange)
                    {
                        continue;
                    }

                    ConsiderTarget(snapshot, settings, candidates, buff, spell, member.Name);
                }

                // PL is always a valid buff target, even when out of party.
                if (!selfCovered && pl.HP > 0 && (buff.PartyWide || ContainsName(settings.Buffs.HasteTargets, pl.Name)))
                {
                    ConsiderTarget(snapshot, settings, candidates, buff, spell, pl.Name);
                }
            }
        }

        private void ConsiderTarget(
            GameSnapshot snapshot,
            CurePleaseSettings settings,
            List<ActionCandidate> candidates,
            TrackedBuff buff,
            EliteAPI.ISpell spell,
            string targetName)
        {
            string stateKey = buff.Key + "|" + targetName;

            // A landing also updates _lastAttempt, so this one check covers both
            // "just landed, wait for the buff to appear" and "recent failure, back off".
            if (_lastAttempt.TryGetValue(stateKey, out DateTime attempted)
                && DateTime.UtcNow - attempted < RetryBackoff)
            {
                return;
            }

            short[] buffs = BuffsFor(snapshot, targetName);
            if (buffs != null)
            {
                // Presence known: recast only when actually missing (dispel/wear).
                if (HasBuff(buffs, buff.Effect))
                {
                    return;
                }
            }
            else
            {
                // Presence unknown: timer fallback (TotalMinutes — legacy used .Minutes).
                if (_lastLanded.TryGetValue(stateKey, out DateTime last)
                    && (DateTime.UtcNow - last).TotalMinutes < buff.Minutes)
                {
                    return;
                }
            }

            candidates.Add(new ActionCandidate
            {
                Category = ActionCategory.BuffUpkeep,
                Score = settings.Priorities.BuffWeight,
                ActionName = spell.Name[0],
                TargetName = targetName,
                Description = buff.Spell + " -> " + targetName,
            });
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.Category != ActionCategory.BuffUpkeep)
            {
                return;
            }

            // Identify which tracked buff this was by matching spell name to effect.
            string key = KeyForSpell(candidate.ActionName);
            if (key == null)
            {
                return;
            }

            string stateKey = key + "|" + candidate.TargetName;
            _lastAttempt[stateKey] = DateTime.UtcNow;

            if (result.Succeeded)
            {
                _lastLanded[stateKey] = DateTime.UtcNow;
            }
        }

        private static string KeyForSpell(string spellName)
        {
            if (spellName.StartsWith("Haste", StringComparison.OrdinalIgnoreCase)) return "Haste";
            if (spellName.StartsWith("Protect", StringComparison.OrdinalIgnoreCase)) return "Protect";
            if (spellName.StartsWith("Shell", StringComparison.OrdinalIgnoreCase)) return "Shell";
            return null;
        }

        private static short[] BuffsFor(GameSnapshot snapshot, string name)
        {
            if (name.Equals(snapshot.Player.Name, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot.Player.Buffs;
            }

            if (!string.IsNullOrEmpty(snapshot.MonitoredName)
                && name.Equals(snapshot.MonitoredName, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot.MonitoredBuffs;
            }

            return snapshot.PartyBuffs.TryGetValue(name, out short[] buffs) ? buffs : null;
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
    }
}
