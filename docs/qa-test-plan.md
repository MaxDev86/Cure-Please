# CurePlease3 — QA Test Plan (pre-alpha 3.0.0)

Prerequisites for every session: Windower running with the target character(s) logged
in; the `CurePlease3` addon available under `Windower4/addons/CurePlease3`; a profile
directory next to the exe (`profiles\`); logs land in `<exe folder>\logs\`.

Legend: **[UI]** visual/layout, **[MEM]** verifies memory reads against the in-game
client, **[ENG]** engine behavior while Started, **[CFG]** settings persistence.

---

## Part 1 — Launch, Attach, Detach

1. [UI] Launch CurePlease3 with no game running. Expect: one instance tab, PL and
   Monitored dropdowns empty or showing nothing attachable, status "Detached",
   Start disabled, Pause disabled, Character Info hidden.
2. [MEM] With one client logged in: open the PL dropdown — the character name/PID
   appears. Attach. Expect: status "Connected", title bar becomes
   `CurePlease3 - <name> <MAIN><lvl>/<SUB><lvl>`, Start and Pause become enabled,
   Character Info button appears.
3. [MEM] On attach, the game client receives `lua load CurePlease3` — confirm the
   addon greeting in the FFXI chat log.
4. [ENG] Press Start, then Pause: no new actions fire, but HP/buff displays keep
   updating. Unpause resumes actions. Pause is disabled while detached or stopped.
5. [MEM] Detach: addon receives `lua unload CurePlease3` (chat confirms unload),
   title resets to `CurePlease3`, Start/Pause disabled, Character Info hidden.
6. [MEM] Close the app while attached: the addon is unloaded on the way out
   (no orphan addon left loaded).
7. [MEM] Kill the game process while attached (or log out): app transitions to
   Lost/Detached without crashing; reattach works after relog.
8. [UI] Zone the character while attached: app stays attached, no crash, values
   recover after zoning.

## Part 2 — Multi-Instance (tabs)

1. [UI] With two+ clients logged in: the `+` button next to the tab adds a new
   instance tab. Attach each tab to a different character.
2. [ENG] Each tab runs its own engine: start tab A only — only character A acts.
3. [MEM] Both tabs share the single UDP listener; each addon talks to its own
   instance (verify per-tab addon load/unload chat messages on attach/detach).
4. [UI] Closing a tab via its `X` detaches that instance only; the first tab has
   no `X` while it is the only tab.
5. [CFG] Attach two tabs to characters whose job combos map to the same profile
   name — the profile-collision warning appears in the log.
6. [MEM] The same PID cannot be attached in two tabs (PID registry blocks it).
7. [ENG] Start both tabs with different profiles — each follows its own settings
   (e.g. different cure thresholds visible in the log).

## Part 3 — Character Info Window

Open via "Character Info…" (visible only while attached).

### 3a. Status tab (live, refreshes ~2/sec)

1. [MEM] Name, main job (full name + level), sub job + level match the in-game
   status window.
2. [MEM] At level 99 main: shows `(Mastered, ML n)` or `(spent/2100 JP)`.
   Verify against the in-game Job Points menu.
3. [MEM] Mastered main: Exemplar shows `current / needed` matching the in-game
   status panel exactly (e.g. `904,594 / 1,379,407` at ML 43). At ML 50 it reads
   "(to ML cap)". Non-mastered main: no exemplar line at all.
4. [MEM] HP/MP/TP and percentages track live (take damage, use MP).
5. [MEM] Zone line shows `Name (id)` matching the current zone; zone and re-check.
6. [MEM] Buffs list shows `Name (id)` pairs matching active buffs; unknown ids
   show as `#id`.
7. [MEM] Pet: with a pet out (PUP automaton, SMN avatar, BST jug, DRG wyvern),
   shows name, HP%, MP%, TP; dismiss pet — line reads "Pet: none".
8. [MEM] Entity status flips Standing/Engaged when entering/leaving combat.

### 3b. Jobs tab

1. [MEM] All 22 jobs listed with correct levels; current main/sub in bold.
2. [MEM] Level-99 jobs show Mastered Yes/No, ML, and JP spent — spot-check against
   the in-game Master Levels menu.

### 3c. Spells tab

1. [MEM] Job dropdown shows `JOB<level>`; per-job spell lists grouped in 5-level
   buckets.
2. [MEM] Learned/Learnable/Locked states match the in-game spell list; enemy-only
   spells (Hastega, Slowga, Paralyga, Silencega, Banishga III) never appear.
3. [UI] Impact shows the blue "Gear Required" state with a tooltip naming
   Twilight/Crepuscular Cloak.
4. [UI] "Learnable only" and "Show locked" filters behave correctly.

### 3d. Abilities tab

1. [MEM] Current main/sub job: learned abilities show green "Learned".
2. [MEM] Off-job tabs show gray "Unknown" for level-met abilities (mask limits),
   "Locked" below level; a set bit from earlier in the session still shows Learned.
3. [MEM] SCH tab: stratagems classify by level on every job tab — with no Arts
   active they still show "Learned" (not "Learnable"), with a tooltip noting the
   required Arts. Activate Light Arts in game, then Dark Arts — displayed states
   must NOT change.
4. [MEM] COR tab: rolls show Learned only after the matching die was used;
   regular COR abilities (Phantom Roll, Double-Up, Quick Draw, Random Deal) show
   correctly without dice.
5. [UI] Merit abilities show "Merit" with the allocation tooltip; main-only
   abilities (Afflatus, Composure) carry the "main job only" tooltip.

## Part 4 — Profiles & Settings Shell

1. [CFG] Save a profile, edit a setting, reload — value persists.
2. [CFG] Auto-switch on: change main or sub job in game — profile matching
   `CHAR_JOB_SUB` (or `JOB_SUB`) activates; a log line records the switch.
3. [CFG] Auto-switch off: job change does NOT switch; the suppressed-switch
   notice is logged.
4. [CFG] Manually picking a profile sticks until the next job/character change
   (or forever with auto-switch off).
5. [CFG] Legacy import: import a legacy CurePlease profile XML — values map onto
   the new settings; spot-check cure thresholds and song lists.
6. [UI] Settings window opens large enough that no grid is clipped
   (Buff Upkeep table fully visible without resizing).

## Part 5 — Settings Pages (per-job logic)

### 5a. General
- [CFG] Pause-when-zoned option exists and persists; with it on, zoning pauses
  actions and unpauses after arrival (per design).

### 5b. White Mage
- [UI] Only one Afflatus (Solace XOR Misery) selectable — picking one clears
  the other. Both carry the "main job only" note.
- [UI] Devotion shows the merit/main-job note. Divine Seal configurable.

### 5c. Scholar
- [UI] Light Arts and Dark Arts are mutually exclusive.
- [UI] Selecting Addendum: White forces Light Arts on; Addendum: Black forces
  Dark Arts on; clearing the Arts clears the addendum. All combinations behave.
- [UI] Rapture options: enable, HP% threshold, minimum HP loss, harvest-at-full-
  charge. Notes mention sub-job access requires a mastered job.

### 5d. Red Mage
- [UI] Composure carries the "main job only" note.
- [CFG] Convert threshold configurable (default 10% MP); persists per profile.

### 5e. Buff Upkeep
- [UI] Job filter dropdown pre-filters the spell list by main+sub (WHM/SCH 99/59
  must NOT offer Regen V — SCH-main-only).
- [UI] Grouped dropdowns: Protect, Shell, Bar-element, Bar-status, Gain-stat,
  Boost-stat, Regen, Refresh, Haste, En-spell, Storm.
- [UI] Exclusivity: only one Gain-, one Boost-, one En-spell, one Storm, one
  Protect-or-Protectra, one Shell-or-Shellra, one Bar-element-or-Bar-ra,
  one Bar-status-or-Bar-ra-status selectable at a time.
- [UI] Accession column present; incompatible spells (Haste, Refresh II/III,
  Boost-*) reject Accession. Perpetuance only offered on SCH-castable spells
  (Phalanx II must NOT offer it).
- [UI] Tier-2 storm note reads "SCH99 Main + 100JP"; Embrava note references
  SCH 1hr (Tabula Rasa).
- [CFG] Duration/fallback timers persist per buff.

### 5f. Debuff Removal
- [UI] Ailments (-na family), Erase-only status effects, and Sleep-via-cure
  are all represented; Doom/Mute correctly excluded or item-gated.
- [UI] PL-silenced item fallback configurable (Echo Drops / Remedy).
- [UI] Yagrush note: -na spells (incl. Erase) become AoE — Accession not needed.

### 5g. Geomancy
- [UI] Indi and Geo bubble selection, sub-job filter for the hate-establish spell
  ("Any" lists all sub-job-obtainable spells; a chosen sub filters the list).
- [UI] Player-cast vs enemy-cast hate methods; enemy list accepts multiple names
  with newline/comma delimiters; fields not clipped (text on its own line).

### 5h. Bard
- [UI] Party/dummy/debuff song lists; enemy debuff dropdown filtered to debuff
  songs only; Pianissimo target dropdowns present.
- [ENG] Honor March (Marsyas-gated) is still sent even though it is "not
  learned" — verify the cast fires in game.

### 5i. Priority
- [UI] Page ordering: per-member priority list editable and persists.

### 5j. Nav order
- [UI] Pages ordered by the agreed grouping (cures first, then buffs/debuffs,
  then jobs).

## Part 6 — Engine Behavior (in game, Started)

Setup: party with at least one damaged member; PL attached and started.

1. [ENG] Cures: correct tier chosen by threshold and estimated HP loss; priority
   order respected; no casts on full-HP members.
2. [ENG] Alliance: members of other alliance parties show in the grid and are
   cured (HP visible even though their buffs are not).
3. [ENG] Curaga: fires only when enough injured members are in range of the
   chosen center.
4. [ENG] Accession (buffs): with Light Arts up and Accession enabled for a buff,
   the engine chains Light Arts → Accession → buff; party members in range
   receive it. Accession is NOT wasted on a cure queued in between (the
   claim/intent system holds it) — verify in the log.
5. [ENG] Accession (cures): qualifying Cure I–IV becomes party-wide under
   Accession per settings.
6. [ENG] Accession (-na): with Addendum: White active, enabled -na removals go
  AoE under Accession; without the addendum they stay single-target (SCH main).
7. [ENG] Perpetuance: SCH-main-only buffs cast under Perpetuance get the
   extended duration; it is never attempted via /SCH.
8. [ENG] Rapture: fires only when the best cure meets BOTH the HP% threshold and
   minimum HP loss; harvest-at-full-charge fires Rapture when charges cap even
   without a qualifying cure; charges are never drained below the reserve.
9. [ENG] Divine Seal: fires ahead of a big cure per settings; never double-
   burned (recast respected).
10. [ENG] Convert (RDM): fires at the configured MP threshold only.
11. [ENG] Stratagem bookkeeping: charges regenerate per the level/JP table
    (1/2/3/4/5 charges; 4:00 → 0:33 recharge); the engine never queues a
    stratagem with zero charges.
12. [ENG] Buff upkeep: buffs recast at the configured fallback timer even when
    no wear-off message arrives (the other-player-overwrite case); when the
    wear-off message does arrive, recast is immediate.
13. [ENG] -ra upkeep: a member missing Protect after Protectra gets the single-
    target Protect (not a wasted -ra); bar- spells accessioned vs not follow the
    configured mode.
14. [ENG] Debuff removal: poison → Poisona, etc.; Erase-only effects (e.g. stat-
    down, helix) removed by Erase; Sleep removed by a cure; silenced PL uses
    Echo Drops/Remedy; no attempt on Doom/Mute.
15. [ENG] Bard: song rotation upkept; Pianissimo single-target songs land on the
    configured targets; enemy debuff songs cast on the engaged enemy only.
16. [ENG] GEO: luopon placed on the configured enemy list; hate-establish spell
    fires first (cure on the configured player OR offensive cast on the enemy,
    per mode); indi bubble upkept.
17. [ENG] Capability audit at Start: any configured-but-unavailable spell/JA is
    logged once (e.g. ML-gated Rapture via /SCH without a mastered job) — no
    command-error spam in the FFXI chat; addon-side log lines appear in chat
    without reaching the server.
18. [ENG] An action whose target moves out of range is skipped/retried cleanly —
    no crash, no infinite spam (attempt throttling works).
19. [ENG] A JA already active is not re-sent (no "already active" chat errors).

## Part 7 — Follow & Misc

1. [ENG] Follow: right-click a party member → "Follow this player"; PL trails at
   the configured distance, recovers from obstacles; "Stop following" releases.
2. [UI] Main window title shows `CurePlease3 - <name> <job>` per active tab.
3. [CFG] Version reads 3.0.0.
4. [UI] Logs are written to `<exe folder>\logs\` (not AppData).

## Part 8 — Regression / Stability

1. Run attached + started for 2+ hours: no memory growth spike, no log flood,
   no missed recasts.
2. Attach → detach → reattach 20× across tabs: no leaked handles, addon state
   consistent.
3. All settings pages: open/close 10× — no exceptions; SettingsWindow
   NullReference regression (fixed earlier) stays fixed.
4. Character Info window: open/close repeatedly while zoning — no stale-data
   exceptions.

---

### Known limitations QA should NOT file as bugs (retest notes)

- Ability "Learned" state for non-current jobs reads "Unknown" unless the job
  was used earlier in the login session (client mask limitation).
- Merit-stratagem allocation (Altruism etc.) cannot be read; classified "Merit"
  until seen under Arts.
- Exemplar points display only for a mastered main job (client struct tracks
  the current main job only).
- Alliance member buffs are not detectable (HP is).
