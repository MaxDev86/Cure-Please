# Backlog — queued feature requests

Items the user has explicitly asked to defer. Not yet designed or implemented.

## Code health (low/medium priority — functionality first)

1. **Exception handling pass.** Unguarded file I/O: `SettingsService.Save/Load`
   (File.ReadAllText / WriteAllText / Move can throw on locked/missing files),
   `ProfileService.Duplicate/Delete/SaveAs`, `GlobalSettingsService`,
   `LegacySettingsImporter`. Some call sites catch, some don't — a locked or
   read-only profile file currently crashes or silently fails depending on the
   path. Decide per-site: surface a UI status line + Serilog, never crash.
2. **XML doc coverage.** Every public function/object should have
   `<summary>` / `<param>` / `<returns>`. Rough gaps (public members vs
   documented): GameSnapshot 49/21, GameStateService 44/31, CurePleaseSettings
   143/129, edit models (Whm 9/1, Sch 10/3, Debuffs 8/4, Healing 6/3),
   PlayerJobsWindow 8/1, CapabilityAudit 7/2.

## Character Info window: deferred tabs

The Spells and Abilities tabs are shipped. Two more tab ideas were evaluated
and deferred — both are informational only; nothing in the automation uses
weapon skills or merit allocations. Note: the WS grade matrix and merit
allocation data WILL be needed for the EasyFarm rewrite (separate project) —
when that starts, revisit these two items first.

### Weapon skills tab (deferred)

Not worth building for a healer/support app. Data is far messier than spells/
abilities: WS unlocks derive from combat skill grades per job x level thresholds
(not a single per-job table), plus merit WS (Upheaval, Resolution, ...) and
relic/mythic/empyrean WS gated by specific weapons. Would also need live
validation of the known-WS bitmask (`HasWeaponSkill`). If a combat view is
ever wanted, prefer showing combat skill levels/ranks + current weapon skill —
that data is already in client memory with no curation.

### Merits tab (deferred)

Shows merit allocations per job (general categories + job group 1/2). Today we
only read the *remaining* merit point count (one byte in the player struct);
the allocation table is not read. It exists in client memory (Windower exposes
it via `windower.ffxi.get_merits()`), but finding it ourselves means a new
version-dependent sig scan — the brittle RE work we want to avoid. Cheaper
route consistent with the hybrid design: have the CurePlease3 addon call
`get_merits()` and stream the table over the existing UDP channel. Names/ids
from Windower's `res/merit_points.lua`. Value: verifies merit setup and closes
the loop on "Merit" statuses in the Abilities tab (Altruism etc.); not used by
automation.
