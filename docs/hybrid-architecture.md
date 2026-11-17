# Hybrid architecture — Windower addon + native memory, by design

Status: **current architecture, deliberate.** Cure-Please reads game state through
FFXI.Memory (part LuaCore-anchored, part FFXiMain-anchored) and exchanges events
and actions with the game through the CurePlease3 Windower addon. This doc is
the design rationale and the long-term shape. The offset-level detail lives in
FFXI.Memory/docs/windower-dependency.md (dependency map + removal plan) and
FFXI.Memory/docs/maps.md (offset provenance).

## The decision

"Hybrid" means each data flow uses the strongest channel available for it:

| Flow | Channel | Why |
|---|---|---|
| State reads (HP/MP, buffs, party/alliance, recasts, master levels, …) | **Memory** via FFXI.Memory | Polling is continuous; lua→app latency on every tick is the net-lag we want to avoid. Reads are idempotent — a stale byte fails safe. |
| Event push (cast outcomes, per-target landings, chat feed) | **Addon → UDP** (127.0.0.1:19771/19770) | Events are inherently push, not poll. The addon sees them the frame they happen; memory would need high-rate polling and still miss transients. Name-keyed protocol v2 (multi-instance safe). |
| Action commands (`/ma`, `/ja`, items) | **App → game input** via Windower shared memory (SendString) | The client executes them as if typed; server sees normal player input. |
| In-game alerts to the user | **App → `//cp3 log` → `add_to_chat`** | Client-side chat-log write only. Never `/echo` — that round-trips the FFXI server (outgoing chat packet 0x0B5). |

## What we verified before committing to this

- **No detours anywhere in the lineage.** The 2020 EliteAPI was pure
  ReadProcessMemory/WriteProcessMemory on data structures (full site table in
  Dism getter_semantics.md); its single code-execution case (SetTarget's
  remote-thread thunk) was replaced in FFXI.Memory by a direct target-index
  write. Windower's Hook.dll detours; neither EliteAPI nor we ever did. A
  native-reads future is therefore an offset-mapping exercise only — no hook
  infrastructure to rebuild.
- **2020 EliteAPI's fixed offsets went stale** — the failure mode that pushed
  us to LuaCore anchoring. Every native offset we add must be signature-scanned
  or live-verifiable, never a bare constant without provenance.
- **Master levels are natively readable** (u8 array at FFXiMain+0x9CE2C0,
  job-id indexed; live-verified on 3 clients 2026-11-16) — proof that
  post-2021 client additions are still findable in FFXiMain without Windower.
- **The game reports effective sub-job level** (ML-raised cap baked into the
  party/player structs), so consumers never need ML math for level gates.
- **Alliance layout live-verified**: 18-slot party struct, slots 0-5 own
  party, TargetIndex = member's own entity index (legacy docs were wrong).

## Phases

**Phase 0 — now (Windower required).** Player struct, bags, recasts, masks are
LuaCore-anchored; commands via Windower MMF; events via the addon. Ship
Cure-Please in this state.

**Phase 1 — native reads.** Port the LuaCore-anchored reads to sig-scanned
FFXiMain structures, in the order listed in windower-dependency.md (buffs →
recasts → masks → bags). Each port: sig-scan + live probe verification on
multiple clients + provenance entry in maps.md. FFXiMain offsets break on
monthly client updates — that is the accepted trade, mitigated by signature
scans and the SmokeTest harness; LuaCore reads remain as fallback until a port
proves stable across a client update.

**Phase 2 — native SendString.** The client has a native text-input buffer (the
2020 EliteAPI wrote to it). Until this lands, *casting* stays Windower-only even
with all reads native. This is the true cut-over gate for a Windower-free
build.

**Phase 3 — provider split.** Introduce a provider interface behind GameMemory
(Windower / native / maybe AshitaCore) only when a second provider actually
exists — no speculative abstraction before that.

**Stays event-driven regardless**: cast outcomes, landing reports, chat feed.
Even fully native reads can't replace a push channel for these; on a bare
client this becomes a small injected/shim component or an Ashita addon — the
protocol (name-keyed, UDP) stays identical.

## Risks and honest costs

- **Monthly client updates** can break every FFXiMain anchor at once. Windower
  absorbs that treadmill today; going native means owning it. Mitigation:
  signature scans (not bare offsets), SmokeTest verification, and keeping the
  LuaCore path compiled in as fallback until a native port survives an update.
- **LuaCore layout fragility** (accepted today): ctx sub-struct offsets are
  fixed values; if LuaCore changes, re-derive from its `get_*` function bodies
  (Dism luacore_re/ has the access-site dumps).
- **Two channels to keep alive**: memory anchors AND the addon protocol. The
  protocol is versioned (v2 name-keyed); parsers accept v1 fallbacks, so old
  addon/new app combinations degrade gracefully.

## Related docs

- FFXI.Memory/docs/windower-dependency.md — dependency map + removal plan (offset level)
- FFXI.Memory/docs/maps.md — offset provenance, live-verification notes
- docs/multi-instance-design.md — name-keyed protocol rationale, tabbed future
