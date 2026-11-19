# Changelog

Curated, high-level entries — one line per meaningful change, newest first within a
release. Update with every commit (or commit batch). Commit messages carry the detail;
this file carries the narrative. See TODO.md for what is planned but not done.

## 3.0.0 (pre-alpha, unreleased)

Full rewrite of legacy CurePlease 2.0.4.4 (WinForms/.NET 4.8) as a WPF app on
.NET 10, driven by FFXI.Memory.

### Review hardening (uncommitted)
- Window close now uses cancel-then-close: tab shutdown (engine stop, addon unload,
  PID release) always completes before the process exits.
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
- CastingService: awaitable cast state machine with guaranteed unlock; cast-bar
  watcher with live-verified fast-cast semantics; stray addon "blocked" events are
  reconciled against the bar before being believed; post-JA/item lockout delays.
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
