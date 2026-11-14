using System.Collections.Generic;

namespace CurePlease.Settings
{
    /// <summary>
    /// Root settings model. Sectioned by feature (unlike the legacy flat ~180-property bag).
    /// The engine reads an immutable snapshot of this; the UI edits a copy and publishes
    /// atomically through SettingsService. Bumping <see cref="Version"/> requires a
    /// migration step in SettingsService.
    /// </summary>
    public sealed class CurePleaseSettings
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public GeneralSettings General { get; set; } = new GeneralSettings();
        public CureSettings Cures { get; set; } = new CureSettings();
        public CuragaSettings Curaga { get; set; } = new CuragaSettings();
        public PrioritySettings Priorities { get; set; } = new PrioritySettings();
        public BuffSettings Buffs { get; set; } = new BuffSettings();
        public DebuffSettings Debuffs { get; set; } = new DebuffSettings();
        public AddonSettings Addon { get; set; } = new AddonSettings();

        public CurePleaseSettings DeepClone()
        {
            return SettingsService.Clone(this);
        }
    }

    public sealed class GeneralSettings
    {
        /// <summary>Engine tick interval in milliseconds (legacy: 500).</summary>
        public int TickIntervalMs { get; set; } = 500;

        /// <summary>Pause all actions (legacy pause button).</summary>
        public bool PauseActions { get; set; }

        /// <summary>Allow healing the local player when not in the party.</summary>
        public bool EnableOutOfPartyHealing { get; set; } = true;
    }

    public sealed class CureTierSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Minimum estimated HP loss before this tier is considered.</summary>
        public uint MinimumHpLoss { get; set; }
    }

    public sealed class CureSettings
    {
        /// <summary>Party members at or below this HPP are cure candidates.</summary>
        public byte CurePercentage { get; set; } = 75;

        /// <summary>High-priority members get cured at or below this (usually higher) HPP.</summary>
        public byte PriorityCurePercentage { get; set; } = 85;

        /// <summary>Monitored character cure threshold.</summary>
        public byte MonitoredCurePercentage { get; set; } = 85;

        /// <summary>Tier table, highest tier first: Cure VI .. Cure I.</summary>
        public List<CureTierSettings> Tiers { get; set; } = new List<CureTierSettings>
        {
            new CureTierSettings { Enabled = true, MinimumHpLoss = 1200 }, // Cure VI
            new CureTierSettings { Enabled = true, MinimumHpLoss = 900 },  // Cure V
            new CureTierSettings { Enabled = true, MinimumHpLoss = 600 },  // Cure IV
            new CureTierSettings { Enabled = true, MinimumHpLoss = 400 },  // Cure III
            new CureTierSettings { Enabled = true, MinimumHpLoss = 200 },  // Cure II
            new CureTierSettings { Enabled = true, MinimumHpLoss = 100 },  // Cure
        };

        /// <summary>Allow falling back to a lower tier when the chosen tier is unavailable.</summary>
        public bool Undercure { get; set; } = true;

        /// <summary>Allow stepping up a tier when the chosen tier is on recast.</summary>
        public bool Overcure { get; set; }

        /// <summary>Restrict overcure to high-priority targets only.</summary>
        public bool OvercureOnHighPriority { get; set; } = true;
    }

    public sealed class CuragaSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Members below this HPP count toward a Curaga.</summary>
        public byte CurePercentage { get; set; } = 75;

        /// <summary>Minimum injured members before Curaga fires.</summary>
        public int RequiredMembers { get; set; } = 3;

        /// <summary>Tier table, highest first: Curaga V .. Curaga.</summary>
        public List<CureTierSettings> Tiers { get; set; } = new List<CureTierSettings>
        {
            new CureTierSettings { Enabled = true, MinimumHpLoss = 1200 }, // Curaga V
            new CureTierSettings { Enabled = true, MinimumHpLoss = 900 },  // Curaga IV
            new CureTierSettings { Enabled = true, MinimumHpLoss = 600 },  // Curaga III
            new CureTierSettings { Enabled = true, MinimumHpLoss = 400 },  // Curaga II
            new CureTierSettings { Enabled = true, MinimumHpLoss = 200 },  // Curaga
        };
    }

    /// <summary>
    /// Scoring weights for the candidate engine. Higher score wins; these replace the
    /// legacy fixed-order chains and hard vetoes (monitored-priority starvation,
    /// debuffs-only-before-small-cures) with tunable weights.
    /// </summary>
    public sealed class PrioritySettings
    {
        /// <summary>Score multiplier for cures on the monitored character.</summary>
        public double MonitoredWeight { get; set; } = 2.0;

        /// <summary>Score multiplier for cures on members marked high priority.</summary>
        public double HighPriorityWeight { get; set; } = 1.5;

        /// <summary>Base weight per point of (deficit / potency) ratio for cures.</summary>
        public double CureWeight { get; set; } = 100.0;

        /// <summary>Base score for a Curaga that meets its member count.</summary>
        public double CuragaWeight { get; set; } = 80.0;

        /// <summary>Score per debuff removal, multiplied by severity (0..1).</summary>
        public double DebuffWeight { get; set; } = 120.0;

        /// <summary>Base score for buff-upkeep actions (kept low: only when idle).</summary>
        public double BuffWeight { get; set; } = 20.0;

        /// <summary>Names of high-priority party members (replaces per-slot checkboxes).</summary>
        public List<string> HighPriorityMembers { get; set; } = new List<string>();
    }

    public sealed class BuffSettings
    {
        public bool AutoHaste { get; set; }
        public double AutoHasteMinutes { get; set; } = 3;

        public bool AutoProtect { get; set; }
        public double AutoProtectMinutes { get; set; } = 30;

        public bool AutoShell { get; set; }
        public double AutoShellMinutes { get; set; } = 30;
    }

    /// <summary>
    /// CurePlease Windower/Ashita addon (UDP) integration. The addon is one optional
    /// data provider: party-member buffs and cast events. The engine works without it,
    /// but party-wide debuff removal requires it until memory-based buff reads land.
    /// </summary>
    public sealed class AddonSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>UDP port the addon sends to (CurePleaseNext default 19771; legacy addon uses 19769).</summary>
        public int Port { get; set; } = 19771;

        /// <summary>Buff data older than this is treated as stale and ignored.</summary>
        public int StaleSeconds { get; set; } = 10;
    }

    public sealed class DebuffSettings
    {
        /// <summary>Master switch for party debuff removal.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Debuff removal takes precedence according to severity weight, not cure size.</summary>
        public List<string> EnabledRemovals { get; set; } = new List<string>
        {
            "Doom", "Paralysis", "Petrification", "Silence", "Sleep", "Poison", "Blindness", "Curse", "Disease",
        };
    }
}
