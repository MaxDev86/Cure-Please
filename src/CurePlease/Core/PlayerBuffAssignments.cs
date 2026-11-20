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
        private readonly object _gate = new object();

        /// <summary>Raised after any change (the popup reflects edits live).</summary>
        public event Action Changed;

        /// <summary>Current spells assigned to a member (empty when none).</summary>
        public IReadOnlyCollection<string> GetSpells(string memberName)
        {
            lock (_gate)
            {
                return memberName != null && _spellsByName.TryGetValue(memberName, out HashSet<string> spells)
                    ? spells.ToArray()
                    : Array.Empty<string>();
            }
        }

        /// <summary>Adds or removes one assigned spell; a new tier replaces others in its catalog group.</summary>
        public void SetSpell(string memberName, string spell, bool assigned)
        {
            if (string.IsNullOrWhiteSpace(memberName) || string.IsNullOrWhiteSpace(spell))
            {
                return;
            }

            bool changed = false;
            lock (_gate)
            {
                if (!_spellsByName.TryGetValue(memberName, out HashSet<string> spells))
                {
                    if (!assigned)
                    {
                        return;
                    }

                    spells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _spellsByName[memberName] = spells;
                }

                if (assigned)
                {
                    BuffCatalogEntry selected = BuffCatalog.Find(spell);
                    if (selected != null)
                    {
                        changed = spells.RemoveWhere(existing =>
                        {
                            BuffCatalogEntry entry = BuffCatalog.Find(existing);
                            return entry != null && entry.Group == selected.Group
                                && !entry.Spell.Equals(selected.Spell, StringComparison.OrdinalIgnoreCase);
                        }) > 0;
                    }

                    changed |= spells.Add(spell);
                }
                else
                {
                    changed = spells.Remove(spell);
                }

                if (spells.Count == 0)
                {
                    changed |= _spellsByName.Remove(memberName);
                }
            }

            if (changed)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>Drops every assignment (fresh attach, party disbanded).</summary>
        public void Clear()
        {
            bool changed;
            lock (_gate)
            {
                changed = _spellsByName.Count > 0;
                _spellsByName.Clear();
            }

            if (changed)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>Consistent point-in-time copy for the engine tick.</summary>
        public IReadOnlyList<KeyValuePair<string, string[]>> Snapshot()
        {
            lock (_gate)
            {
                return _spellsByName
                    .Select(pair => new KeyValuePair<string, string[]>(pair.Key, pair.Value.ToArray()))
                    .ToList();
            }
        }
    }
}
