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
        public BardSettings Bard { get; set; } = new BardSettings();
        public GeoSettings Geo { get; set; } = new GeoSettings();
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

        /// <summary>Base score for bard songs (JA prep steps score above this).</summary>
        public double SongWeight { get; set; } = 25.0;

        /// <summary>Base score for geomancer actions (Indi upkeep, luopan placement; prep JAs score above this).</summary>
        public double GeoWeight { get; set; } = 24.0;

        /// <summary>Names of high-priority party members (replaces per-slot checkboxes).</summary>
        public List<string> HighPriorityMembers { get; set; } = new List<string>();
    }

    public sealed class BuffSettings
    {
        /// <summary>Haste the members listed in <see cref="HasteTargets"/>.</summary>
        public bool AutoHaste { get; set; }

        /// <summary>Spell to use ("Haste" or "Haste II").</summary>
        public string HasteSpell { get; set; } = "Haste";

        /// <summary>Names to keep Haste on (replaces legacy per-slot checkboxes).</summary>
        public List<string> HasteTargets { get; set; } = new List<string>();

        /// <summary>Fallback refresh interval when buff presence is unknown (no addon data).</summary>
        public double AutoHasteMinutes { get; set; } = 3;

        /// <summary>Keep Protect on the whole party (cast on each member missing it).</summary>
        public bool AutoProtect { get; set; }

        /// <summary>Spell to use ("Protect" .. "Protect V").</summary>
        public string ProtectSpell { get; set; } = "Protect V";

        public double AutoProtectMinutes { get; set; } = 30;

        public bool AutoShell { get; set; }

        /// <summary>Spell to use ("Shell" .. "Shell V").</summary>
        public string ShellSpell { get; set; } = "Shell V";

        public double AutoShellMinutes { get; set; } = 30;
    }

    /// <summary>One Pianissimo single-target song assignment.</summary>
    public sealed class PianissimoTarget
    {
        public string Song { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }

    /// <summary>
    /// Bard song engine. Party songs are AoE around the bard and tracked via the bard's
    /// own buffs (always readable); pianissimo targets need known buffs (self, monitored
    /// process, or addon party data). See docs/bard-design-notes.md.
    /// </summary>
    public sealed class BardSettings
    {
        /// <summary>Master switch; the module also requires main or sub job BRD.</summary>
        public bool Enabled { get; set; }

        /// <summary>Ordered party rotation, sung on self (AoE). Later songs overwrite earlier ones when slots are full.</summary>
        public List<string> PartySongs { get; set; } = new List<string>();

        /// <summary>
        /// Low-value songs sung to occupy the instrument-granted slots so the real
        /// rotation can overwrite them. These must be songs the player's GearSwap lua
        /// treats as placeholders — equipping the multi-song instrument (Daurdabla)
        /// and NO duration midcast set for them — otherwise they will not be the
        /// lowest-duration songs and the overwrite will eat a real song instead.
        /// (Mote BRD luas do this via named midcast sets for Army's Paeon and
        /// Army's Paeon II, e.g. set_combine(sets.precast.FC.BardSong, {range=...}).)
        /// </summary>
        public List<string> DummySongs { get; set; } = new List<string>();

        /// <summary>
        /// Dummy-song placement when rebuilding the full song structure after losing it
        /// (death/zone/full dispel). The end state is identical either way — every party
        /// song up at full strength — only the path differs:
        /// "Middle" (default): the first two party songs are sung, then the dummies, then
        /// the remaining party songs (they overwrite the dummies).
        /// "First": dummies occupy the instrument-granted slots before any party song.
        /// Either way, GearSwap must sing dummies WITHOUT the duration midcast set so
        /// they are always the lowest-duration song and get overwritten — that overwrite
        /// rule is what makes both placements work.
        /// </summary>
        public string DummyPlacement { get; set; } = "Middle";

        /// <summary>Nightingale + Troubadour before a refresh pass ("NiTro").</summary>
        public bool UseNitro { get; set; }

        /// <summary>
        /// Use Soul Voice (1 h recast) before a refresh pass when it is off recast, and
        /// re-sing active songs that were not sung under Soul Voice while it is up.
        /// Off by default — most players save SV for specific fights.
        /// </summary>
        public bool UseSoulVoice { get; set; }

        /// <summary>
        /// Declared owned instruments (e.g. "Marsyas", "Daurdabla", "Gjallarhorn",
        /// "Blurred Harp +1", "Terpander", "Loughnashade"). Determines song-slot count
        /// (2 + best bonus in the list) and Honor March (Marsyas) / Aria of Passion
        /// (Loughnashade) availability. Cure-Please only sends /ma and /ja commands —
        /// the player's own GearSwap lua is expected to equip the right instrument per
        /// song, so this list declares what GearSwap has to work with. Empty = auto-detect:
        /// equipped Range slot first, then best known instrument from owned items.
        /// </summary>
        public List<string> OwnedInstruments { get; set; } = new List<string>();

        /// <summary>
        /// Legacy v2-alpha name for <see cref="OwnedInstruments"/> — set-only so old
        /// settings files migrate on load; never serialized back out.
        /// </summary>
        public string Instrument
        {
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && !OwnedInstruments.Contains(value.Trim()))
                {
                    OwnedInstruments.Add(value.Trim());
                }
            }
        }

        /// <summary>
        /// Offensive songs kept on the battle target while engaged (Elegy, Nocturne,
        /// Threnody, Requiem, Lullaby families). Enemy debuffs cannot be read from memory,
        /// so these are re-sung on a conservative timer; target switches reset tracking.
        /// </summary>
        public List<string> EnemyDebuffSongs { get; set; } = new List<string>();

        public bool UseMarcato { get; set; }

        /// <summary>Song to pair Marcato with (typically Honor March — naturally the longest).</summary>
        public string MarcatoSong { get; set; } = "Honor March";

        /// <summary>Clarion Call before songs that need the 3rd slot.</summary>
        public bool UseClarionCall { get; set; }

        /// <summary>Song intended for the Clarion Call slot (sung after dummies, before the rest).</summary>
        public string ClarionSong { get; set; } = string.Empty;

        /// <summary>Single-target top-ups via Pianissimo (5 s recast), e.g. Ballad on the tank.</summary>
        public List<PianissimoTarget> PianissimoTargets { get; set; } = new List<PianissimoTarget>();

        /// <summary>Estimated party-song duration before modifiers the bot itself applied (Troubadour doubles).</summary>
        public double BaseDurationSeconds { get; set; } = 180;

        /// <summary>Re-sing a tracked song when its estimated remaining time drops below this (overwrite-same is always safe).</summary>
        public double RefreshMarginSeconds { get; set; } = 30;

        /// <summary>AoE song reach with Horn; recipients beyond this are skipped (9.9 per the guide).</summary>
        public double SongRangeYalms { get; set; } = 9.9;
    }

    /// <summary>
    /// Geomancer support: one Indicolure aura (presence-based via buff 612 Colure
    /// Active) plus one luopan (pet-based via Player.PetIndex). Spell names, not the
    /// legacy combo indices. See docs/geo-design-notes.md.
    /// </summary>
    public sealed class GeoSettings
    {
        /// <summary>Master switch; the module also requires main or sub job GEO.</summary>
        public bool Enabled { get; set; }

        /// <summary>Indicolure spell to keep up on self (e.g. "Indi-Fury"). Empty = no Indi upkeep.</summary>
        public string IndiSpell { get; set; } = string.Empty;

        /// <summary>Only (re)cast the Indi spell while engaged.</summary>
        public bool IndiOnlyWhenEngaged { get; set; } = true;

        /// <summary>Master switch for luopan placement and management.</summary>
        public bool LuopanEnabled { get; set; }

        /// <summary>Geocolure spell to place (e.g. "Geo-Frailty"). Empty = no luopan.</summary>
        public string GeoSpell { get; set; } = string.Empty;

        /// <summary>
        /// Character a beneficial luopan is centered on. Empty = the monitored
        /// character when one is attached, otherwise self.
        /// </summary>
        public string LuopanTarget { get; set; } = string.Empty;

        /// <summary>
        /// Optional enemy name used for the engagement check and as the cast target of
        /// an offensive luopan (legacy specifiedEngageTarget + the dual-purpose
        /// LuopanSpell_Target, split into two explicit settings). Empty = monitored
        /// character's engagement, falling back to the local player's.
        /// </summary>
        public string EngagedTargetName { get; set; } = string.Empty;

        /// <summary>Blaze of Glory before dropping a luopan (luopan gets halved HP).</summary>
        public bool UseBlazeOfGlory { get; set; }

        /// <summary>Ecliptic Attrition on a healthy luopan (>= 90% HP, drains it faster).</summary>
        public bool UseEclipticAttrition { get; set; }

        /// <summary>Dematerialize on a healthy luopan (>= 90% HP).</summary>
        public bool UseDematerialize { get; set; }

        /// <summary>Life Cycle when the luopan is at 5-30% HP and the player is at >= 90%.</summary>
        public bool UseLifeCycle { get; set; } = true;

        /// <summary>Full Circle a luopan that ended up >= 10 yalms from the action, then re-drop it.</summary>
        public bool UseFullCircle { get; set; } = true;

        /// <summary>Entrust before casting <see cref="EntrustSpell"/> on a single target.</summary>
        public bool UseEntrust { get; set; }

        /// <summary>Indicolure spell cast on <see cref="EntrustTarget"/> while Entrust is up.</summary>
        public string EntrustSpell { get; set; } = string.Empty;

        /// <summary>Entrusted spell target. Empty = the monitored character when one is attached.</summary>
        public string EntrustTarget { get; set; } = string.Empty;

        /// <summary>Radial Arcana (consumes the luopan, restores MP) when MP drops to the threshold.</summary>
        public bool UseRadialArcana { get; set; }

        /// <summary>MP at or below which Radial Arcana fires (luopan must be up and near the player).</summary>
        public uint RadialArcanaMpBelow { get; set; } = 300;
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
