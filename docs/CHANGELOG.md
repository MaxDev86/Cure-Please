# Changelog

Curated, high-level entries — one line per meaningful change, newest first within a
release. Update with every commit (or commit batch). Commit messages carry the detail;
this file carries the narrative. See TODO.md for what is planned but not done.

## 3.0.0 (pre-alpha, unreleased)

Full rewrite of legacy CurePlease 2.0.4.4 (WinForms/.NET 4.8) as a WPF app on
.NET 10, driven by FFXI.Memory.

### Priority fixes (uncommitted)
- Pianissimo's song picker now lists beneficial songs only, excluding Requiem,
  Elegy, Threnody, Lullaby, Virelai, and Magic Finale.
- Settings now has an existing-profile dropdown that reloads every editor page,
  with unsaved-change confirmation and failed-load state preservation.
- Party rows gain legacy-style enable checkboxes (checked by default); unchecked
  members are excluded from targeted support and AoE recovery trigger counts.
- Cast outcomes now come exclusively from spell-correlated action packets,
  never the cast bar; missing completions return Unknown and use live
  buffs/recasts plus a short retry rather than a genuine-failure strike.
  BRD no longer waits ~11 s on stale fast-cast bar progress between songs.
  A configurable Bard grace period controls post-completion song spacing.
  Cold-start rotations also advance from observed buffs when outcome packets
  are lost, so remaining real songs overwrite dummies instead of stalling.
- PL song refresh reads exact remaining times directly from the client's buff
  memory block, with the CurePlease3 0x63 feed as fallback; other recipients retain
  configurable landing-time estimates.
- PL and monitored processes now have independent Attach/Switch/Detach rows;
  the large right-hand Start/Pause/Resume button requires both attachments, and
  either detach or a monitored switch stops the engine. Start always resumes saved
  and zone pauses. A draggable splitter resizes the party grid and log.
- An out-of-party PL can display and single-target cure the monitored client's
  party, matching legacy cross-client behavior. An opt-in also allows Accession
  party cures through an injured monitored-party target; native AoEs stay blocked.
- BRD NiTro now continues with Troubadour alone when Nightingale is unavailable.
- Debuff removal skips out-of-range targets and backs off failed casts instead of
  repeatedly winning the action queue.
- A monitored client exiting detaches monitoring and stops the engine until both
  characters are attached and Start is pressed again.
- Corrupt/unreadable profiles no longer activate or report a successful switch.
- Legacy import honors singing/debuff master switches and no longer restores
  default removals when the legacy configuration had none selected.
- BRD landing reports are caster-keyed and spell-id correlated even when they
  arrive before cast feedback; unselected song contexts no longer accumulate.
- Curaga counts only reachable own-party members; alliance injuries no longer
  trigger party-only Majesty; Clarion overflow songs wait for their extra slot.
- GEO Entrust works without a luopan, and GEO spells check their MP cost.
- Session-buff assignments are thread-safe and replace sibling tiers; failed
  settings saves restore prior live state; Curaga member count is clamped.
- Native follow stops/resets cleanly and closed tabs stop their preview timers.

### GEO/BRD fixes (uncommitted)
- Restored legacy `GeoWhenEngaged` as the optional luopan engagement gate, including
  settings UI and legacy import; successful GEO/BRD casts no longer incur the 15-second
  failure retry backoff.

### Party debuff parity (uncommitted)
- DebuffModule scans the PL, monitored character, and addon-reported own-party
  buffs every tick; Evasion Down and Magic Defense Down now map to Erase.
- Empty addon buff reports clear prior state; stale entries expire, and per-tab
  roster filtering no longer prunes other instances' party data.
- Legacy import includes Magic Defense Down in the Erase group and honors the
  legacy addon enable switch.

### Review hardening (uncommitted)
- Window close now uses cancel-then-close: tab shutdown (engine stop, addon unload,
  PID release) always completes before the process exits. The final Close is queued
  through the dispatcher so synchronous cleanup cannot nest WPF close operations.
- Engine lifecycle: Start chains onto a still-unwinding previous loop instead of
  silently no-op'ing; StopAsync disposes the CTS safely; module registration is
  rejected while running; zone-pause flag is volatile.
- CastingService: typed addon-event decode (no magic +1 ints), Detail on
  SendFailed/Timeout outcomes, stale-event reset moved to just before SendCommand.
- AddonListener: name-keyed dictionaries capped at 64 entries (loopback spoofing
  cannot grow them unboundedly).
- SettingsService: failed saves clean up the .tmp file; a corrupt settings file is
  preserved as .bad before any save can overwrite it.
- UI log lines include the exception type; engine tick floor is a named constant;
  Converters empty catch narrowed to the three convertible exceptions.
- Source: Claude-Sonnet5_5-Review-November18.md (docs/).

### Core engine
- Single decision loop with one immutable GameSnapshot per tick; modules offer
  scored candidates, highest score wins, every decision logged.
- CastingService: awaitable cast state machine with guaranteed unlock;
  spell-correlated addon action events determine outcomes without the cast bar;
  unresolved completions reconcile through live buffs/recasts; post-JA/item lockout delays.
- Incapacitation gates per tick (sleep/stun/terror/charm/petrify block all;
  silence/mute/Omerta block spells; amnesia/impairment block JAs).
- StratagemIntents: engine-owned claim registry so prep JAs (Accession, Divine
  Seal, Rapture, Perpetuance, Majesty) are consumed by the intended spell.
- CapabilityAudit: per-tick hard gate plus a logged audit of configured-but-
  unavailable actions when job/level/master-level context changes.

### Modules
- Cure (Rapture/Divine Seal/Majesty preps, emergency intent), Curaga (Accession
  party-cure), Debuff (group -na AoE via Yagrush/Accession, Addendum: White gate,
  PL self-removal items), BuffUpkeep (family exclusivity, -ra coverage repair,
  Perpetuance/Accession preps, gear-aware durations), Bard (song rotation, dummy
  songs, instruments, Pianissimo, enemy debuffs, NiTro/Soul Voice/Marcato/Clarion),
  GEO (Indi upkeep, luopan management, enmity-spell handling), per-player session
  buffs, WHM/SCH/RDM job abilities (incl. configurable Convert threshold).

### UI
- Tabbed multi-instance: one InstanceView per attached process, "+" tab to add,
  per-tab close; PID registry prevents double-attach; one shared loopback UDP
  listener (port 19771) routes addon messages by character name.
- WPF settings window with nav tree, job pages, profile auto-switch by job combo
  (opt-in), Save As/Duplicate/Delete, dirty-check on close; legacy XML importer.
- Main UI: HP/MP bars, pause button (attached + running only), job/mastery line,
  party rows with follow buttons; Character Info window (Status/Jobs/Spells/
  Abilities tabs, learned vs learnable vs gear-gated).
- Serilog file logging to <exe>\logs\ with global exception capture.

### Addon
- CurePlease3 Windower addon: party buff feed, cast outcome events, action landing
  reports, handshake, in-client log function; loaded on attach, unloaded on detach.

## Legacy
- 2.0.4.2 and earlier: see git history (legacy\ folder, frozen).
