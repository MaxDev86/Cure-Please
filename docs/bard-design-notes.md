# BRD Support — Design Notes for the Port

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

## Ranges & targeting

- AoE song max range: **9.9 yalms** (with Horn). Bot must range-check every
  intended recipient before singing an AoE song, or sing on a central
  target.
- Lullaby: string instruments = wide AoE (Horde Lullaby I: 8-yalm radius
  at 154 string skill); wind instruments = narrow fixed AoE. Single-target
  Lullaby should use Gjallarhorn/Marsyas (accuracy), not harp.
- Song debuff duration also benefits from Song Duration+ gear.

## Engine design implications (checklist for implementation)

- [ ] Per-character song-slot model: ordered slots, which song, which
      source (base/Clarion/instrument), estimated expiry.
- [ ] JA sequencing through the shared action queue (P1/P2 architecture):
      NiTro → songs; Marcato → Honor March; Pianissimo → single-target
      songs. Each step verified (buff present on target / JA buff on self)
      before advancing.
- [ ] Refresh policy: resing when estimated remaining < margin; resing
      same song is always safe (overwrite-same rule).
- [ ] Recovery policy: detect missing configured songs per character;
      Pianissimo-based individual recovery; dummy-song logic only when slot
      structure was lost.
- [ ] Range gate: 9.9 yalms AoE check; central-target selection.
- [ ] Config: keep legacy concepts (4 song slots, 2 dummy songs, clarion
      song, pianissimo target pairs) but express as ordered rotation +
      per-target-group assignments, not flat combo boxes.
- [ ] Bard must not starve healing: song engine feeds candidate actions
      into the same scored-candidate engine as cures/debuffs
      (see casting-priority.md) with configurable category weight.
