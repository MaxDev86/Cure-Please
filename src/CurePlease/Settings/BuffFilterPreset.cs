namespace CurePlease.Settings
{
    /// <summary>
    /// Initial job-filter state for the Buff Upkeep page, taken from the attached
    /// character. View-only: it presets the filter dropdowns, nothing is persisted.
    /// </summary>
    public sealed class BuffFilterPreset
    {
        /// <summary>Main job id (1-22); 0 = no preset.</summary>
        public byte MainJob { get; set; }
        public int MainLevel { get; set; } = 99;

        /// <summary>Sub job id (1-22); 0 = no sub job.</summary>
        public byte SubJob { get; set; }
        public int SubLevel { get; set; } = 49;
    }
}
