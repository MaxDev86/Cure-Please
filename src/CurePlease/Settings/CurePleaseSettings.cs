using System;
using System.Collections.Generic;
using CurePlease.Core;

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
        public SchSettings Sch { get; set; } = new SchSettings();
        public WhmSettings Whm { get; set; } = new WhmSettings();
        public RdmSettings Rdm { get; set; } = new RdmSettings();
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

        /// <summary>
        /// Pause actions when the player zones; resume is manual (legacy pauseOnZoneBox).
        /// When false, the engine still resets buff/song tracking on zoning but resumes
        /// automatically as soon as the character is logged back in.
        /// </summary>
        public bool PauseOnZone { get; set; }

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

        /// <summary>
        /// Cure members of the other alliance parties (slots 6-17). Their HP is
        /// readable; their buffs are not, so this covers cures only — debuff
        /// removal and presence-checked buffs stay own-party. Single-target spells
        /// can target alliance members; AoE (Curaga) cannot and stays party-only
        /// regardless of this setting.
        /// </summary>
        public bool CureAllianceEnabled { get; set; } = true;

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

        /// <summary>
        /// Majesty (PLD JA) before a cure when at least 2 members need one — the cure
        /// then lands AoE. Majesty also makes Protect/Shell AoE (see Buff Upkeep).
        /// </summary>
        public bool UseMajesty { get; set; }

        /// <summary>
        /// Divine Seal (WHM 15 JA, sub-job accessible) before a cure when the target
        /// is critically low — the cure's potency doubles. Shares the 10-minute
        /// recast with the grouped-removal use on the Debuff Removal page. Legacy:
        /// divineSeal (legacy fired it by PL MP; here it keys off the target).
        /// </summary>
        public bool UseDivineSeal { get; set; }

        /// <summary>Divine Seal fires when the best cure target is at or below this HPP.</summary>
        public byte DivineSealHpBelowPercent { get; set; } = 25;

        /// <summary>
        /// Cures for targets at or below this HPP may consume a stratagem buff
        /// (Accession, Perpetuance, ...) another module claimed for a buff or
        /// removal cast — life beats buffs; the claiming module re-preps. Above
        /// this HPP the claim holds and the cure waits a tick.
        /// </summary>
        public byte IntentEmergencyHpPercent { get; set; } = 25;
    }

    public sealed class CuragaSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Members below this HPP count toward a Curaga.</summary>
        public byte CurePercentage { get; set; } = 75;

        /// <summary>Minimum injured members before Curaga fires.</summary>
        public int RequiredMembers { get; set; } = 3;

        /// <summary>
        /// SCH main or sub: prep Accession and cast Cure I–IV as the party heal
        /// instead of the native Curaga line (Cure V/VI are not Accession-compatible).
        /// Costs one stratagem charge per party cure; falls back to the Curaga line
        /// when no charge is free. Legacy: accessionCure.
        /// </summary>
        public bool UseAccession { get; set; }

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

        /// <summary>Base score for scholar JA upkeep (arts, addenda, sublimation — idle-time self-buffs).</summary>
        public double SchWeight { get; set; } = 22.0;

        /// <summary>Base score for white mage JA upkeep (Afflatus stances; Devotion scores above this).</summary>
        public double WhmWeight { get; set; } = 22.0;

        /// <summary>Base score for red mage JA upkeep (Composure).</summary>
        public double RdmWeight { get; set; } = 22.0;

        /// <summary>Names of high-priority party members (replaces per-slot checkboxes).</summary>
        public List<string> HighPriorityMembers { get; set; } = new List<string>();
    }

    /// <summary>
    /// One tracked buff (docs/buff-upkeep-design.md). Buff Upkeep is PL-ONLY — these
    /// are the buffs kept on the character running Cure Please; per-member party
    /// buffs (and Accession) belong to the future Player Buffs page. The catalog
    /// supplies the buff id, default duration, and Perpetuance compatibility; the
    /// entry stores user choices.
    /// </summary>
    public sealed class BuffEntry
    {
        /// <summary>Spell name, e.g. "Protect V" — must match a BuffCatalog entry.</summary>
        public string Spell { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        /// <summary>Fallback refresh interval when buff presence is unknown; prefilled from the catalog's stock duration.</summary>
        public double DurationMinutes { get; set; }

        /// <summary>
        /// Accession (SCH stratagem, main or sub) before the cast: the self-cast
        /// extends to party members in range. Only meaningful for catalog-compatible
        /// spells; requires Light Arts up; doubles the MP cost. No range check —
        /// whoever is in range gets it.
        /// </summary>
        public bool UseAccession { get; set; }

        /// <summary>
        /// Perpetuance (SCH main only) before the cast: extends the duration (multiplier
        /// in Scholar settings). Only meaningful for catalog spells an SCH main can cast.
        /// </summary>
        public bool UsePerpetuance { get; set; }
    }

    public sealed class BuffSettings
    {
        /// <summary>
        /// Tracked buffs. Seeded from the catalog (all disabled) on first load —
        /// see <see cref="EnsureSeeded"/>.
        /// </summary>
        public List<BuffEntry> Entries { get; set; } = new List<BuffEntry>();

        /// <summary>
        /// Group name -> chosen spell for groups that are disabled (an enabled entry
        /// already identifies its group's spell). Written by the Buff Upkeep page.
        /// </summary>
        public Dictionary<string, string> GroupSelection { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fresh settings get the full catalog (all disabled); JSON load overwrites Entries afterward.</summary>
        public BuffSettings()
        {
            EnsureSeeded();
        }

        /// <summary>
        /// Seeds entries for any catalog spell not yet present (fresh settings get the
        /// full catalog, disabled; older profiles gain newly added spells).
        /// </summary>
        public void EnsureSeeded()
        {
            foreach (BuffCatalogEntry catalog in BuffCatalog.Entries)
            {
                if (!Entries.Exists(e => e.Spell.Equals(catalog.Spell, StringComparison.OrdinalIgnoreCase)))
                {
                    Entries.Add(new BuffEntry
                    {
                        Spell = catalog.Spell,
                        DurationMinutes = catalog.DefaultMinutes,
                    });
                }
            }
        }
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
        /// Optional enemy name(s) used for the engagement check and as the cast target of
        /// an offensive luopan (legacy specifiedEngageTarget + the dual-purpose
        /// LuopanSpell_Target, split into two explicit settings). Multiple enemies can
        /// be listed separated by ";", "," or new lines — engagement passes when ANY is engaged and
        /// the engaged one is used as the cast target/anchor. Empty = monitored
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

        /// <summary>
        /// Master switch for the enmity assist: an offensive luopon's debuff only lands
        /// on enemies the GEO has hate with, so before/after placing one we establish
        /// hate via a spell cast. Hate cannot be read back — probes are cheap and bounded.
        /// </summary>
        public bool EnmityAssist { get; set; }

        /// <summary>
        /// The tank/DD whose TARGET identifies the current enemy and who (party mode)
        /// receives the beneficial hate spell. Deliberately not the monitored
        /// character — PL and monitored are often the same character.
        /// </summary>
        public string EnmityPlayerName { get; set; } = string.Empty;

        /// <summary>Cast the hate spell on the enemy by name instead of on EnmityPlayerName.</summary>
        public bool EnmityCastOnEnemy { get; set; }

        /// <summary>Hate spell name — dropdown content comes from EnmitySpellCatalog.</summary>
        public string EnmitySpell { get; set; } = "Cure";

        /// <summary>Sub-job filter for the spell dropdown (Windower job id; 0 = Any).</summary>
        public int EnmitySubJobId { get; set; }

        /// <summary>Re-probe hate every N seconds while an offensive bubble is up (0 = once per enemy only).</summary>
        public int EnmityRefreshSeconds { get; set; }
    }

    /// <summary>
    /// Scholar arts and Sublimation upkeep (legacy Form2 SCHOLAR STRATAGEMS group).
    /// All presence-based off the player's own buffs; sub-job SCH works the same.
    /// </summary>
    public sealed class SchSettings
    {
        /// <summary>Master switch; the module also requires main or sub job SCH.</summary>
        public bool Enabled { get; set; }

        /// <summary>Keep Light Arts up. Mutually exclusive with Dark Arts — see the module docs.</summary>
        public bool LightArts { get; set; }

        /// <summary>Addendum: White while Light Arts is up (consumes a stratagem charge). Sub-job accessible (SCH 10).</summary>
        public bool AddendumWhite { get; set; }

        /// <summary>Keep Dark Arts up.</summary>
        public bool DarkArts { get; set; }

        /// <summary>Addendum: Black while Dark Arts is up (consumes a stratagem charge). Sub-job accessible (SCH 30).</summary>
        public bool AddendumBlack { get; set; }

        /// <summary>
        /// Keep Sublimation charging, and consume the charge once the MP deficit
        /// reaches <see cref="SublimationMpDeficit"/>. Not charged while Refresh is up
        /// (the buffs are mutually exclusive in-game).
        /// </summary>
        public bool Sublimation { get; set; }

        /// <summary>MP deficit (MaxMP - MP) at which a charged Sublimation is consumed (legacy: 100).</summary>
        public uint SublimationMpDeficit { get; set; } = 100;

        /// <summary>
        /// Rapture before a qualifying cure (SCH 55; from a sub job this requires a
        /// master-leveled main job because /SCH normally caps at 49). Consumes one
        /// stratagem charge and requires Light Arts.
        /// </summary>
        public bool UseRapture { get; set; }

        /// <summary>Rapture when the cure target is at or below this HPP.</summary>
        public byte RaptureCureHpBelowPercent { get; set; } = 50;

        /// <summary>Rapture when the target's estimated missing HP is at least this large.</summary>
        public uint RaptureMinimumHpLoss { get; set; } = 600;

        /// <summary>Do not spend a charge unless this many would remain afterward.</summary>
        public byte RaptureReserveCharges { get; set; } = 1;

        /// <summary>
        /// When charges are full, spend one to have Rapture ready instead of wasting
        /// recharge progress. If Rapture is already up, no charge is spent.
        /// </summary>
        public bool RaptureHarvestAtFullCharge { get; set; } = true;

        /// <summary>
        /// Perpetuance duration multiplier applied to enhancing casts made under it.
        /// Stock is 2x; hands gear raises it (Savant's Bracers +1 = 2.25, +2 / Arbatel
        /// NQ = 2.50, Arbatel +1 = 2.55, +2 = 2.60, +3 = 2.65). Pre-set from an
        /// inventory scan on attach unless <see cref="PerpetuanceMultiplierManual"/>.
        /// </summary>
        public double PerpetuanceMultiplier { get; set; } = 2.0;

        /// <summary>
        /// True once the user picks the multiplier themselves in the Scholar page —
        /// the attach-time inventory scan no longer overwrites it (covers bracers
        /// sitting on a storage slip, which the scan cannot see).
        /// </summary>
        public bool PerpetuanceMultiplierManual { get; set; }
    }

    /// <summary>
    /// White mage job abilities (legacy Form2 enhancing group): Afflatus stances and
    /// Devotion. Presence-based off the player's own buffs. Divine Caress lives in
    /// DebuffSettings — it modifies removals, not upkeep.
    /// </summary>
    public sealed class WhmSettings
    {
        /// <summary>Master switch; the module also requires main or sub job WHM.</summary>
        public bool Enabled { get; set; }

        /// <summary>Keep Afflatus Solace up (cure-potency stance). Mutually exclusive with Misery.</summary>
        public bool UseAfflatusSolace { get; set; }

        /// <summary>Keep Afflatus Misery up (Cura/Banish stance). Mutually exclusive with Solace.</summary>
        public bool UseAfflatusMisery { get; set; }

        /// <summary>Devotion: give MP to a party member at the cost of your own HP.</summary>
        public bool UseDevotion { get; set; }

        /// <summary>Named Devotion target. Empty = any qualifying party member.</summary>
        public string DevotionTarget { get; set; } = string.Empty;

        /// <summary>Devotion fires when the target's MP is at or below this (legacy: DevotionMP).</summary>
        public uint DevotionMpBelow { get; set; } = 300;

        /// <summary>Only Devotion while engaged (monitored character when attached, else self).</summary>
        public bool DevotionOnlyWhenEngaged { get; set; }
    }

    /// <summary>Red mage job abilities (legacy: Composure checkbox on the enhancing group).</summary>
    public sealed class RdmSettings
    {
        /// <summary>Master switch; the module also requires main or sub job RDM.</summary>
        public bool Enabled { get; set; }

        /// <summary>Keep Composure up (extends your enhancing magic duration). Main job only in-game.</summary>
        public bool UseComposure { get; set; }

        /// <summary>Convert (swap HP and MP) when MP drops to the threshold. Works from a sub job.</summary>
        public bool UseConvert { get; set; }

        /// <summary>
        /// MP percent at or below which Convert fires (legacy convertMP was absolute,
        /// default 300; percent scales with gear/level instead). Never fires while weakened.
        /// </summary>
        public byte ConvertMpBelowPercent { get; set; } = 10;
    }

    /// <summary>
    /// data provider: party-member buffs and cast events. The engine works without it,
    /// but party-wide debuff removal requires it until memory-based buff reads land.
    /// </summary>
    public sealed class AddonSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>UDP port the addon sends to (CurePlease3 default 19771; legacy addon uses 19769).</summary>
        public int Port { get; set; } = 19771;

        /// <summary>Buff data older than this is treated as stale and ignored.</summary>
        public int StaleSeconds { get; set; } = 10;
    }

    public sealed class DebuffSettings
    {
        /// <summary>Master switch for party debuff removal.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Divine Caress (WHM JA) before a removal so the cured target also gains
        /// immunity to that debuff. Fired when a removal is about to happen and the
        /// buff is not already up.
        /// </summary>
        public bool UseDivineCaress { get; set; }

        /// <summary>Debuff removal takes precedence according to severity weight, not cure size.</summary>
        public List<string> EnabledRemovals { get; set; } = new List<string>
        {
            "Doom", "Paralysis", "Petrification", "Silence", "Sleep", "Poison", "Blindness", "Curse", "Disease",
        };

        /// <summary>
        /// Wake sleeping party members with a cure spell (Sleep/Sleep2 are not
        /// -na-removable; any cure lands the wake). Never applies to the PL — a
        /// sleeping PL cannot cast.
        /// </summary>
        public bool WakeSleepEnabled { get; set; }

        /// <summary>Cure / Cura / Curaga — the spell used to wake sleepers.</summary>
        public string WakeSleepSpell { get; set; } = "Curaga";

        /// <summary>
        /// When the PL is silenced, use an item to clear it (the PL cannot cast
        /// Silena on themselves). Only fires when the item is actually in a
        /// portable container. Mute is intentionally NOT handled — unremovable.
        /// </summary>
        public bool PlSilenceItemEnabled { get; set; }

        /// <summary>Echo Drops / Catholicon / Remedy / Vicar's Drink.</summary>
        public string PlSilenceItem { get; set; } = "Echo Drops";

        /// <summary>When the PL is doomed, use Holy Water / Hallowed Water (Cursna can fail).</summary>
        public bool PlDoomItemEnabled { get; set; }

        /// <summary>Holy Water / Hallowed Water.</summary>
        public string PlDoomItem { get; set; } = "Holy Water";

        /// <summary>
        /// Group removals: when at least this many observable members share the same
        /// ailment, one cast removes it from everyone in range. Mechanism is picked
        /// automatically: Yagrush (free, always-on Divine Veil) if equipped, then
        /// Accession / Divine Seal per the toggles below. Below 2 disables grouping.
        /// </summary>
        public int GroupRemovalMinCount { get; set; } = 2;

        /// <summary>Allow spending a stratagem charge on Accession for grouped removals (SCH main or sub).</summary>
        public bool GroupRemovalUseAccession { get; set; } = true;

        /// <summary>
        /// Allow Divine Seal for grouped removals (WHM main 50+ only — the AoE comes
        /// from the Divine Veil trait, which /WHM does not get). Shares the 10-minute
        /// recast with the critical-cure use on the Healing page and is suppressed
        /// while a cure target is critically low.
        /// </summary>
        public bool GroupRemovalUseDivineSeal { get; set; }
    }
}
