# Settings UI — Design Notes (pre-implementation)

The settings dialog is a nav-list + page WPF window editing a copy of the
active profile, publishing through SettingsService on Save. This file
collects the requirements that shape it; fill in the layout section when
the UI is built.

## Profiles and job combos

Profiles are per job combo (`ProfileService`, `<exe>\profiles\`,
`WHM_SCH.json`, optional `Charname_BRD_NIN.json` character prefix) and
auto-switch with the logged-in character — same scheme as the legacy
`MAIN_SUB.xml` files (the legacy settings window's bottom bar).

**A profile's main job AND sub job determine which settings are
relevant.** A WHM/SCH gains Light Arts, Addendum: White, Sublimation,
and Accession/Perpetuance stratagem policy; WHM/RDM instead cares about
enspells and Phalanx; /NIN brings Utsusemi, /DNC waltzes/steps, /BRD
songs (level-capped), /GEO Indi- (level-capped). The UI must therefore:

- Show the sections applicable to the profile's main + sub job, and
  visibly de-emphasize (not delete) the rest — a WHM/SCH profile keeps
  its RDM-sub fields in the file, harmless, so switching sub job later
  loses nothing.
- Label each job-locked section with the combo it applies to, so a
  shared `default.json` stays comprehensible.

Runtime enforces capability regardless of what the UI shows. Every
module gates candidates through `HasAbility` / `KnowsSpell` /
`MeetsSpellLevelRequirement`, and the engine additionally removes any
unusable spell or JA candidate before sending it. This final filter is a
fail-safe against new modules accidentally producing in-game
"command error" spam.

`CapabilityAudit` lists the enabled actions that are unavailable in the
current context. It runs on engine start, whenever the character,
main/sub job, job level, or main-job master level changes, and whenever
the active settings object changes (including profile switches). Each
issue is logged once with its settings source and a clear "it will not
be attempted" message; temporary recast/MP/range conditions are not
audit issues. Settings retained for an unrelated job page are ignored.
Instrument-granted Honor March and Aria of Passion keep their special
resource/mask exception.

Sub-job capability uses the game's reported effective sub-job level
(which already includes a master-leveled main job's raised cap). The
master level itself is part of the audit context, so gaining a master
level re-runs the audit even when the visible job levels stay the same.

## Edit semantics (from the original proposal)

- The dialog edits a deep copy; Save publishes atomically and writes the
  active profile file. Cancel discards.
- The engine reads settings per tick, so saved changes apply live,
  including profile auto-switches mid-session.
- Import path: legacy per-combo XML files import into the matching
  profile JSON (LegacySettingsImporter already maps cures, curaga,
  debuffs, bard, geo, scholar).

## Page layout (as built)

Nav is **feature pages + job pages**, mirroring the settings model
(legacy's tab grouping was right; its flat storage was not). Pages are
grouped under non-selectable headers in the nav list:

- **Core:** General, Priorities. General carries tick interval,
  **PauseOnZone** (legacy pauseOnZoneBox), and out-of-party healing.
  **PauseActions** is controlled by the main window's Pause/Resume button
  beside Start, not buried in Properties; snapshots and party display
  continue while new actions are paused. Zone behavior: a logged-in -> `Loading`
  login-status edge (or a zone-id change between snapshots, e.g. warp)
  makes the engine call `OnZoneChanged` on every module — buff/song/
  luopon tracking resets because zoning strips them all. With
  PauseOnZone off the engine resumes by itself at the first valid
  post-zone snapshot (no fixed sleep like legacy's 17 s); with it on,
  the engine latches a pause and the Start button becomes Resume until
  clicked.
- **Healing** (feature pages, always shown, no job gate — the
  Cure/Curaga/Debuff modules are job-agnostic and rely on runtime
  capability gating): Healing (Cures + Curaga), Debuff Removal.
- **Buffs:** Buff Upkeep (PL-only self-buffs). Per-member buffs are NOT a
  settings page — they are session-only assignments edited from the main
  window's per-row "Buffs…" button (see docs/buff-upkeep-design.md).
- **Jobs** (module gates on main/sub job): White Mage, Red Mage,
  Scholar, Bard, Geomancy. Generic healing must never be filed
  under a job page — none of the cure/debuff modules check the job.

## Scholar page — Rapture (shipped)

Rapture (SCH 55 White Grimoire stratagem; from /SCH it additionally needs a
master-leveled main job, since /SCH normally caps at 49) boosts the next cure.
A naive "always on" toggle would drain stratagem charges, so the Scholar page
exposes a conservative policy instead (defaults in parentheses):

- **Use Rapture before strong cures** (off) — master switch.
- **Target HP at or below (%)** (50) **and estimated missing HP at least**
  (600) — both must hold for the best cure candidate of the tick.
- **Reserve stratagem charges** (1) — never spend the last N charges.
- **At full charges, pre-charge Rapture instead of wasting recharge** (on) —
  when charges are capped, spend one to have Rapture ready even with no
  qualifying cure in sight; recharge progress is otherwise lost at the cap.

Mechanics: requires Light Arts or Addendum: White up, consumes one charge off
the shared stratagem timer (id 231, `Stratagems.AvailableCharges`), and is
offered just above the best cure so it executes first — the cure follows on a
later tick (same sequencing as Majesty, which it composes with). Presence-gated
on the Rapture buff (no double-spend) and covered by the 10 s JA verify
window. Legacy import maps `rapture` and counts it toward `Sch.Enabled`.

## Job-combo nav filter (user-controlled, shipped)

The nav is built by `SettingsNavigation.Build` (pure, harness-tested).
Feature pages always show. A checkbox under the nav — visible only when
the profile name encodes a combo (parsed by
`ProfileService.TryComboFromProfileName`, e.g. `Charname_WHM_SCH`) —
trims job pages to main+sub, labeling the sub page "(sub)". **Off by
default: nothing is ever hidden unless the user asks**, the filter is a
view preference only (the settings model keeps every section), an
unparseable profile name fails open (all pages), and a deep-linked page
(e.g. main window's Buffs… button) beats the filter.

The toggle persists in the app-level **global settings file**
(`<exe>\global_settings.json`, `GlobalSettingsService`) — not inside
per-combo profiles, since it is a viewing preference, not job behavior.
That file also stores `AutoSwitchProfiles` (default **true**, the legacy
behavior). The main window shows the active profile and an auto-switch
checkbox. With auto-switch off, a manually selected profile stays active
across character and job changes; when a changed context matches a
different profile, the UI logs a loud `PROFILE AUTO-SWITCH OFF` notice
once for that context instead of silently switching or staying stale.
Re-enabling the checkbox resumes switching on the next context change.
App-wide preferences stay in `<exe>\global_settings.json`; per-profile
job settings stay in `<exe>\profiles\`.

An early draft grouped cures/curaga/debuffs/buffs under a "White Mage"
page; that was wrong because none of those modules check the job, and
it was corrected before any settings file depended on it.
