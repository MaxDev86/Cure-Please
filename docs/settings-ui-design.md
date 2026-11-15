# Settings UI — Design Notes (pre-implementation)

The settings dialog is a nav-list + page WPF window editing a copy of the
active profile, publishing through SettingsService on Save. This file
collects the requirements that shape it; fill in the layout section when
the UI is built.

## Profiles and job combos

Profiles are per job combo (`ProfileService`, `<exe>\profiles\`,
`WHM_SCH.json`, optional `Alternate_BRD_NIN.json` character prefix) and
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

Runtime already enforces capability regardless of what the UI shows:
every module gates candidates through `HasAbility` / `KnowsSpell` /
`MeetsSpellLevelRequirement`, so a configured-but-unavailable action on
the current combo simply never fires (legacy relied on the same trick
via `HasAbility`/`JobChecker`). The UI filtering is a presentation
concern; the engine is the correctness backstop.

Sub-job capability is level-capped (sub level = main/2): /SCH gets fewer
stratagem charges and no Addendum until 50+ main, /BRD songs cap at the
sub level, etc. The engine's level checks handle the spell side; JA
availability (Sublimation needs /SCH 50+, i.e. main 99) is likewise
gated by `HasAbility`.

## Edit semantics (from the original proposal)

- The dialog edits a deep copy; Save publishes atomically and writes the
  active profile file. Cancel discards.
- The engine reads settings per tick, so saved changes apply live,
  including profile auto-switches mid-session.
- Import path: legacy per-combo XML files import into the matching
  profile JSON (LegacySettingsImporter already maps cures, curaga,
  debuffs, bard, geo).
