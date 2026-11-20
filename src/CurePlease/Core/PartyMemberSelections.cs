using System;
using System.Collections.Concurrent;

namespace CurePlease.Core
{
    /// <summary>
    /// Session-only per-character enable selections shown beside party rows.
    /// Names default to enabled, matching legacy Cure-Please.
    /// </summary>
    public sealed class PartyMemberSelections
    {
        private readonly ConcurrentDictionary<string, bool> _values =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public bool IsEnabled(string name)
        {
            return string.IsNullOrEmpty(name)
                || !_values.TryGetValue(name, out bool enabled)
                || enabled;
        }

        public void SetEnabled(string name, bool enabled)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _values[name.Trim()] = enabled;
            }
        }

        public void Clear()
        {
            _values.Clear();
        }
    }
}
