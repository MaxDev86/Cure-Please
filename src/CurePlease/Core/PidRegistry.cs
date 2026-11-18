using System.Collections.Generic;

namespace CurePlease.Core
{
    /// <summary>
    /// Tracks which game process ids are claimed as a tab's PL attachment. Two
    /// engines driving one character would fight over casts and movement, so a
    /// PL pid may be claimed once across the whole window. Monitored pids are
    /// intentionally NOT tracked — several tabs may legitimately watch the
    /// same tank.
    /// </summary>
    public sealed class PidRegistry
    {
        private readonly HashSet<int> _claimed = new HashSet<int>();

        /// <summary>Claims a process id. False when another tab already holds it.</summary>
        public bool TryClaim(int pid)
        {
            lock (_claimed)
            {
                return _claimed.Add(pid);
            }
        }

        public void Release(int pid)
        {
            lock (_claimed)
            {
                _claimed.Remove(pid);
            }
        }
    }
}
