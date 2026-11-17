using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurePlease.Settings
{
    /// <summary>
    /// JSON persistence for <see cref="CurePleaseSettings"/> with versioning and
    /// atomic publication. The engine calls <see cref="Current"/> once per tick and
    /// gets a consistent snapshot; the UI edits a copy and calls <see cref="Save"/>,
    /// which swaps the live object in one reference assignment. This replaces the
    /// legacy static mutable Form2.config read from ~1900 sites.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        private readonly object _gate = new object();
        private volatile CurePleaseSettings _current = new CurePleaseSettings();

        /// <summary>Raised after a new settings object becomes live.</summary>
        public event Action<CurePleaseSettings> SettingsChanged;

        /// <summary>The live settings. Safe to read from any thread; never mutated in place.</summary>
        public CurePleaseSettings Current => _current;

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CurePlease");

        public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

        /// <summary>Serializes a deep copy — used so the UI can edit without touching live settings.</summary>
        public static CurePleaseSettings Clone(CurePleaseSettings source)
        {
            return JsonSerializer.Deserialize<CurePleaseSettings>(
                JsonSerializer.Serialize(source, JsonOptions), JsonOptions);
        }

        /// <summary>Canonical JSON with the same options as file writes — used by the settings window's dirty check.</summary>
        public static string SerializeForComparison(CurePleaseSettings settings)
        {
            return JsonSerializer.Serialize(settings, JsonOptions);
        }

        /// <summary>
        /// Loads settings from disk and publishes them. Never throws: a missing file
        /// keeps the current settings, and an unreadable/corrupt file is logged and
        /// also keeps the current settings (publishing half-valid state is worse than
        /// keeping known-good).
        /// </summary>
        public CurePleaseSettings Load(string path)
        {
            if (!File.Exists(path))
            {
                return Current;
            }

            try
            {
                string json = File.ReadAllText(path);
                CurePleaseSettings loaded = JsonSerializer.Deserialize<CurePleaseSettings>(json, JsonOptions)
                                            ?? new CurePleaseSettings();

                Migrate(loaded);
                Publish(loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Settings load failed for {Path}; keeping current settings", path);
                return Current;
            }
        }

        /// <summary>
        /// Persists settings atomically (write-then-rename) and publishes them. False
        /// when the write failed — logged; the in-memory publish is skipped too, so a
        /// failed save never makes unsaved-on-disk settings look live.
        /// </summary>
        public bool Save(CurePleaseSettings settings, string path)
        {
            settings.Version = CurePleaseSettings.CurrentVersion;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string json = JsonSerializer.Serialize(settings, JsonOptions);

                // Write-then-rename so a crash mid-write cannot corrupt the live file.
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Settings save failed for {Path}", path);
                return false;
            }

            Publish(settings);
            return true;
        }

        private void Publish(CurePleaseSettings settings)
        {
            lock (_gate)
            {
                _current = settings;
            }

            SettingsChanged?.Invoke(settings);
        }

        /// <summary>Makes an externally-built settings object (e.g. a legacy import) live.</summary>
        public void Adopt(CurePleaseSettings settings)
        {
            Migrate(settings);
            Publish(settings);
        }

        /// <summary>Imports a legacy CurePlease 2.x XML file and makes it live. Returns the import report.</summary>
        public LegacySettingsImporter.ImportResult ImportLegacy(string path)
        {
            LegacySettingsImporter.ImportResult result = LegacySettingsImporter.Import(path);
            Adopt(result.Settings);
            return result;
        }

        /// <summary>
        /// Migration pipeline. Unknown fields are ignored by the deserializer and new
        /// fields keep their defaults, so migrations only run when the shape changes
        /// in ways defaults cannot cover.
        /// </summary>
        private static void Migrate(CurePleaseSettings settings)
        {
            if (settings.Version < 1)
            {
                settings.Version = 1;
            }

            // JSON load replaces Entries wholesale; re-add catalog spells introduced
            // after the profile was written.
            settings.Buffs.EnsureSeeded();

            // Future: if (settings.Version < 2) { ... settings.Version = 2; }
        }
    }
}
