using System;
using System.IO;
using System.Text.Json;

namespace CurePlease.Settings
{
    /// <summary>
    /// App-level preferences that are NOT job behavior — they live in one global
    /// file next to the exe (<c>global_settings.json</c>), not inside the
    /// per-combo profile files. Everything here must default to the
    /// least-surprising value (nothing hidden, nothing filtered).
    /// </summary>
    public sealed class GlobalSettings
    {
        public int Version { get; set; } = 1;

        /// <summary>
        /// Settings window: trim the job-page nav to the active profile's
        /// main/sub combo. OFF by default — every page stays visible unless the
        /// user explicitly asks for the filter (a view preference only; the
        /// settings model keeps every section regardless).
        /// </summary>
        public bool FilterJobPagesByCombo { get; set; }

        /// <summary>
        /// Auto-switch profiles when the character/job context changes (legacy
        /// behavior, default ON). When OFF, a manually picked profile is fully
        /// sticky — important for users with several variants of the same combo
        /// profile (e.g. different bar- spells or cure priorities per WHM_SCH
        /// variant); a would-have-switched notice is logged instead.
        /// </summary>
        public bool AutoSwitchProfiles { get; set; } = true;
    }

    /// <summary>
    /// Load/save for the single global settings file. UI-thread only; unlike
    /// <see cref="SettingsService"/> there is no engine consumer, so a plain
    /// mutable <see cref="Current"/> is fine.
    /// </summary>
    public sealed class GlobalSettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private readonly string _path;

        public GlobalSettingsService(string path = null)
        {
            _path = path ?? DefaultPath;
        }

        public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "global_settings.json");

        public GlobalSettings Current { get; private set; } = new GlobalSettings();

        /// <summary>Loads the file; missing or unreadable files fall back to defaults.</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    Current = JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(_path), JsonOptions)
                              ?? new GlobalSettings();
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Global settings load failed for {Path}; using defaults", _path);
                Current = new GlobalSettings();
            }
        }

        /// <summary>Write-then-rename so a crash mid-write cannot corrupt the file.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(temp, _path, true);
            }
            catch (Exception ex)
            {
                // A UI preference failing to persist must never break the app.
                Serilog.Log.Warning(ex, "Global settings save failed for {Path}", _path);
            }
        }
    }
}
