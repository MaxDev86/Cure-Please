## Review feedback

### Strengths
- **Engine design:** `Engine.cs` runs one cancellable loop with one immutable `GameSnapshot` per tick. Each module offers scored candidates and the highest score wins. This is easy to reason about and log.
- **Resilience:** Each module call and each tick is wrapped in try/catch, so one failure can't kill the loop. Cancellation is handled correctly.
- **Casting:** `CastingService` uses a `SemaphoreSlim` released in `finally`. That removes the deadlock risk the legacy code had. Timing constants are named and documented, and `ConfigureAwait(false)` is used consistently.
- **Settings:**
  - `SettingsService` uses atomic write-then-rename.
  - It publishes the live settings with a single reference swap.
  - A corrupt file keeps the known-good settings.
  - Settings are versioned and have a migration hook.
- **Structure:** Modules, settings pages and edit models are separated cleanly. XML doc comments explain why things are done, not just what.
- **Multi-instance support:** A PID registry prevents two tabs driving the same character. A single shared UDP listener is bound to loopback only.

### Issues and risks

1. **`async void OnClosing` in `MainWindow.xaml.cs` (line ~296).**
   - Closing is a synchronous event. If it awaits shutdown, the window can close before the shutdown finishes, unless `e.Cancel` is set and the close is re-triggered.
   - Verify that shutdown completes before the process exits.
   - Exceptions in an `async void` handler crash the app.

2. **Thread safety in `Engine`.**
   - `_pausedByZone`, `_wasLoggedIn` and `_lastZoneId` are written on the loop thread. `ResumeFromZone()`, `Stop()` and `PausedByZone` are called from the UI thread.
   - The bool fields aren't `volatile`. `_modules` is a plain `List` with no lock, so `RegisterModule` during a running loop is unsafe.
   - Events such as `Log`, `SnapshotReady` and `ZonePauseChanged` are raised on the background thread. Confirm every subscriber marshals to the dispatcher.

3. **`Engine.Start` / `Stop` lifecycle.**
   - `Stop()` cancels but never disposes `_cts`.
   - A quick `Stop()` then `Start()` can hit `IsRunning` while the old loop is still finishing, and `Start` then silently returns.
   - `StopAsync` should also dispose the `CancellationTokenSource`.

4. **`CastingService` outcome handling.**
   - On `SendFailed`, `result.Elapsed` is only set in `finally`. That's fine, but the `Detail` field is empty.
   - `NotifyCastEvent` stores `(int)kind + 1` as a magic-number protocol. The comment says "0 none, 1 finished..." but the code doesn't use an enum.
   - A stray event from before a cast could be consumed by the next one. The reset to 0 happens before the stationary wait, so events arriving during that wait can be lost or misattributed.

5. **Empty `catch { }` in `Converters.cs` (line 43).** It swallows all exceptions silently. Catch `FormatException` and `InvalidCastException` specifically, or use `double.TryParse`.

6. **Nullable reference types are disabled** (`<Nullable>disable</Nullable>`). Properties like `OwnerModule`, `PrepForSpell` and `AddonListener Addon` can be null with no compiler help. This is a likely source of `NullReferenceException`s.

7. **`requireAdministrator` manifest.**
   - It is probably needed for process memory access.
   - It means every launch triggers UAC, and the app and any WPF drag/drop run elevated.
   - Document this, and keep the attack surface small. The loopback-only UDP bind is good.

8. **UDP addon input.**
   - Any local process can send packets to the loopback port.
   - Check that `AddonListener` bounds message size, validates and parses defensively, and rate-limits or caps roster state. I did not read this file in full.

9. **Settings file handling.**
   - Writing `path + ".tmp"` is fine, but a failed save leaves the `.tmp` file behind.
   - `Load` gives no UI feedback on corruption. Consider backing up the bad file (`.bad`) before the user overwrites it with a save.
   - `Clone` via JSON round-trip is OK but slow if it's called often.

10. **Legacy code is still in the repo.**
    - `legacy\Form1.cs` and `Form2.cs` are about 10k lines, with `Thread.Sleep` and `async void` throughout.
    - They aren't part of the new project. If they're kept only for reference, git history already preserves them.
    - If the legacy importer needs them, document that.

11. **No tests.** The solution has a single project. The scoring engine, modules, `SettingsService` (migration) and `LegacySettingsImporter` are highly testable because the snapshot is immutable.

12. **Hard-coded values and magic numbers.** Examples are `Math.Max(50, ...)` for the tick delay, the 3 s start window, and `Thread`-style timings. Most are named constants in `CastingService`, which is good. The engine's 50 ms floor and the addon event codes should follow the same pattern.

13. **Log strings use `+` concatenation and `ex.Message` only.** In `RaiseLog`, include the exception type. Full details do go to Serilog.

## Suggested improvements

- **Quality gates**
  - Enable `<Nullable>enable</Nullable>`, ideally incrementally per file or folder with `#nullable enable`.
  - Add `<TreatWarningsAsErrors>` (or at least analyzers) and an `.editorconfig`.
  - Add a test project (xUnit) covering module scoring, `Engine` tick decisions with a fake `GameStateService`, and settings migration.
  - Abstract `GameStateService` and `CastingService` behind interfaces so the engine can be tested without a game process.
- **Concurrency**
  - Make the engine's shared flags `volatile` or use `Interlocked`.
  - Guard module registration, or make it constructor-only.
  - Use a typed `CastEventKind` for the addon cast event instead of `+1` encoding.
  - Consider `Channel<T>` or the dispatcher for UI events.
- **Lifecycle**
  - Replace `async void OnClosing` with the cancel-then-close pattern. `Cancel = true`, await shutdown, then close with a re-entrancy flag.
  - Dispose the CTS, the UDP client and `SemaphoreSlim`. Consider `IAsyncDisposable` on `InstanceView` and `Engine`.
- **Architecture**
  - Use dependency injection (`Microsoft.Extensions.DependencyInjection` or Generic Host) instead of `new` in `MainWindow` and `InstanceView`.
  - Move `MainWindow` and `InstanceView` logic into view models (MVVM). The code-behind is large and builds UI such as tab headers in code.
  - Replace the string-based `OwnerModule`, `ActionName` and `TargetName` with typed IDs where practical.
- **Settings**
  - Keep a rolling backup of `settings.json`.
  - Use `JsonSerializerContext` source generation for speed and trimming safety.
  - Validate values after load, for example clamping `TickIntervalMs` and `StaleSeconds`.
- **Observability**
  - Use Serilog structured properties instead of concatenated strings.
  - Add a UI-visible decision trace: the candidate list with scores, and why each was rejected.
- **Repo hygiene**
  - Remove or archive `legacy\`.
  - Keep a README that documents the admin requirement, the addon protocol and the build steps.
  - Add a CI workflow (build and test).
  - Check that the publish profiles (`FolderProfile*.pubxml`) don't contain machine-specific paths.
- **Data catalogs.** `BuffCatalog`, `AbilityCatalog` and `ZoneNames` are large in-code tables. Consider embedded JSON resources so they can be updated without recompiling and tested for duplicates.
