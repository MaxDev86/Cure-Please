# Buff Upkeep redesign — white magic spell analysis

**Status: shipped (PL-only v2).** Per-member buffs shipped separately as the
session-based Player Buffs feature (main-window "Buffs…" buttons →
`PlayerBuffAssignments` + `PlayerBuffModule`, runtime-only, name-keyed) — see
"Player Buffs (shipped)" below. The shipped Buff
Upkeep page is self-buffs only, with Accession as an optional prep JA that lets
the self-cast also hit party members in range — see "Shipped settings model" below.

Drives the Buff Upkeep page rework. Scope: white-magic buffs (and the cures they
intersect via Accession). Classification per the
[BG-wiki Accession page](https://www.bg-wiki.com/ffxi/Accession) and in-game job
spell lists. Durations are stock (no Composure/Perpetuance/gear) and a few need
in-game verification — marked (verify).

## Natively AoE — Accession unnecessary (exclude from per-target upkeep)

| Spell | Notes |
|---|---|
| Curaga I–V | Handled by CuragaModule |
| Protectra I–V, Shellra I–V | WHM; our Accession path emulates these |
| Bar-*ra (Barfira, Barblizzara, ...) | WHM/RDM AoE bar-element |
| Esuna | Self-centered AoE, needs Afflatus Misery — DebuffModule |

## Single-target, Accession-compatible

| Spell | Default duration | Jobs | Notes |
|---|---|---|---|
| Protect I–V | 30 min | WHM/RDM/SCH | V from SCH 90 — the Protectra V emulation |
| Shell I–V | 30 min | WHM/RDM/SCH | as above |
| Regen I–V | ~1–2 min/tier stock; enhancements (WHM merits/gear) reach 4–5 min; 10+ min under Perpetuance (user-confirmed) | WHM/SCH/RDM | duration varies wildly by build — the per-entry DurationMinutes fallback carries this; default low, user tunes up |
| Refresh (I) | 2.5 min | RDM 41 | II/III are NOT compatible |
| Adloquium | 3 min (verify) | SCH | SCH main |
| Phalanx (I) | 3 min | RDM 33 | Phalanx II NOT compatible (RDM main) |
| Stoneskin | 15 min | WHM 28/RDM 34/SCH 44 | |
| Blink | 15 min (verify) | WHM/RDM/SCH | |
| Aquaveil | 10 min | WHM/RDM/SCH | |
| Bar-* (single: Barfire, Barstone, ..., Barblind, Barvirus, ...) | ~8 min (verify) | WHM/RDM | many entries — group by family in UI? |
| Enspells (Enfire, Enstone, ...) | 3 min | RDM | self-only in practice |
| *-storm (Firestorm, ...) | 3 min | SCH | self/party |
| Animus Augeo / Animus Minuo | 1 min (verify) | SCH | |
| Invisible, Sneak, Deodorize | EXCLUDED (user decision) | — | serve no purpose in active combat; not in the catalog |
| Embrava | 5 min (verify) | SCH main (taboo) | |
| Erase, -na (Poisona, Paralyna, Silena, Blindna, Cursna, Viruna, Stona) | instant | WHM/SCH | reactive removal, NOT upkeep — DebuffModule |
| Cure I–IV | instant | WHM/RDM/SCH/PLD | reactive, NOT upkeep — CureModule |

## Single-target, NOT Accession-compatible

| Spell | Default duration | Jobs | Notes |
|---|---|---|---|
| Haste I | 3 min | WHM 40/RDM 48 | |
| Haste II | 3 min | RDM 96 (main) | |
| Refresh II / III | 2.5 min | RDM 82/99 (main) | user-noted |
| Boost-STR/DEX/... | 5 min (verify) | WHM main 81–99 | user-noted |
| Gain-STR/MND/... | 5 min (verify) | RDM main 81–99 | user-corrected (was WHM) |
| Flurry I/II | 3 min | RDM main | |
| Phalanx II | 4 min (verify) | RDM main | single-target despite the name |
| Cure V/VI | instant | WHM main | Accession-incompatible (BG-wiki note) |
| Sacrifice | instant | WHM main | incompatible |
| Auspice | 5 min (verify) | WHM main | not on the compatible list |
| Reraise I–IV, Raise I–III/Arise | — | various | excluded by the game |

## Shipped settings model (BuffSettings v2)

**Scope change (user decision): Buff Upkeep is PL-ONLY** — buffs kept on the
character running Cure Please, mirroring legacy's split between the enhancing tab
(PL) and the per-player buff menus. Per-member party buffs (and the Accession
party Protect/Shell flow, whose engine work was built and then pulled from this
page) live in the Player Buffs feature below.

Entries are backed by a static catalog (`Core/BuffCatalog.cs`; status ids, stock
durations, and job levels from Windower's spells.lua):

```csharp
public sealed class BuffEntry
{
    public string Spell { get; set; }              // "Protect V"
    public bool Enabled { get; set; }
    public double DurationMinutes { get; set; }    // fallback timer, prefilled from catalog
    public bool UseAccession { get; set; }         // SCH main or sub, catalog-gated
    public bool UsePerpetuance { get; set; }       // SCH main only, catalog-gated
}
// BuffSettings also has GroupSelection: group -> chosen spell while the group is
// disabled (an enabled entry identifies its group's spell by itself).
```

Rules the catalog enforces:
- `AccessionCompatible` per spell: the BG-wiki Accession list (Cure I–IV, Protect/
  Shell I–V, bar-*, tier-1 enspells, storms, Regen, Refresh I, Phalanx I,
  Stoneskin, Aquaveil, ...). Haste, Refresh II/III, Cure V/VI are out, as are
  spells only another main job provides while subbing SCH (En-II spells,
  Enlight/Endark, Baramnesia — BG-wiki note; user-confirmed for Baramnesia);
  tier-1 enspells ARE in (SCH self-casts, party in range gets the enspell —
  user-confirmed). Flurry is not on the list.
- `PerpetuanceCompatible` per spell: SCH-native, or WHM/RDM/PLD level ≤ 49 (the
  sub-job cap under SCH main). Computed from spells.lua level tables — e.g. Haste
  (WHM 40) and Flurry (RDM 48) ARE compatible via SCH/WHM and SCH/RDM 98;
  Phalanx II (RDM 75), Refresh II/III, En-II spells, Baramnesia, Boost-*, Gain-*
  are NOT.
- Mutually exclusive families: BarElement, BarStatus, Enspell (incl. Enlight/
  Endark), Storm — the engine never casts over a different buff of the same family.
- UI-level exclusivity: only one Gain-* row, one Boost-* row, one enspell row
  (any element incl. Enlight/Endark), one storm row (any element), one Protect
  row (Protect OR Protectra), one Shell row (Shell OR Shellra), one bar-element
  row (Bar (element) OR Bar-ra (element)) and one bar-status row (Bar (status)
  OR Bar-ra (status)) can be enabled at a time — the game only allows one of
  each anyway. (Gain-STR and Boost-STR share status id 119, so the engine's
  family check can't separate them — hence UI-side.)
- Notes carry job/level info: Boost-* are WHM main 81–99, Gain-* are RDM main
  81–99, Enlight is PLD, Endark is DRK.
- Job gating stays runtime (KnowsSpell/MeetsSpellLevelRequirement); the UI lists
  everything and the engine skips what the current combo can't cast.

## UI shape (shipped)

One row per GROUP with a dropdown of the group's spells: Protect, Shell, Haste,
Regen, Refresh, Phalanx, Flurry, Bar (element), Bar (status), per-stat Boost/Gain,
per-element enspells (Fire..Water + Enlight/Endark) and storms. Enspells/storms
group by ELEMENT, not tier: element is the tactical per-fight choice while tier is
a potency axis inside the row (same as Protect I→V), and tier-grouped rows would
let two enabled rows of the same element fight over the single enspell slot. The
chosen spell persists via `BuffSettings.GroupSelection`; switching spells carries
Enabled/Accession/Perpetuance over (dropped when incompatible) and resets minutes
to the new spell's stock duration.

### Filter row (shipped)

Above the grid: **Main/Sub job dropdowns + level boxes** (prefilled from the
attached character, Any/Any when detached), a **search box** (matches group name,
member spells, notes), and an **Enabled only** toggle. All three AND together and
are view-only — nothing is persisted and everything is off by default. The job
filter shows a row when any member spell is castable by main (level ≤ main level)
or sub (level ≤ sub level); levels come from `BuffCatalogLevels.cs` (generated by
`tools/gen_buff_levels.py` from spells.lua — levels >99 are JP-gated and count as
main-job-99-only). Toggling a row's On checkbox does not re-filter until the next
filter change, so a row never vanishes mid-click.

The job filter also trims each row's spell **dropdown** to the castable tiers
(e.g. WHM99/SCH59 no longer offers Regen V). A stored choice (explicit selection
or enabled member) is always kept in the list so the view filter never rewrites
the profile; the implicit first-member default is not protected — instead the
row displays the first castable tier, view-only, until the user picks something.

## Accession (SCH main or sub, shipped)

Accession (SCH 40, White Grimoire stratagem) makes the next single-target white
magic spell hit all party members in range. On an enabled, compatible entry the
module offers the Accession JA first and holds the cast until the buff lands
(10s verify window), then self-casts — whoever is in range gets the buff (no
per-member range check, per user decision). This covers the legacy
`accessionProShell` flow: the importer flags the chosen Protect/Shell entries.

## Group -na removal (Yagrush / Accession / Divine Seal, shipped)

When N+ observable members share the same ailment (Debuff Removal page,
default 2, below 2 = off), one cast clears them all. The mechanism is picked
automatically, cheapest first:

1. **Yagrush equipped** — always-on Divine Veil (100%, no Divine Seal needed,
   per [BG-wiki](https://www.bg-wiki.com/ffxi/Yagrush); Erase counts as a
   status-removal spell — user-confirmed). Detected from the live equipment
   table by item id (Windower items.lua 18993–21078, every leveled version;
   the Incomplete 18973 lacks the effect). No JA spent: the plain cast AoEs.
2. **Accession** (SCH main or sub, toggleable) — 1 stratagem charge; the JA
   is offered and the group cast held until the buff lands (10s verify
   window, individuals suppressed while pending). Own party only.
3. **Divine Seal** (WHM main 50+ only — the AoE comes from the Divine Veil
   *trait*, which /WHM does not get; toggleable) — free, but shares the
   10-minute recast with the critical-cure doubling below, so it is
   suppressed while a cure target is at/below the critical HPP.

With no mechanism available the plain per-member removals stand.

**Addendum: White gate (SCH main).** Every -na/Erase spell is Addendum-gated
([BG-wiki](https://www.bg-wiki.com/ffxi/Addendum:_White)) while spells.lua
lists them with SCH levels — the level gate alone would attempt casts that
fail in-game. On SCH main (when the sub job does not provide the spell
natively — SCH/WHM gets -na from /WHM), DebuffModule requires the Addendum:
White buff and preps the JA first (Light Arts + 1 charge), holding the
removals meanwhile. The capability audit warns when a SCH main has removals
enabled without the addendum upkeep configured.

## Divine Seal cure doubling (shipped)

Separate from the AoE use: Healing page toggle, WHM 15 so it works from a
sub job. When the best cure target is at/below the critical HPP (default
25%), Divine Seal is offered above the cure (same prep pattern as Rapture/
Majesty) and the doubled cure follows next tick. Legacy `divineSeal` (fired
by PL MP) maps here.

## Cure-side Accession (SCH main or sub, shipped)

The Curaga module's second party-heal mode (Healing page, legacy
`accessionCure`): when the injured count meets the Curaga threshold on SCH
main or sub, the module preps Accession and casts **Cure I–IV** on the
lowest-HPP member instead of the native Curaga line — an Accessioned Cure IV
out-heals Curaga per target and costs a stratagem charge instead of a
higher-tier spell slot. Same sequencing as the buff side: JA first, cast held
until the buff lands (10s verify window, no premature Curaga), then the cure.
Falls back to the Curaga line when no charge is free, White Grimoire is down,
or no compatible cure is castable; while Accession is up a native Curaga is
never fired. Reuses the Curaga threshold/member-count settings and scoring.

A separate opt-in allows this path when the PL is outside the monitored party.
The cure targets the lowest-HPP reachable member rather than `<me>`: Accession
extends a compatible spell cast on an outside player to that target's party.
Native Curaga has no cross-party fallback. Imported legacy `accessionCure`
profiles enable this option to preserve their monitored-party behavior.

Cure V/VI are not Accession-compatible (BG-wiki), so two guards keep the
charge from being wasted: the party-heal cast resolves from
`SpellBook.AccessionCureTierNames` (Cure IV down), and the single-target cure
module caps its tier resolution at Cure IV whenever the PL's Accession buff
is up — any cure the engine picks then still spreads to the party.

## Player Buffs (shipped, session-only)

Per-member buff assignments live **outside** the profile: legacy never persisted
its per-slot `autoXEnabled` arrays, and parties change too often for saved
assignments to pay off (user decision). The main window has a "Buffs…" button on
every party/alliance row:

- **PL row** deep-links to Settings → Buff Upkeep (`SettingsWindow` initial-page
  parameter) — self-buffs stay profile settings, one owner per target. Two
  managers of the same buff would downgrade each other (presence is checked by
  status id, not spell) and fight over refresh timers.
- **Other rows** open `PlayerBuffsPopup`: a checklist of the catalog's
  `PartyTargetable` buffs (targets mask from Windower spells.lua — 43 of the
  116 catalog spells qualify; bar-singles/enspells/Boost/Gain/Phalanx are
  self-only in retail, -ra spells are self-centered AoE).

Assignments are keyed by character NAME (not legacy's slot index — roster
reshuffles renumber slots mid-session), stored in `PlayerBuffAssignments`,
cleared on attach. `PlayerBuffModule` casts them on any active same-zone party
or alliance member (single-target spells reach cross-party), scoring just above
Buff Upkeep (`BuffWeight + 2`). Presence degrades by source: monitored
attachment → addon buff report → unobservable targets recast on the stock
duration minus 30 s (timer mode, like BRD enemy songs). Failed casts back off
15 s; family exclusivity is honored when the target's buffs are readable.

## Party -ra spells (shipped on Buff Upkeep)

Protectra/Shellra I–V, the six bar-element -ra and eight bar-status -ra spells
are self-cast, natively AoE (10' around the PL) — they live on the Buff Upkeep
page as four more group rows ("Protectra", "Shellra", "Bar-ra (element)",
"Bar-ra (status)"). No Accession flag (pointless on a native AoE) and no engine
changes: they grant the SAME status ids as the single-target versions (Protectra
-> 40, Barfira -> 100, ...), so presence checks, family exclusivity and recast
timers treat singles and -ra interchangeably — a Protectra row is satisfied by
Protect V and a Barfira row won't be cast over Barblizzard. Perpetuance follows
the SCH-access rule (WHM sub cap 49): Protectra I–III, Shellra I–II and all
bar-ra except Baramnesra (WHM 78 main) can be Perpetuanced; Protectra IV/V and
Shellra III–V cannot. Enabling both a single and its -ra row is harmless — the
first cast satisfies both.

## Coverage repair (shipped)

The two party-wide failure modes — a member outside the 10' AoE when the
-ra/accessioned cast went off, and a member dispelled mid-cycle — are repaired
by `BuffUpkeepModule.TryOfferCoverageRepair` while the PL still has the buff.
Detection needs the addon's per-member buff feed (no addon -> no repair, by
design); settle window 6 s after a confirmed landing before misses are judged,
then the usual 15 s attempt backoff applies.

- **Protect/Shell lines** (Protectra row, or a Protect/Shell single running
  under Accession): surgical single-target patch on the missed member with the
  highest castable single — party-targetable to 21', spends no stratagem and
  doesn't refresh the members who still have it. When a patch exists the AoE
  is never recast for coverage.
- **Bar- lines**: singles are self-only, so the AoE must be re-run — a plain
  -ra recast when a Bar-ra row is configured, or a FRESH Accession charge +
  self-cast when the bar is an accessioned single (offered only with a
  stratagem free; this is the costly case — one missed member can consume a
  charge). At least one missed member must be inside 10'.
- Own party only (alliance members can't receive -ra/Accession buffs); dead,
  other-zone, and addon-invisible members are skipped. Repair candidates score
  just below the PL's own missing-buff cast, so a PL recast (which re-covers
  everyone in range anyway) wins the tick.

## Perpetuance (SCH main only, shipped)

Perpetuance (SCH 87, White Grimoire stratagem) extends the duration of the next
enhancing spell: 2x stock, up to 2.65x with hands gear — confirmed per
[BG-wiki](https://www.bg-wiki.com/ffxi/Perpetuance); the multiplier is a Scholar
page setting pre-set from an inventory scan (see Open questions #4). It CANNOT
come from a sub job. On an enabled, compatible entry the module offers the
Perpetuance JA first and holds the cast until the buff lands (10s verify window),
then casts; the landing records the extended duration (entry minutes x multiplier)
so the timer fallback respects it. Accession + Perpetuance combine on SCH main
(Accession offered first, then Perpetuance).

## Open questions — resolved

1. **Bar-spells (decided): one bar-ELEMENT and one bar-STATUS per player, enforced.**
   The catalog keeps the 16 individual entries (each has its own status-effect id)
   but tags each with a family: `BarElement` or `BarStatus`. The engine enforces
   exclusivity per target: a bar-* spell is only offered when the target has NO
   other buff of the same family up (recasting a different one would just
   overwrite — wasted MP and a silent settings fight). UI groups the two families
   into collapsed sections so the page stays readable.

2. **Regen (decided): keep it in the catalog.** Duration is build-dependent —
   ~1–2 min stock, 4–5 min with WHM enhancements, 10+ min under Perpetuance. No
   special modeling: DurationMinutes stays user-editable per entry and Perpetuance
   casts record the extended duration (see below); the Regen row just ships a
   conservative default.

3. **Sneak / Invisible / Deodorize (decided): excluded from the catalog entirely.**
   No combat purpose, and their random durations made timer fallback unreliable
   anyway.

4. **Perpetuance multiplier (decided): hybrid — dropdown pre-set from an inventory
   scan.** Base is 2x ([BG-wiki](https://www.bg-wiki.com/ffxi/Perpetuance));
   hands gear modifies it: Savant's Bracers +1 = 2.25x, Savant's +2 / Arbatel
   Bracers = 2.50x, Arbatel +1 = 2.55x, +2 = 2.60x, +3 = 2.65x. Design:
   - Scholar page gets a "Perpetuance duration" dropdown listing the multiplier
     tiers (2.0 / 2.25 / 2.50 / 2.55 / 2.60 / 2.65).
   - On attach, the engine scans owned items (`GameStateService.GetOwnedItemIds`)
     for the best bracers owned and pre-selects the matching tier. Item ids can
     be resolved by name through the resources API at runtime — no hardcoded ids.
   - Edge case (user-noted): bracers sitting on a **storage slip** are invisible
     to the inventory scan, so the dropdown stays user-overridable; the scan only
     ever picks the initial value, it does not lock the field.
   - Equipped-but-not-owned detection isn't needed: owned covers equipped (the
     scan includes equipped items already).

## Stratagem intent registry (shipped)

The legacy flaw: Accession prepped for a buff gets eaten by an unrelated cure
before the buff cast fires — the buff does not know what it was cast for, and
legacy had no cross-module coordination at all.

`Core/StratagemIntents.cs` is an engine-owned claim registry. Prep JA candidates
carry `PrepBuff` (the "next spell" status effect) and `PrepForSpell`; the engine
records a claim (buff, owner module, intended spell) when the JA executes
successfully and suppresses centrally each tick:

- other modules' spells that would consume the claimed buff
  (`StratagemIntents.SpellConsumes` maps spell -> consumed buffs: Accession =
  Cure I–IV + Accession-compatible catalog buffs + -na/Erase; Perpetuance =
  compatible enhancing buffs; Rapture/Divine Seal/Majesty = cure lines, Majesty
  also Protect/Shell singles);
- other modules' prep JAs for the same buff (a second Accession on top of a
  pending one just burns a charge);
- the owner's OWN consuming spells that are not the intended spell (BuffUpkeep
  claimed Accession for Protect V: its missing Regen entry waits).

Emergency override: cures at/below `Cures.IntentEmergencyHpPercent` (Healing
page, default 25) carry `ActionCandidate.IsEmergency` and bypass suppression —
life beats buffs; the claim owner re-preps next cycle, costing a charge. Same
tradeoff as legacy, but only in genuine emergencies.

Divine Seal is special: cures are never blocked by a DS claim (the life-saver
use wins the shared 10-minute recast); the claim still suppresses competing DS
prep JAs, and the Debuff module already yields to critical cures before
prepping.

A claim clears when the buff is observed up and then gone (consumption), the
10s TTL expires (same window as the per-module JA verify), or on zone. An
unseen claim survives pruning on TTL alone (JA packet lag). Claim recording is
logged ("claimed by \<module\>").
