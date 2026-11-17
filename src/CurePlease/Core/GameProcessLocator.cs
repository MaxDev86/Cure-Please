using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CurePlease.Core
{
    /// <summary>A running FFXI game process the user can attach to.</summary>
    public sealed class GameProcessInfo
    {
        public int Pid { get; set; }
        public string CharacterName { get; set; }

        /// <summary>"Charname (40956)"; falls back to the bare PID before login.</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(CharacterName) ? "(" + Pid + ")" : CharacterName + " (" + Pid + ")";

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Enumerates running FFXI processes (same launcher set as legacy Cure-Please).
    /// The game window title carries the character name once logged in, which is how
    /// legacy showed names in its PID combos without attaching anything.
    /// </summary>
    public static class GameProcessLocator
    {
        private static readonly string[] LauncherNames = { "pol", "xiloader", "edenxi" };

        public static List<GameProcessInfo> FindGameProcesses()
        {
            var list = new List<GameProcessInfo>();
            foreach (string name in LauncherNames)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        list.Add(new GameProcessInfo { Pid = process.Id, CharacterName = process.MainWindowTitle });
                    }
                    catch
                    {
                        // Process exited between enumeration and the title read.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return list.OrderBy(p => p.CharacterName).ThenBy(p => p.Pid).ToList();
        }
    }
}
