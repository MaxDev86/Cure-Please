using System;
using System.Collections.Generic;
using System.Linq;

namespace CurePlease.Core
{
    /// <summary>
    /// Session-only per-player buff assignments (main-window "Buffs" popup).
    /// Deliberately NOT part of the profile/settings JSON — legacy behaved the
    /// same (its autoXEnabled arrays were never persisted): parties change too
    /// often for saved assignments to mean anything.
    ///
    /// Keyed by character NAME, not party slot — roster reshuffles renumber
    /// slots mid-session, and a name-keyed assignment simply goes quiet while
    /// that character is absent and resumes when they rejoin. The PL is
    /// excluded by the UI: self-buffs belong to Buff Upkeep (one owner per
    /// target — two managers of the same buff would downgrade/timer-fight).
    /// </summary>
    public sealed class PlayerBuffAssignments
    {
        private readonly Dictionary<string, HashSet<string>> _spellsByName =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised after any change (the popup reflects edits live).</summary>
        public event Action Changed;

        /// <summary>Current spells assigned to a member (empty when none).</summary>
        public IReadOnlyCollection<string> GetSpells(string memberName)
        {
            return memberName != null && _spellsByName.TryGetValue(memberName, out HashSet<string> spells)
                ? spells.ToArray()
                : Array.Empty<string>();
        }

        /// <summary>Adds or removes one assigned spell.</summary>
        public void SetSpell(string memberName, string spell, bool assigned)
        {
            if (string.IsNullOrWhiteSpace(memberName) || string.IsNullOrWhiteSpace(spell))
            {
                return;
            }

            if (!_spellsByName.TryGetValue(memberName, out HashSet<string> spells))
            {
                if (!assigned)
                {
                    return;
                }

                spells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _spellsByName[memberName] = spells;
            }

            bool changed = assigned ? spells.Add(spell) : spells.Remove(spell);
            if (spells.Count == 0)
            {
                changed |= _spellsByName.Remove(memberName);
            }

            if (changed)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>Drops every assignment (fresh attach, party disbanded).</summary>
        public void Clear()
        {
            if (_spellsByName.Count > 0)
            {
                _spellsByName.Clear();
                Changed?.Invoke();
            }
        }

        /// <summary>Consistent point-in-time copy for the engine tick.</summary>
        public IReadOnlyList<KeyValuePair<string, string[]>> Snapshot()
        {
            return _spellsByName
                .Select(pair => new KeyValuePair<string, string[]>(pair.Key, pair.Value.ToArray()))
                .ToList();
        }
    }
}
