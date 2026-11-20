using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Per-player session buffs (main-window "Buffs" popup; the assignments are
    /// runtime-only, see <see cref="PlayerBuffAssignments"/>). Casts the assigned
    /// party-targetable catalog buffs on any active same-zone party OR alliance
    /// member — single-target spells can aim cross-party.
    ///
    /// Presence detection degrades by data source:
    ///  - monitored character: buffs read from the second attachment;
    ///  - own-party members: addon buff reports (name-keyed);
    ///  - anyone else (typically alliance): unobservable — timer-based recast at
    ///    the catalog's stock duration minus a margin, same as BRD enemy songs.
    /// Failed casts back off briefly instead of spamming.
    /// </summary>
    public sealed class PlayerBuffModule : IDecisionModule
    {
        private const float BuffRange = 21f;

        /// <summary>After a cast attempt, wait this long before offering it again.</summary>
        private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(15);

        /// <summary>Timer-mode refresh margin: recast this long before the estimated expiry.</summary>
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

        private readonly GameStateService _state;
        private readonly PlayerBuffAssignments _assignments;

        /// <summary>"name|spell" -> last confirmed landing (timer mode + family freshness).</summary>
        private readonly Dictionary<string, DateTime> _landed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"name|spell" -> last attempt, any outcome.</summary>
        private readonly Dictionary<string, DateTime> _attempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public PlayerBuffModule(GameStateService state, PlayerBuffAssignments assignments)
        {
            _state = state;
            _assignments = assignments;
        }

        public string Name => "PlayerBuffs";

        /// <summary>
        /// Zoning strips every buff — drop landing/attempt tracking. The assignments
        /// themselves survive: the party usually persists across a zone.
        /// </summary>
        public void OnZoneChanged()
        {
            _landed.Clear();
            _attempted.Clear();
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            PlayerState pl = snapshot.Player;
            if (pl.HP == 0)
            {
                return;
            }

            string monitored = snapshot.MonitoredName ?? string.Empty;

            foreach (KeyValuePair<string, string[]> assignment in _assignments.Snapshot())
            {
                string memberName = assignment.Key;
                if (memberName.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the PL is Buff Upkeep's target — one owner per buff
                }

                if (!snapshot.PartyMemberSelections.IsEnabled(memberName))
                {
                    continue;
                }

                PartyMemberState member = FindMember(snapshot, memberName, pl.ZoneId);
                if (member == null)
                {
                    continue; // not in the party/alliance right now — assignment stays for later
                }

                short[] buffs = BuffsOf(snapshot, memberName, monitored);
                bool observable = buffs != null;

                foreach (string spellName in assignment.Value)
                {
                    BuffCatalogEntry catalog = BuffCatalog.Find(spellName);
                    if (catalog == null || !catalog.PartyTargetable)
                    {
                        continue;
                    }

                    string key = memberName + "|" + catalog.Spell;

                    if (observable)
                    {
                        if (HasBuff(buffs, catalog.BuffId))
                        {
                            _landed[key] = DateTime.UtcNow; // observed up — keep the family timer fresh
                            continue;
                        }

                        // Family exclusivity on the target: another bar/status or
                        // storm is up — casting would silently overwrite it.
                        if (catalog.Family != BuffFamily.None && HasFamilyConflict(buffs, catalog))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // Unobservable target: recast on the stock-duration timer.
                        if (_landed.TryGetValue(key, out DateTime landed)
                            && DateTime.UtcNow - landed < TimeSpan.FromMinutes(catalog.DefaultMinutes) - RefreshMargin)
                        {
                            continue;
                        }
                    }

                    if (_attempted.TryGetValue(key, out DateTime attempted)
                        && DateTime.UtcNow - attempted < RetryBackoff)
                    {
                        continue;
                    }

                    EliteAPI.ISpell spell = SpellBook.IsCastable(_state, catalog.Spell, pl.MP);
                    if (spell == null)
                    {
                        continue;
                    }

                    if (_state.GetEntityDistance(member.TargetIndex) > BuffRange)
                    {
                        continue;
                    }

                    candidates.Add(new ActionCandidate
                    {
                        Category = ActionCategory.BuffUpkeep,
                        Score = settings.Priorities.BuffWeight + 2,
                        ActionName = spell.Name[0],
                        TargetName = member.Name,
                        Description = catalog.Spell + " -> " + member.Name + (observable ? "" : " (timer)"),
                    });
                }
            }
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility || candidate.Category != ActionCategory.BuffUpkeep
                || candidate.TargetName.Length == 0)
            {
                return;
            }

            BuffCatalogEntry catalog = BuffCatalog.Find(candidate.ActionName);
            if (catalog == null || !catalog.PartyTargetable)
            {
                return; // a Buff Upkeep (self) cast — not ours
            }

            string key = candidate.TargetName + "|" + catalog.Spell;
            _attempted[key] = DateTime.UtcNow;
            if (result.Succeeded)
            {
                _landed[key] = DateTime.UtcNow;
            }
        }

        private static PartyMemberState FindMember(GameSnapshot snapshot, string name, int zone)
        {
            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (member.Active && member.Zone == zone
                    && member.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }
            }

            return null;
        }

        /// <summary>The member's current buffs when readable (monitored attachment or addon), else null.</summary>
        private static short[] BuffsOf(GameSnapshot snapshot, string memberName, string monitored)
        {
            if (monitored.Length != 0 && memberName.Equals(monitored, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot.MonitoredBuffs;
            }

            return snapshot.PartyBuffs.TryGetValue(memberName, out short[] buffs) ? buffs : null;
        }

        private static bool HasBuff(short[] buffs, short effect)
        {
            foreach (short buff in buffs)
            {
                if (buff == effect)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasFamilyConflict(short[] buffs, BuffCatalogEntry catalog)
        {
            HashSet<short> familyIds = BuffCatalog.BuffIdsOfFamily(catalog.Family);
            foreach (short id in buffs)
            {
                if (id != catalog.BuffId && familyIds.Contains(id))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
