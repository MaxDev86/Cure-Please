# BRD Support — Design Notes for the Port

**Standing design rule (whole app, not just BRD): Cure-Please sends actions;
it never manages gear.** The decision layer reads game memory and emits
`/ma` / `/ja` (and at most `/target`) commands; equipment is owned by the
player's client-side tooling (typically a GearSwap lua). Reproducing
GearSwap would require packet reading *and* injection — out of scope
permanently. Consequence for instruments: the user declares which
instruments they own (`BardSettings.OwnedInstruments`) and we trust their
lua to equip the right one per song; the live equipped slot is only a
fallback signal for players without GearSwap, never an override — equipped
state at decision time is transient GearSwap *output*, not input.

Design reference:
[Obamiwannabuffya: A Bard Guide by Funkworkz (BG-wiki)](https://www.bg-wiki.com/ffxi/Obamiwannabuffya:_A_Bard_Guide_by_Funkworkz)
(major update 2022-11-23). This file distills the bot-relevant mechanics so
the design does not depend on the live page. Gear-set content of the guide
is out of scope (the player gears the character; the bot sings).

Legacy state: bard code in `legacy/Form1.cs` is partial and buggy
(see legacy-assessment.md section 2, findings 5–7: wrong-name song guards,
one-song-per-tick `else if` recovery, songs counted on the monitored player
instead of the bard). Replace wholesale with a real song engine; keep only
the config *concepts* (song1–4 slots, dummy songs, clarion song,
pianissimo targets already exist in legacy Form2).

## Song slot model (core mechanic the engine must implement)

- Base: **2 song slots** per character.
- **Clarion Call** adds a 3rd slot. Clarion Call songs *always take the
  third slot*.
- Instruments granting extra songs (Daurdabla, etc.) take the **4th/5th
  slots**, or 3rd/4th when Clarion Call is not active.
- **Overwrite rule:** singing on a character already at max songs
  overwrites their **lowest-remaining-duration** song.
- **Exception:** re-singing a song already on the target overwrites *that
  song* regardless of remaining duration or potency (no "has no effect"
  message). This is what makes refresh-by-resing safe.
- Consequence: slot position cannot be freely re-applied — if the "second
  slot" song was dispelled from a 5-song character, re-singing it without
  the right ability/instrument active overwrites the lowest-duration song
  instead. The engine must track *which ability/instrument produced each
  slot*, not just which songs are present.

## Job abilities and how the engine should sequence them

- **Troubadour** — 1.5x cast time, doubles song duration, adds accuracy
  (5/5 = elemental-seal-level accuracy). JP: +20 s song duration.
- **Nightingale** — reduces song cast/recast; 5/5 = 100% Quick Magic.
  Stacks with Troubadour ("NiTro") — the standard pre-buff combo.
- **Marcato** — boosts next song; typically paired with Honor March
  (naturally longest song when Marsyas-equipped).
- **Clarion Call** — extra song slot (see above); JP: +40 s song duration.
  Clarion songs can be kept up indefinitely by resinging before they wear.
- **Soul Voice** — doubles song effects; ~3.5 min duration with relic +2
  body. Stacks with Song+ gear/merits.
- **Pianissimo** — single-target song, **5 s recast**. The tool for
  individual top-ups and dispel recovery.
- **Tenuto** — keeps a song on the bard (e.g. Ballad for MP), 5 s recast;
  use before singing on others.

## Durations & timing (for the bot's refresh model)

- Party buff songs last ~3 minutes flat with no bonuses; Carnwenhan
  (Mythic) extends by 50% (→ ~4:30); Troubadour doubles; Song+ gear and
  Song-Effect-Duration gear extend further. **No known cap.**
- Songs differ in length from each other depending on per-song Song+ gear
  and Marcato pairing — the engine cannot assume uniform durations.
- **Miracle Cheer** (Mog Bonanza instrument): all songs fixed at 15 min,
  does not stack with other duration gear; with Soul Voice + Clarion Call
  and a re-apply pass before SV wears (~20 s), party SV durations reach
  ~18 min.
- The guide explicitly warns third-party timers are unreliable (gear not
  picked up, lag). The bot must track **its own conservative duration
  estimates per character** (base × known multipliers the *bot itself*
  applied: Troubadour, Clarion, Marcato) and refresh with margin, rather
  than trusting external timers.
- Relevant existing API surface: party-member song buffs are visible in
  buff data (same mechanism as debuff tracking — addon packet 0x076 today;
  see legacy-assessment.md section 3). Duration remaining is NOT in buff
  data → estimation is unavoidable.

## Recommended buff sequence (from the guide's walkthrough)

Full-party buff (NiTro active):
1. Nightingale + Troubadour
2. Sing in order: **Minuet IV → Minuet V → Minne V → Victory March →
   Marcato + Honor March** (DDs first for max damage potential)
3. **Pianissimo Minne IV → Pianissimo Ballad III** on the tank
4. **Pianissimo Ballad II → Pianissimo Ballad III** on supports (or Etudes
   etc. as configured)

Dispel/death recovery (per affected character only — never re-dummy the
whole party):
1. Pianissimo + dummy song(s) if slot structure was lost
2. Pianissimo + re-sing the lost song
3. (Guide steps assume fast-cast gear; recast gaps are negligible)

"Dummy songs" (low-value songs used to fill slots so later real songs land
in the right slot) are only needed when a character *lost* a song —
death, dispel, zone, natural expiry.

The guide's own cold-start walkthrough is a two-pass variant: sing the full
set *unboosted* in any order (the unboosted copies act as the dummies), then
NiTro and re-sing each song — each boosted copy overwrites its own unboosted
one via the overwrite-same rule, so dummy duration is irrelevant. The
implemented engine instead uses dedicated dummy songs + the
overwrite-lowest-duration rule (see below), which converges in fewer casts
but requires GearSwap to sing dummies WITHOUT the duration midcast set so
they are always the lowest-duration song on the player.

## Ranges & targeting

- AoE song max range: **9.9 yalms** (with Horn). Bot must range-check every
  intended recipient before singing an AoE song, or sing on a central
  target.
- Lullaby: string instruments = wide AoE (Horde Lullaby I: 8-yalm radius
  at 154 string skill); wind instruments = narrow fixed AoE. Single-target
  Lullaby should use Gjallarhorn/Marsyas (accuracy), not harp.
- Song debuff duration also benefits from Song Duration+ gear.

## Engine design implications (checklist for implementation)

Implemented in `src/CurePlease/Core/Modules/BardModule.cs` (v2, headless-tested
69 checks incl. engine gates). Deviations from the original checklist are noted.

- [x] Per-character song-slot model: ordered slots, which song, estimated
      expiry. Simplified: the "slot source" (base/Clarion/instrument) is not
      tracked — presence is observed from real buff data every tick, and
      trimming the tracked list to the observed family count enforces the
      overwrite-lowest-duration rule implicitly.
- [x] JA sequencing through the shared action queue: NiTro → songs;
      Marcato → MarcatoSong; Clarion Call before 3rd-slot songs; Pianissimo →
      single-target songs. Each step is verified by the JA buff appearing on
      the PL (10 s verify window), not by a blind wait — a blocked JA falls
      back to singing without it instead of stalling.
- [x] Refresh policy: resing when estimated remaining < margin; resing
      same song is always safe (overwrite-same rule). Estimates use only
      multipliers the bot itself applied (Troubadour ×2).
- [x] Recovery policy: missing configured songs are re-sung from observed
      buff counts (PL from memory, own party via the addon); Pianissimo-based
      single-target recovery for configured pairs; dummy songs are only
      prepended when ALL song buffs are gone (death/zone/full dispel).
- [x] Range gate: 9.9 yalms (configurable); member-triggered re-sings are
      skipped for out-of-range members, Pianissimo pairs are range-checked.
      Deviation: no central-target selection — party songs are always sung
      on self.
- [x] Config: ordered rotation + dummy list + clarion song + pianissimo
      pairs (`BardSettings`), replacing the legacy flat combo boxes. Legacy
      song1–4/dummy1–2 combo indices are mapped by the importer (separator
      rows accounted for; "Godess's Hymnus" typo and trailing-space names
      corrected). Note: the legacy clarionSong combo was never persisted
      (dead UI), so there is nothing to import for it.
- [x] Bard must not starve healing: songs/JAs are candidates with
      `Priorities.SongWeight` (default 25, above buff upkeep 20, below
      cures); JA prep steps score SongWeight + 10 so sequencing falls out of
      the same scoring rule.

## v2 additions (Singer addon parity — see "Singer comparison" below)

- [x] Instrument-aware slot model (`BardSettings.OwnedInstruments`, empty =
      auto-detect). Table: Daurdabla(90/99) +2; Daurdabla(75–85)/Blurred
      Harp(+1)/Terpander/Loughnashade II +1; Loughnashade III +2.
      **Marsyas and Gjallarhorn grant NO additional song slots** (+0 —
      potency/duration instruments; an earlier draft of the table wrongly
      credited them with +2). Honor March gated on Marsyas,
      Aria of Passion on any Loughnashade (family `Aria` = buff 213).
      The declared list is the source of truth (slots = 2 + best bonus in
      the list; GearSwap equips per song). Auto-detect — equipped Range slot
      via FFXI.Memory `GetEquippedItem` (live-verified), then best owned
      instrument — only runs when nothing is declared. When the settings UI
      gains an instrument control it should be a checkbox list, not a
      dropdown. The legacy v2-alpha singular `Instrument` JSON key migrates
      into the list on load.
- [x] Dummy-fill trigger extended: dummies are also sung when the instrument
      grants more slots than the real rotation fills (spare slots = permanent
      padding, Singer parity). On a cold start (all song buffs lost) with a
      multi-song instrument whose slots the rotation fills, a dedicated
      cold-start pass runs instead: dummies are sacrificial, placed per
      `BardSettings.DummyPlacement` — **"Middle"** (default: first two real
      songs → dummies → remaining real songs) or **"First"** (dummies before
      everything). Once all dummies were observed up together they are
      retired from the rotation and the remaining real songs overwrite them;
      the pass ends when every real family is observed. The pass walks the
      rotation *positionally* (family-deficit detection would let a later
      same-family song jump the queue). Both placements converge to the same
      end state; "Middle" gets the two most important songs up ~20 s sooner.
- [x] Soul Voice (`UseSoulVoice`, opt-in — 1 h recast): offered before a
      refresh pass when enabled; songs tracked with `WithTroubadour` /
      `WithSoulVoice` flags are re-sung while the JA is up when they predate
      it (Singer's nt/sv upgrade flags). Duration estimate ×2 per active JA.
- [x] Enemy debuff songs (`EnemyDebuffSongs`): Elegy/Nocturne/Threnody/
      Lullaby families on the battle target (`<t>`). Timer-based tracking
      with base durations (enemy debuffs are unreadable from memory);
      re-sing on expiry or target switch. Magic Finale is not supported
      (no trackable debuff). Sleep-lock via Lullaby works by configuration.
- [x] Incapacitation gates (engine level): sleep/petrify/stun/charm/terror
      block everything; silence/mute/Omerta block spells (items exempt);
      amnesia/impairment block job abilities.
- [x] Addon action landings (0x028): the Windower addon reports per-target
      recipients (`CUREPLEASE_action_<spellId>_<names>`); a confirmed landing
      on a member suppresses "member missing song" re-sings for 15 s while
      the 0x076 buff packet lags. Ashita addon does not send this yet (raw
      packet parsing; landing data degrades gracefully).
- [x] Pianissimo per-target song lists: multiple `PianissimoTargets` entries
      may share one target name; each song is tracked independently.

Known v2 limits: instrument-granted songs (Honor March via Marsyas, Aria of
Passion via Loughnashade) skip the known/level gates entirely — the client's
known-spell mask only contains them while the instrument is equipped, and
GearSwap equips it mid-cast, so the check can never pass at decision time; the
instrument gate (declared/owned) is the availability check instead, with the
recast timer still honored. Honor March is additionally absent from some
resource DB versions, in which case it is offered with no DB checks at all.
Either way a song that keeps failing is disabled for the session after 3
consecutive failures. Pianissimo pairs require buff visibility on the
target (self, monitored process, or addon party data) — timer-only tracking
for outside targets was deliberately not built (unreliable per the guide).
Enemy songs assume the battle target is a mob (`/ma ... <t>` on a player
target is harmless but wasteful).

## Singer comparison (why the design diverges)

Singer (Windower lua, Ivaar) is packet-driven: it estimates every song's
expiry per target from 0x028/0x029 packets and a large gear/JP duration
table (`song_timers.lua`). CurePlease instead observes real buff presence
(PL from memory, party via the addon) and only estimates durations to
refresh *ahead* of wear — dispels, zoning, and overwrite rules need no
modeling. Singer's instrument tables predate Loughnashade (Prime weapon,
item ids 22304–22307; stage II grants +1 song slot, stage III +2 and frees
Aria of Passion outside Sortie), which is why it miscomputes slots for
Loughnashade users — our table includes it, and the equipped stage is read
directly from the equipment table.

## GearSwap contract (dummy songs)

CurePlease sends only /ma and /ja commands — equipment is the player-side
lua's job. The cold-start pass depends on one contract:

**Dummy songs must be songs the player's GearSwap lua equips the multi-song
instrument (Daurdabla etc.) for, WITHOUT duration gear.** Dummies are the
overwrite targets; if a dummy gets the full duration set it outlives real
songs and the positional overwrite math breaks.

Reference implementation (user-verified): the user's Mote-Include v2 BRD lua
(`<Character>_BRD.lua`) lines 423–424 —

```lua
sets.midcast['Army\'s Paeon']    = set_combine(sets.precast.FC.BardSong, {range=info.ExtraSongInstrument})
sets.midcast['Army\'s Paeon II'] = set_combine(sets.precast.FC.BardSong, {range=info.ExtraSongInstrument})
```

with `info.ExtraSongInstrument = 'Daurdabla'` / `info.ExtraSongs = 2`
(lines 177–179). Honor March routes to Marsyas (lines 407, 905–907,
961–963); everything else gets Gjallarhorn + the duration set (429/450).

Consequences:

- The user's `DummySongs` setting must list exactly the songs their lua
  special-cases (for this lua: `Army's Paeon`, `Army's Paeon II`).
- Mote-Include's `state.SongMode` (None/Placeholder) macro toggle is NOT
  required — the midcast overrides above apply regardless of mode.
- Because GearSwap swaps Daurdabla in only mid-cast, the equipped slot
  alone never shows the extra slots → the user must declare
  `OwnedInstruments` in settings.json (e.g. `["Marsyas", "Daurdabla"]`)
  for the engine to plan 4 slots and gate Honor March correctly.
