# Multi-Instance (Tabbed Attachments) — Design Research

**Status: SHIPPED.** MainWindow is a tab shell owning one `InstanceView` per
attached process; the shared `AddonListener` routes cast events by character
name; `PidRegistry` blocks two tabs from claiming the same PL process; a
profile-collision warning fires when two tabs hold the same profile file;
`EliteAPI` implements `IDisposable` and `GameStateService.Detach()` disposes
deterministically. The original research follows.

What it would take to attach to N FFXI processes from one Cure-Please window:
one tab per instance, each tab its own PL character, profile, and optional
monitored character. Research only — no code changed.

## Verdict

The architecture is already close. Everything on the engine side is
constructor-injected, so a "tab" is mostly *composition*, not new logic. The
real work is (1) a per-tab context object, (2) the tab UI shell, and (3) two
cross-talk fixes (addon protocol, profile files). No blockers found in
FFXI.Memory.

## FFXI.Memory — already per-process safe

- `EliteAPI(int processId)` holds a native per-process `_instance` handle;
  every tool wrapper is built from it (`EliteAPI.cs:75`). Two EliteAPI objects
  on different PIDs do not share attachment state.
- `Engine/Instance` keeps per-pid Windower MMF views
  (`WindowerMMFKeyboardHandler_<pid>` etc.) and its own scan cooldowns.
- Static-state audit: `DataSources` statics are pure functions taking
  `Instance`; `Chat`'s statics are one global UDP feed keyed by *character
  name* (`Chat.cs:23`) — shared by design and safe.
- **Gap to fix:** `EliteAPI` has a finalizer but no `IDisposable`
  (`EliteAPI.cs:84`), and `GameStateService.Detach()` just nulls the field.
  With tabs opening/closing, native handles must be released deterministically:
  add `IDisposable` to `EliteAPI` and have `GameStateService` (or the tab
  context) dispose on detach/close.
- Attach cost: each attachment runs its own signature scans. N tabs = N scan
  passes, once per attach — acceptable, but attach should stay off the UI
  thread.

## Cure-Please — singleton inventory

Already instance-based (just new one per tab):

| Component | Per-tab state it owns |
|---|---|
| `GameStateService` | `_pl`/`_monitored` EliteAPI pair, connection state, item caches |
| `Engine` | module list, tick loop, per-tick snapshot |
| `CastingService` | `_castSlot` semaphore, cast-event correlation |
| Modules | BardModule song timers, GeoModule luopan/enmity backoff, DebuffModule attempt windows — **must not be shared across characters** |
| `SettingsService` | `Current` — no statics except JSON options; instantiable per tab |
| `ProfileService` | `ActiveName`, `_lastContextKey` — per-tab; its character+job context key already auto-switches each tab independently |

Shared by design (keep one):

- `AddonListener` — buff messages are name-keyed
  (`CUREPLEASE_buffs_<Name>_...`), so one UDP listener can feed every tab's
  `PartyBuffs`. Each engine filters its snapshot to its own roster; only stale
  entries are removed from the shared table, never another tab's live roster.
- `GameProcessLocator` — pure enumeration.
- Serilog — add a per-tab property (character name) so the file log
  distinguishes instances: `{Message}` → `[{Character}] {Message}`.

## Cross-talk problems to solve

1. ~~**Addon cast events are not name-keyed.**~~ **Done** (CurePlease3 addon
   rewrite): v2 sends `CUREPLEASE_casting2_<Name>_<finished|interrupted|
   blocked>` and `CUREPLEASE_confirmed_<Name>` (plus the bare legacy handshake);
   `AddonListener.TryParseCastEvent` / `TryParseHandshake` parse both v1 (caster
   null → broadcast) and v2, and MainWindow filters cast events by the attached
   player name. Remaining for tabs: when the shared listener lands, each tab
   just subscribes with its own name filter — no further protocol work needed.
2. **Profile file collisions.** Two tabs on the same job combo load the same
   JSON file; Save = last writer wins. Policies, cheapest first: (a) warn when
   two tabs hold the same profile name; (b) encourage character-prefixed
   profiles (`Charname_WHM_SCH.json`) — the auto-switch already prefers them;
   (c) later: file-watcher reload. Recommend (a)+(b).
3. **PID uniqueness.** The UI must stop two tabs from attaching the same
   process as PL (two engines driving one character = chaos). Monitored PID
   may legitimately repeat (several tabs watch the same tank).

## Proposed shape

```
MainWindow (TabControl)
 └─ per tab: InstanceView (UserControl; today's MainWindow content moved down)
     └─ InstanceContext (new, ~100 lines)
         GameStateService + CastingService + SettingsService
         + ProfileService + Engine + module set
```

- `InstanceContext` builds exactly what `MainWindow`'s constructor builds
  today; `MainWindow` keeps tab add/remove, the shared `AddonListener`, and
  the PID registry enforcing uniqueness.
- Tab header = character name (from the first good snapshot) + connection
  state. Tab close = `Engine.StopAsync()` → dispose `EliteAPI` pair.
- Monitored combo: keep "any running process", optionally list other tabs'
  characters first.
- The harness (`BardTest`) is unaffected — `GameStateService`'s virtual
  surface and per-instance engines are exactly what it fakes today.

## Effort estimate

| Piece | Size |
|---|---|
| `EliteAPI : IDisposable` + deterministic detach | S |
| `InstanceContext` + `InstanceView` extraction (most of MainWindow's 336 lines move) | M |
| Tab add/close + PID registry + per-tab log context | S |
| Addon protocol v2 (name-keyed cast events) + lua — **shipped** | S-M |
| Profile collision warning | S |

The zero-code alternative remains "run N copies of the exe" (profiles already
auto-switch per character). Tabs buy: one window, shared addon listener, and
cross-instance monitoring without juggling processes.
