using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CurePlease.Core;

namespace CurePlease.Settings
{
    /// <summary>
    /// Job-combo profiles. A profile is one complete <see cref="CurePleaseSettings"/>
    /// stored as JSON in <c>&lt;exe&gt;\profiles\</c>, named legacy-style:
    /// <c>WHM_SCH.json</c>, <c>BRD_NIN.json</c>, <c>WHM.json</c> (no sub job), with an
    /// optional character prefix (<c>Alternate_BRD_NIN.json</c>) for per-character
    /// overrides — the same scheme legacy used for its XML files.
    ///
    /// The engine reads the player's job every tick, so a /job change or a login on
    /// another character hot-swaps the profile: character-specific file first, then
    /// the job-combo file; when neither exists the current settings stay active.
    /// <c>default.json</c> is the fallback profile loaded at startup.
    /// </summary>
    public sealed class ProfileService
    {
        /// <summary>Job id (1-22) -> three-letter abbreviation, matching legacy JobNames.</summary>
        private static readonly string[] JobAbbreviations =
        {
            string.Empty,
            "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK", "BST", "BRD",
            "RNG", "SAM", "NIN", "DRG", "SMN", "BLU", "COR", "PUP", "DNC", "SCH",
            "GEO", "RUN",
        };

        public const string DefaultProfileName = "default";

        private readonly SettingsService _settings;
        private readonly string _directory;

        /// <summary>"character|mainJob|subJob" of the last considered snapshot — switches fire once per context change.</summary>
        private string _lastContextKey = string.Empty;

        public ProfileService(SettingsService settings, string directory = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _directory = directory ?? DefaultDirectory;
        }

        public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "profiles");

        public string DirectoryPath => _directory;

        /// <summary>Name of the active profile, or null when settings did not come from a profile file.</summary>
        public string ActiveName { get; private set; }

        public string ActivePath => ActiveName == null ? null : PathFor(ActiveName);

        /// <summary>Raised with the profile name after a switch.</summary>
        public event Action<string> ProfileSwitched;

        public static string JobAbbreviation(byte jobId)
        {
            return jobId < JobAbbreviations.Length && JobAbbreviations[jobId].Length > 0
                ? JobAbbreviations[jobId]
                : null;
        }

        /// <summary>"BRD_NIN" combo name; "WHM" when there is no sub job; null when the main job is unknown.</summary>
        public static string ComboNameFor(byte mainJob, byte subJob)
        {
            string main = JobAbbreviation(mainJob);
            if (main == null)
            {
                return null;
            }

            string sub = JobAbbreviation(subJob);
            return sub == null ? main : main + "_" + sub;
        }

        /// <summary>Character-specific profile name ("Alternate_BRD_NIN"); falls back to the combo name.</summary>
        public static string ProfileNameFor(string characterName, byte mainJob, byte subJob)
        {
            string combo = ComboNameFor(mainJob, subJob);
            if (combo == null)
            {
                return null;
            }

            string character = Sanitize(characterName);
            return character.Length == 0 ? combo : character + "_" + combo;
        }

        public IReadOnlyList<string> ListProfiles()
        {
            if (!Directory.Exists(_directory))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Considers a job-context switch. Called with each snapshot; switches at most
        /// once per context change and only when a matching profile file exists
        /// (character-specific first, then the job combo). True when a switch happened.
        /// </summary>
        public bool ConsiderSnapshot(GameSnapshot snapshot)
        {
            PlayerState player = snapshot?.Player;
            if (player == null || player.MainJob == 0)
            {
                return false;
            }

            string contextKey = (player.Name ?? string.Empty) + "|" + player.MainJob + "|" + player.SubJob;
            if (contextKey == _lastContextKey)
            {
                return false;
            }

            _lastContextKey = contextKey;

            string characterSpecific = ProfileNameFor(player.Name, player.MainJob, player.SubJob);
            if (characterSpecific != null && !characterSpecific.Equals(ComboNameFor(player.MainJob, player.SubJob), StringComparison.OrdinalIgnoreCase)
                && File.Exists(PathFor(characterSpecific)))
            {
                SwitchTo(characterSpecific);
                return true;
            }

            string combo = ComboNameFor(player.MainJob, player.SubJob);
            if (combo != null && File.Exists(PathFor(combo)))
            {
                SwitchTo(combo);
                return true;
            }

            return false;
        }

        /// <summary>Loads a profile by name and makes it the active one. False when the file does not exist.
        /// A manual switch sticks until the job context actually changes — ConsiderSnapshot
        /// will not immediately override it for the current context.</summary>
        public bool SwitchTo(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                return false;
            }

            _settings.Load(path);
            ActiveName = name;
            ProfileSwitched?.Invoke(name);
            return true;
        }

        /// <summary>Saves the live settings under a profile name and makes it active.</summary>
        public void SaveAs(string name)
        {
            _settings.Save(_settings.Current, PathFor(name));
            ActiveName = name;
        }

        /// <summary>Saves the live settings back to the active profile; false when there is none.</summary>
        public bool SaveActive()
        {
            if (ActiveName == null)
            {
                return false;
            }

            _settings.Save(_settings.Current, PathFor(ActiveName));
            return true;
        }

        public void Duplicate(string sourceName, string newName)
        {
            string sourcePath = PathFor(sourceName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("No such profile: " + sourceName, sourcePath);
            }

            Directory.CreateDirectory(_directory);
            File.Copy(sourcePath, PathFor(newName), false);
        }

        public bool Delete(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            if (ActiveName != null && ActiveName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                ActiveName = null;
            }

            return true;
        }

        private string PathFor(string name)
        {
            return Path.Combine(_directory, Sanitize(name) + ".json");
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }
}
