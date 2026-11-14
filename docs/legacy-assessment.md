# Legacy CurePlease 2.0.4.4 — Full Flaw Assessment

Consolidated findings from a full-codebase investigation of `legacy/`
(Form1.cs, Form2.cs, Form2.Designer.cs, CustomClasses.cs, Items.cs,
Settings.cs, Program.cs, WindowerAddon/, AshitaAddon/). Companion document
to `casting-priority.md`, which covers the cure-tier/priority model and is
not repeated here.

Finding types: **Bug** = wrong behavior today; **Design flaw** = works but
fragile/unmaintainable; **Perf** = performance problem.

---

## 1. Threading & Main Loop

1. **`actionTimer_TickAsync` is `async void` with no reentrancy guard**
   (Bug) — `Form1.cs:5337`, 500 ms timer (`Form1.Designer.cs:1504`), never
   disabled while running. When the handler hits `await Task.Delay`, the UI
   thread is released and the next tick starts before the previous finishes.
   Multiple ticks run concurrently, mutating shared fields
   (`CastingBackground_Check`, `castingSpell`, follow/song state) without
   synchronization.
2. **Cast lock can deadlock the bot forever** (Bug) — `CastSpell`
   (`Form1.cs:5166`) sets `CastingBackground_Check = true`; the only unlock
   is `ProtectCasting_DoWork` (`Form1.cs:9681-9716`), which has **no
   `RunWorkerCompleted` handler and no try/catch**. If it throws, the lock
   stays set and the main loop skips all healing (`Form1.cs:5630`)
   permanently.
3. **`JobAbility_Delay_DoWork` would lock JAs forever if ever run** (Bug,
   dormant) — `Form1.cs:9718-9732`: sets `JobAbilityLock_Check = true`,
   sleeps 2 s on the UI thread, and the unlock line is commented out. No
   caller today, but the worker is declared and wired.
4. **No global exception handlers** (Design flaw) — `Program.cs:12-16` has
   no `Application.ThreadException` / `UnhandledException` hooks; with
   `async void` everywhere, any stray exception kills the process.
5. **UI thread frozen for seconds** (Perf/UX) — `Thread.Sleep(17000)`
   during zoning (`Form1.cs:3478-3479`); ~1.6 s of sleeps in the pause
   button (`Form1.cs:3104-3126`, `:7988-8012`). Window goes
   "not responding".
6. **`JobAbility_Wait` / `Item_Wait` use `Invoke(async void lambda)`**
   (Bug) — `Form1.cs:7626-7661`: fire-and-forget continuations; exceptions
   swallowed; lock set synchronously, cleared asynchronously → races.
7. **Shared flags accessed cross-thread without synchronization** (Design
   flaw) — `CastingBackground_Check` read/written from UI thread,
   `ProtectCasting_DoWork`, and `AddonReader_DoWork` with no
   `lock`/`volatile`/`Interlocked`.
8. **Two racing unlock paths for the cast lock** (Bug) — addon
   "interrupted"/"finished" packets (`Form1.cs:9445-9477`) call
   `CancelAsync()` + clear the lock, but `ProtectCasting_DoWork` never
   checks `CancellationPending` and clears the lock again on its own
   schedule. A stale worker can unlock a *newer* cast.
9. **`CastSpell` returns before the spell is cast** (Design flaw) —
   `Form1.cs:5166`: sends `/ma`, sets lock, returns. No confirmation of
   cast start/success/failure.
10. **Dead concurrency primitives** (Design flaw) — `JobAbilityDelay`,
    `JobAbility_Delay`, `CastingCheck_BackgroundTask`, `AutomaticChecks`
    timer never used; `CustomCommand_Tracker` (`Form1.cs:9748-9787`) has an
    *empty* DoWork yet restarts itself forever.
11. **All `await` continuations resume on the UI thread** (Perf) — the
    whole decision loop effectively runs on the message pump; combined with
    overlapping ticks this saturates the UI thread.

**Port direction:** one cancellable background decision loop (long-running
`Task`/`IHostedService`), one action at a time; casting as an awaitable
state machine with timeout + `finally` unlock; thread-safe central game
state; UI updates via binding only; no `Thread.Sleep`, no `async void`.

---

## 2. Buff Upkeep, Job Abilities & Timing

1. **Buff timestamps set on cast initiation, not success** (Bug) —
   `hastePlayer` etc. (`Form1.cs:5202-5270`) set
   `playerHaste[id] = DateTime.Now` immediately after `SendString`.
   Interrupted/out-of-range casts are never retried until the refresh
   window expires.
2. **`TimeSpan.Minutes` instead of `TotalMinutes`** (Bug) —
   `Form1.cs:6901-6941`, `:9135-9171`. `Minutes` is the 0–59 component: a
   buff cast 1 h 04 m ago reads as "4 minutes". Refresh thresholds misfire
   badly past the one-hour mark.
3. **Flurry II gated by the Haste timer** (Bug) — `Form1.cs:6917` checks
   `playerHasteSpan` instead of `playerFlurry_IISpan`.
4. **SCH stratagem charge math is dimensionally wrong** (Bug) —
   `Form1.cs:8495-8571`: `t = recast/60` (minutes) then
   `stratsUsed = t / baseTimer` (baseTimer in *seconds*) → almost always 0 →
   `currentSCHCharges` reads as max nearly always; every
   `currentSCHCharges >= 1` check passes when it shouldn't.
5. **Bard song recast checks compare against the wrong song** (Bug) —
   `Form1.cs:9099`, `:9116`: songs 1 and 2 guard against
   `Last_Song_Cast != song_4.song_name` instead of their own names → skips
   or repeated song 4.
6. **Bard recast `else if` chain retries only one song per tick** (Design
   flaw) — `Form1.cs:9135-9182`: multiple expired songs recover one per
   500 ms tick.
7. **`songs_currently_up1` counts songs on the monitored player, not the
   bard** (Bug) — `Form1.cs:5597`, `:5743-5749`: Troubadour/Nightingale
   decisions use `_ELITEAPIMonitored` buffs instead of `_ELITEAPIPL`.
8. **Buff-wearoff detection requires the addon** (Design flaw) —
   `Form1.cs:4470-4568`: timer resets on missing buffs only happen under
   `if (Form2.config.EnableAddOn)`. Without the addon, dispelled buffs are
   not detected until the configured minutes elapse (compounds with #2).
9. **Protect/Shell tier handling wrong in both directions** (Bug) — PL
   self (`Form1.cs:6064-6138`): any-tier Protect blocks casting the
   configured higher tier (never upgrades). Party (`Form1.cs:6921-6927`):
   time-based only — overwrites higher tiers from other sources, never
   detects dispel.
10. **Accession/Perpetuance sequencing is stateless** (Design flaw) —
    `Form1.cs:6090-6321`: three ticks per combo via
    `if (!plStatusCheck(...)) { JobAbility_Wait(...); return; }`; no
    verification a JA succeeded; retries the same branch forever on
    silence/amnesia.
11. **`JobAbility_Wait` never verifies success** (Design flaw) —
    `Form1.cs:7645-7662`: sends `/ja`, sleeps 2 s, unlocks. Failed JA
    treated as success.
12. **Per-player state in `DateTime[]` indexed by `MemberNumber`** (Design
    flaw) — `Form1.cs:871-1168`: assumes party list ordering is stable;
    party restructure silently corrupts per-player timers.
13. **Divine Seal fires at MPP <= 11** (Design flaw) — `Form1.cs:5653`:
    triggers only when almost no MP remains to benefit from it.

**Port direction:** per-character buff-state objects (dictionary keyed by
stable character ID, not array index); record *landing* time on confirmed
cast finish; `TotalSeconds`-based durations; tier-aware buff tracking;
explicit JA/spell action queue with per-step verification.

---

## 3. Debuff Tracking & Addon Dependency

1. **Party debuff detection is 100% addon-dependent, no fallback**
   (Critical design flaw) — `ActiveBuffs` (`Form1.cs:243`) is written only
   by UDP packets from the Windower/Ashita Lua addon (packet 0x076).
   Without the addon, debuff removal sees only PL + monitored player.
2. **No verification the addon loaded** (Bug) — `Form1.cs:3101-3141`,
   `:7986-8022`: sends `//lua load`, sets `LUA_Plugin_Loaded = 1`; the only
   real confirmation is a `CUREPLEASE_confirmed` packet flipping a label
   color. Silent failure looks healthy.
3. **No staleness detection on debuff data** (Bug) — `Form1.cs:9526-9538`:
   entries have no timestamp/timeout; one lost packet = phantom debuff for
   minutes; addon death = stale "all clear".
4. **Windower vs Ashita zoning handling inconsistent** (Bug) — Ashita
   addon suppresses updates during zoning (packets 0x0B/0x0A); Windower
   addon does not. Bogus debuff lists right after zoning.
5. **Reader locks a copy, not the source** (Bug) — `Form1.cs:4476-4478`:
   `lock (ActiveBuffs.ToList())` does not synchronize with writers locking
   `ActiveBuffs`. Torn reads possible.
6. **UDP protocol: no validation, no auth, errors swallowed** (Design
   flaw) — `Form1.cs:9423-9548`: any local process can inject
   `CUREPLEASE_*` commands; malformed packets vanish silently.
7. **String-encoded buff protocol** (Design flaw) — ASCII encoding,
   `_`-delimited, culture-sensitive `ToLower()` matching
   (`Form1.cs:9434-9438`, `:4487`).
8. **Cast tracking differs addon vs no-addon** (Design flaw) —
   `Form1.cs:5188-5196`: packet-driven unlock vs cast-bar polling fallback
   behave differently (early/late unlocks).
9. **No per-member cleanup on leave/zone/death** (Bug) —
   `ActiveBuffs.Clear()` only on zone/pause; a departed member keeps their
   debuff record.
10. **`BuffStorage : List<BuffStorage>`** (Design flaw) — `Form1.cs:25-30`:
    entity inherits a list of itself; same anti-pattern in `CharacterData`,
    `SongData`, `SpellsData`, `GeoData`, `JobTitles` (`Form1.cs:25-77`).
11. **`removeDebuff` string round-trip, removes one occurrence** (Perf/
    design) — `Form1.cs:3901-3923`: split → `List.Remove` → rejoin →
    `FindIndex`.
12. **PartyBuffs display off-by-one** (Bug, cosmetic) — `PartyBuffs.cs:60-77`:
    comma placement wrong; use `string.Join`.

**Port direction:** data-provider abstraction for debuff sources; addon
becomes optional with handshake + staleness timestamps + per-member roster
reconciliation; typed/versioned UTF-8 protocol (or direct memory reads if
the API exposes party buffs); `ConcurrentDictionary<string, PlayerBuffState>`
with `LastUpdated`.

**Verified boundary (researched 2026-11): alliance-member buffs are
unavailable, period.** The server's only buff-distribution packet is
incoming `0x076` with exactly 5 entries — the 5 other members of the
client's *own party* (Windower `fields.lua:3344-3354`). Alliance member
updates (`0x0DD` / `0x0C8`) carry HP/MP/TP/jobs/zone but no buffs, so the
data is never on the client to be read — by memory scan or packet parse.
Consequences: alliance-wide *healing* is fine (HP data exists);
alliance-wide *debuff detection* is impossible for any tool (this also
means the legacy addon only ever covered 5 members, not the 18 the UI
implies); own-party buffs ARE client-side, so a memory-first replacement
for the addon is viable for the 6-member party scope. The only
alliance-wide option is out-of-band state sharing between multiple bot
instances.

---

## 4. Settings UI & Config Model

**Why the settings window is laggy: ~480 controls** (271 checkboxes,
35 NumericUpDown, 29 ComboBox, 41 custom-painted `GroupBoxEx`, 10 tab
pages) all created up front in `InitializeComponent`
(`Form2.Designer.cs`, 8,772 lines), plus GDI+ custom painting per group box
(`CustomClasses.cs:24-96`) on every repaint.

1. **Static mutable `Form2.config` read from >1,900 sites** (Design flaw /
   thread-safety) — `Form2.cs:1809`; non-modal settings window mutates the
   same object the action timer reads. No isolation, no snapshots.
2. **~1,000 lines of manual control↔config plumbing** (Design flaw) —
   `button4_Click` (`Form2.cs:2375-2864`, ~490 lines) and `updateForm`
   (`Form2.cs:3414-3917`, ~500 lines). New setting = 3 edit sites.
3. **Copy/paste mapping bugs** (Bug) — `Form2.cs:2551`
   (`barspellAccession` ← `barstatusAccession`), `:3619` (`blinkAccession` ←
   `blinkPerpetuance`), `:3489` (`plShell` ← `plProtect`).
4. **32 KB `app.config` is orphaned legacy** (Design flaw) — ~160 dead
   `userSettings` entries; real persistence is hand-rolled XML of
   `MySettings` into a `Settings` folder.
5. **No config versioning/migration** (Design flaw) — flat ~180-property
   `[Serializable]` bag; old profiles silently misbehave on schema change.
6. **Load/Save closes the window** (Bug/UX) — `loadButton_Click` →
   `button4_Click` → `Close()` (`Form2.cs:3391-3410`, `:2862`); `Form1`
   calls `Form2.button4_Click` as a backdoor save.
7. **`updateForm` mutates config as a side effect** (Bug) —
   `Form2.cs:3458-3466`, `:3470-3477`, `:3579-3594`: writes sentinel
   defaults into the model while "refreshing" the UI.
8. **Keyboard shortcuts reversed** (Bug) — `Form2.cs:4211-4225`: Ctrl+S
   loads, Ctrl+O saves.
9. **~400 lines of hardcoded defaults in the constructor** (Design flaw) —
   `Form2.cs:1931-2332`.
10. **Duplicated hardcoded data** (Design flaw) — same ~80-song list pasted
    into 8 combo boxes; job list built by hand (`Form2.cs:1819-1929`).

**Port direction:** MVVM settings with sectioned ViewModels and two-way
binding (CommunityToolkit.Mvvm); JSON persistence with a `Version` field +
migration pipeline; engine consumes an immutable per-tick snapshot, UI
publishes atomically on Save; navigation-shell layout with lazily loaded
sections and `ItemsControl`-bound debuff/song lists. This fixes the lag as
a side effect.

---

## 5. Robustness, Process Lifecycle & API Usage

1. **EliteAPI instances never disposed** (Bug/leak) — `Form1.cs:3018`,
   `:3155`: re-attaching overwrites the old instance; unmanaged handles
   leak.
2. **No game-process watchdog** (Bug) — ticks check only
   `_ELITEAPIPL == null` / `LoginStatus`; game crash → timers spin on stale
   data or throw native exceptions forever.
3. **Party indexing without bounds checks** (Bug/crash) —
   `castingPossible` (`Form1.cs:5112`) and ~10 other sites index
   `GetPartyMembers()[id]` with no `Count` validation.
4. **~143 `GetPartyMembers()` calls per tick** (Perf) — each call re-reads
   process memory and allocates; 500 ms tick → constant interop + GC churn.
5. **Resource lookups dereferenced without null checks** (Bug/crash) —
   `CastSpell` (`Form1.cs:5171`), `HasSpell` (`:355`), `HasAbility` (`:335`):
   typo or missing resource → NRE.
6. **Background workers restart unconditionally on completion** (Bug) —
   `Form1.cs:9371`, `:9557`, `:9786`: permanent failures masked; potential
   restart storms.
7. **`Form1_FormClosing` doesn't stop timers/workers/dispose API** (Bug) —
   `Form1.cs:8350-8380`: crashes during shutdown; lingering hooks.
8. **`hpUpdates_Tick` updates player 0 twice, assumes 18 members** (Bug) —
   `Form1.cs:3767-3774`.
9. **Spell lookup arrays allocated every tick** (Perf) — `Form1.cs:5339-5340`,
   `:6790-6791`.
10. **`GeneratePT_structure` double-fetch + `FirstOrDefault()` → 0**
    (Design flaw) — `Form1.cs:8444-8488`: missing PL misread as "party 1".
11. **Model classes inherit `List<T>` of themselves** (Design flaw) — see
    3.10.

**Port direction:** API instances owned by a disposable service with
process-health monitoring and clean re-attach; one party snapshot per tick
passed to all decision code; safe resource resolution service; orderly
shutdown (cancel services → dispose API → close UDP).

---

## Prioritization Proposal

### P0 — Correctness bugs (heal wrong today; cheap to fix, port must not inherit)
- `TimeSpan.Minutes` → `TotalMinutes` everywhere (2.2)
- Flurry II wrong timer (2.3)
- SCH stratagem charge math (2.4)
- Bard song wrong-name guards (2.5); songs counted on monitored player (2.7)
- Buff timestamps on initiation, not success (2.1)
- Cast-lock deadlock via unhandled `ProtectCasting` failure (1.2)
- Settings mapping bugs: barspell/blink/plShell (4.3); reversed Ctrl+S/Ctrl+O (4.8)
- Already cataloged in casting-priority.md: BuffChecker swapped args,
  Poisona JobChecker, plAttackDown double-gate

### P1 — Architectural foundations (build these first in the port; everything else hangs off them)
- Single cancellable background decision loop replacing timer/`async void`/
  BackgroundWorker soup (1.1, 1.4, 1.6, 1.11)
- Awaitable casting state machine with timeout + guaranteed unlock (1.2,
  1.8, 1.9)
- Per-tick party snapshot; kill the 143-calls-per-tick pattern (5.3, 5.4)
- Settings: MVVM + JSON + versioning + immutable engine snapshot (4.1, 4.2,
  4.5) — also fixes the settings-window lag (4.0)
- Disposable API lifecycle + process watchdog (5.1, 5.2, 5.7)
- Scored-candidate casting engine (see casting-priority.md)

### P2 — Feature robustness (after foundations)
- Per-character buff-state objects: landing-time, tier-aware, dispel-
  detection without addon (2.8, 2.9, 2.12)
- Explicit JA/action queue with per-step verification (2.10, 2.11)
- Debuff provider abstraction; addon handshake + staleness + roster
  reconciliation (3.1-3.5, 3.9)
- Divine Seal trigger redesign (2.13); bard song queue (2.6)
- **Full BRD support** — legacy bard code is partial and buggy (2.5, 2.6,
  2.7) and must be replaced with a proper song engine. Design reference:
  [Obamiwannabuffya: A Bard Guide by Funkworkz (BG-wiki)](https://www.bg-wiki.com/ffxi/Obamiwannabuffya:_A_Bard_Guide_by_Funkworkz)

### P3 — Cleanup (opportunistic)
- Dead workers/timers/OLDBARDCODE removal (1.10, 2.14)
- `app.config` purge (4.4); hardcoded defaults/data dedup (4.9, 4.10)
- UDP protocol hardening (3.6, 3.7); `BuffStorage`/`List<T>`-inheritance
  model cleanup (3.10, 5.11)
- PartyBuffs comma bug (3.12); `hpUpdates_Tick` duplication (5.8);
  per-tick array allocations (5.9)
