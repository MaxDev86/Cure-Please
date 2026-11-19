# TODO

Planned work, roughly in priority order. Cross entries out (move to CHANGELOG.md)
when done. Nothing here is committed to a timeline.

## Before release (testing)
- Run the QA docs end-to-end against live clients: QA-CurePlease3 (general),
  plus the per-job plans (BRD, GEO, RDM, SCH) in docs/. The app is pre-alpha —
  most features have never been exercised live.
- Verify multi-instance tab behavior live (attach two processes, confirm the PID
  registry blocks a double-attach and the shared UDP listener routes by name).

## Code health (from the 2026-11-18 review, docs/Claude-Sonnet5_5-Review-November18.md)
- Enable nullable reference types incrementally (#nullable enable per folder).
- Add a test project: engine scoring, settings migration, legacy importer are
  testable as-is; abstract GameStateService/CastingService behind interfaces so
  the engine can run without a game process.
- README: document the requireAdministrator requirement (memory access), the
  addon UDP protocol, and build steps.
- Settings: surface corrupt-file loads in the UI (the .bad backup exists, the
  user is not told); clamp loaded values (TickIntervalMs, StaleSeconds); consider
  rolling backups and JsonSerializerContext source-gen.
- Decision trace: UI-visible candidate list with scores and rejection reasons.

## Later / low priority
- DI + MVVM rework — defer until feature work settles (release hardening).
- Remove the legacy\ folder once the port is feature-complete (git history keeps it).
- Data catalogs (BuffCatalog, AbilityCatalog, ZoneNames) as embedded JSON.
- Weapon skills / merits tabs in Character Info — deferred; needed for the
  EasyFarm rewrite, not for Cure-Please itself.
- CI workflow (build + test) once tests exist.
