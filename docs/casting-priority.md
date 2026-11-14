# Casting Priority — Legacy Assessment & Redesign Proposal

Analysis of the legacy WinForms CurePlease 2.0.4.4 casting-priority machinery
(`legacy/Form1.cs`), and the proposed replacement for the WPF port.

## How the legacy priority works

**One action per tick, fixed evaluation order.** The main action timer
(`Form1.cs:5760` onwards) runs this sequence every tick. A global
`CastingBackground_Check` lock ensures only one spell is ever in flight.

1. PL silence item check (Echo Drops etc.)
2. PL self-cure — only if out-of-party healing is on and PL isn't in the party
   (`Form1.cs:5838`)
3. **Curaga pass** — counts party members under `curagaCurePercentage`; if
   >= `curagaRequiredMembers`, fires `CuragaCalculatorAsync` on the
   lowest-HPP one (`Form1.cs:5887`)
4. **Single-target cures**, in this order (`Form1.cs:5900`–5930):
   - monitored target, if `enableMonitoredPriority` and under
     `monitoredCurePercentage` — *exclusive*: everyone else is skipped that tick
   - high-priority (checked) members under `priorityCurePercentage`,
     lowest HPP first, `break` after first
   - everyone else under `curePercentage`, lowest HPP first, `break` after first
5. `RunDebuffChecker()` — full party debuff sweep (`Form1.cs:5934`)
6. PL/party buff upkeep (barspells, haste, etc.)

**Tier selection** (`CureCalculator` / `CureCalculator_PL`,
`Form1.cs:3925`–4045): a hardcoded VI → V → IV → III → II → I if/else chain.
`HP_Loss` is *estimated* from HPP (`HP*100/HPP - HP`). Each tier is gated by
its enabled flag, `HP_Loss >= cureNamount`, a hardcoded MP floor
(227/125/88/46/24/8), `HasSpell`, `JobChecker`. The highest tier whose
threshold is met wins.

**Tier fallback** (`CureTiers`, `Form1.cs:3232`): if the chosen tier is on
recast or not known, it steps down one tier (`Undercure` config) or up one
tier (`Overcure` config, optionally restricted to high-priority targets via
`OvercureOnHighPriority`). Returns the string `"false"` as a sentinel.

## Where the legacy model is broken

1. **Debuff priority is inverted by damage size.**
   `PrioritiseOverLowerTier` only injects `RunDebuffChecker()` inside the
   Cure III/II/I branches (`Form1.cs:3958`, `:3967`, `:3976`). If someone
   takes a *big* hit (Cure IV+ territory), debuffs are never checked that
   tick — the worse the damage, the lower the debuff priority. Backwards for
   Doom, Paralysis, etc.
2. **Curaga pre-empts emergencies.** The Curaga pass runs before
   single-target cures, so a 3-person moderate-damage Curaga can lock the
   cast while a tank at 15% waits a full cast cycle.
3. **Monitored priority is a hard veto, not a weight.** When enabled it
   starves the rest of the party entirely — no threshold comparison, just
   "monitored first, always".
4. **Priority is ordinal, not scored.** First-match-`break` chains
   everywhere; there is no comparison of "who needs it most", only "who is
   first in this fixed order and past a static threshold".
5. **HP_Loss is an estimate from HPP** and the MP floors are magic numbers
   baked into code, not derived from spell data. Changing a tier's cost means
   editing six branches in two duplicated functions — `_PL` and party
   versions are copy-paste twins, a maintenance trap.
6. **The cast lock is the real arbiter.** `RunDebuffChecker()` "winning"
   over a pending cure only works because `CastSpell` silently no-ops when
   `CastingBackground_Check` is set (`Form1.cs:5168`). Priority is an
   emergent side-effect of a mute function, not an explicit decision.

## Legacy bugs to fix in the port

Found during the FFXI.Memory / EliteMMO.API verification work. All present
in 2.0.4.4 under both the original and the rebuilt DLL.

1. **Swapped `BuffChecker` args** — `BuffChecker(0, 418)` at
   `Form1.cs:4105` and `:4301`. Signature is `BuffChecker(buffID,
   checkedPlayer)`; the intent was `BuffChecker(418, 0)` (Afflatus Misery
   check). Result: Amnesia → Esuna never fires.
2. **Wrong job check in the Poisona branch** — `Form1.cs:4305` checks
   `JobChecker("Erase")` instead of `JobChecker("Poisona")` for the
   monitored-player poison removal.
3. **`plAttackDown` double-gating** — the PL-section Erase branches
   (Bind/Burn/Dia/etc.) require their own config flag *and* `plAttackDown`.
   Decide deliberately in the port whether `plAttackDown` is the master
   Erase toggle.
4. NOT a bug: `plStatusCheck`'s `.Cast<StatusEffect>()` works correctly with
   the FFXI.Memory `StatusEffect` enum (verified live).

## Proposed engine for the WPF port

Replace the tier chains with a **per-tick candidate-scoring pass**:

- Each tick, build a list of candidate actions from *all* categories:
  single-target cures (per member, per available tier), Curaga (when member
  count justifies), debuff removals (per member per ailment), buff upkeep,
  items.
- Score each candidate:
  - cures by `HP deficit / potency` and MP efficiency, weighted by member
    priority class (monitored > high-priority > normal — as *weights*, not
    vetoes)
  - debuff removals by severity class (Doom/Paralysis/Petrify high, -na
    ailments medium, stat-downs low) scaled by urgency
  - buff upkeep low, only when idle
- Apply hard filters first (range via `castingPossible`, MP, recast, job,
  cast lock), then pick the single highest-scoring candidate and cast it.
  One gate, one decision point.
- Config becomes per-category weights + thresholds instead of interleaved
  booleans like `PrioritiseOverLowerTier`; "debuffs before small cures only"
  becomes an explicit severity-weight setting the user can tune.
- Spell tier choice moves to a data-driven lookup (spell table with
  cost/potency from resources) instead of the `CureTiers` ladder — pick the
  most MP-efficient tier that covers the deficit, with undercure/overcure as
  a fallback policy flag.

### Consequences

- Kills the duplicated `_PL` / party calculators.
- Makes priority inspectable: the engine can log "chose X over Y, score
  42 vs 38".
- Fixes the inverted debuff priority as a consequence of the design rather
  than a special case.
