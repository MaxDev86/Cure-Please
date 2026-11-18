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
    /// optional character prefix (<c>Charname_BRD_NIN.json</c>) for per-character
    /// overrides — the same scheme legacy used for its XML files.
    ///
    /// The UI reads the player's job every snapshot, so a /job change or a login on
    /// another character can hot-swap the profile: character-specific file first,
    /// then the job-combo file. This is controlled by <see cref="AutoSwitchEnabled"/>;
    /// when off, the current profile stays active and a matching candidate is only
    /// reported through <see cref="SwitchSuppressed"/>. When no profile matches, the
    /// current settings stay active. <c>default.json</c> loads at startup.
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

        /// <summary>Job id (1-22) -> full English job name.</summary>
        private static readonly string[] JobFullNames =
        {
            string.Empty,
            "Warrior", "Monk", "White Mage", "Black Mage", "Red Mage", "Thief",
            "Paladin", "Dark Knight", "Beastmaster", "Bard", "Ranger", "Samurai",
            "Ninja", "Dragoon", "Summoner", "Blue Mage", "Corsair", "Puppetmaster",
            "Dancer", "Scholar", "Geomancer", "Rune Fencer",
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

        /// <summary>
        /// When false, <see cref="ConsiderSnapshot"/> never switches profiles — a
        /// manually picked profile stays active across job/character changes and
        /// <see cref="SwitchSuppressed"/> is raised instead. Mirrors the global
        /// AutoSwitchProfiles preference (default true = legacy behavior).
        /// </summary>
        public bool AutoSwitchEnabled { get; set; } = true;

        /// <summary>
        /// Raised with the profile name that WOULD have been activated when
        /// auto-switch is off and the job context changes to one with a different
        /// matching profile. For the UI to log so a stale profile is never silent.
        /// </summary>
        public event Action<string> SwitchSuppressed;

        public static string JobAbbreviation(byte jobId)
        {
            return jobId < JobAbbreviations.Length && JobAbbreviations[jobId].Length > 0
                ? JobAbbreviations[jobId]
                : null;
        }

        /// <summary>Full English job name for a job id ("White Mage"); null when unknown.</summary>
        /// <param name="jobId">Job id (1-22).</param>
        /// <returns>The full job name, or null.</returns>
        public static string JobFullName(byte jobId)
        {
            return jobId < JobFullNames.Length && JobFullNames[jobId].Length > 0
                ? JobFullNames[jobId]
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

        /// <summary>Character-specific profile name ("Charname_BRD_NIN"); falls back to the combo name.</summary>
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

        /// <summary>
        /// Recovers the job combo encoded in a profile name — the trailing JOB or
        /// JOB_JOB tokens ("Charname_WHM_SCH" -> WHM/SCH). FFXI character names are
        /// letters-only and job abbreviations are compared case-sensitively, so a
        /// leading non-abbreviation token is a character prefix. False when no combo
        /// can be read (e.g. "default" or a hand-named profile) — callers must fail
        /// open (show everything) in that case.
        /// </summary>
        public static bool TryComboFromProfileName(string profileName, out byte mainJob, out byte subJob)
        {
            mainJob = 0;
            subJob = 0;
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return false;
            }

            string[] tokens = profileName.Trim().Split('_');
            byte last = JobIdFromAbbreviation(tokens[tokens.Length - 1]);
            if (last == 0)
            {
                return false;
            }

            if (tokens.Length >= 2)
            {
                byte previous = JobIdFromAbbreviation(tokens[tokens.Length - 2]);
                if (previous != 0)
                {
                    mainJob = previous;
                    subJob = last;
                    return true;
                }
            }

            mainJob = last;
            return true;
        }

        /// <summary>Inverse of <see cref="JobAbbreviation"/>; 0 when the token is not a job abbreviation.</summary>
        private static byte JobIdFromAbbreviation(string token)
        {
            for (byte i = 1; i < JobAbbreviations.Length; i++)
            {
                if (JobAbbreviations[i].Equals(token, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        public IReadOnlyList<string> ListProfiles()
        {
            try
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
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Profile directory {Directory} is not readable", _directory);
                return Array.Empty<string>();
            }
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

            string candidate = FindProfileFor(player);
            if (candidate == null)
            {
                return false;
            }

            if (!AutoSwitchEnabled)
            {
                if (!candidate.Equals(ActiveName, StringComparison.OrdinalIgnoreCase))
                {
                    SwitchSuppressed?.Invoke(candidate);
                }

                return false;
            }

            SwitchTo(candidate);
            return true;
        }

        /// <summary>Character-specific file first, then the job-combo file; null when neither exists.</summary>
        private string FindProfileFor(PlayerState player)
        {
            string characterSpecific = ProfileNameFor(player.Name, player.MainJob, player.SubJob);
            if (characterSpecific != null && !characterSpecific.Equals(ComboNameFor(player.MainJob, player.SubJob), StringComparison.OrdinalIgnoreCase)
                && File.Exists(PathFor(characterSpecific)))
            {
                return characterSpecific;
            }

            string combo = ComboNameFor(player.MainJob, player.SubJob);
            return combo != null && File.Exists(PathFor(combo)) ? combo : null;
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

        /// <summary>Saves the live settings under a profile name and makes it active. False when the write failed.</summary>
        public bool SaveAs(string name)
        {
            if (!_settings.Save(_settings.Current, PathFor(name)))
            {
                return false;
            }

            ActiveName = name;
            return true;
        }

        /// <summary>Saves the live settings back to the active profile; false when there is none or the write failed.</summary>
        public bool SaveActive()
        {
            if (ActiveName == null)
            {
                return false;
            }

            return _settings.Save(_settings.Current, PathFor(ActiveName));
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

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Profile delete failed for {Path}", path);
                return false;
            }

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
