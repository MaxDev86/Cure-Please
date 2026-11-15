# GEO Support — Design Notes for the Port

The standing design rule applies (see bard-design-notes.md): Cure-Please
sends `/ma` / `/ja` commands and reads memory; it never manages equipment.
GEO has no gear-gated spells the way BRD does (no "Honor March" problem) —
Handbell choice is potency-only, so no ownership model is needed.

Legacy state: GEO code in `legacy/Form1.cs` works but carries the usual
legacy flaws: blind 1.5 s `JobAbility_Wait` JA sequencing, a WinForms
`FullCircle_Timer` doing game decisions off-tick, an `EclipticStillUp`
flag plus a second timer re-deriving what buff memory already says, and a
dual-purpose `LuopanSpell_Target` that is BOTH the character a beneficial
luopan is centered on AND the enemy name used for engage checks /
offensive placement (via `specifiedEngageTarget`). The port keeps the
config concepts and the detection sources, and drops the timer soup.

## Mechanics the module implements

- **Indicolure (`Indi-*`)**: self-centered aura, one at a time; casting a
  new Indi replaces the old one. While any Indi is up the GEO carries buff
  **612 (Colure Active)** — readable in the player's own buff list every
  tick, so upkeep is purely presence-based (no duration estimates: Indi
  lasts until replaced/dispelled/death).
- **Geocolure (`Geo-*`)**: summons a **Luopan** at the target's position;
  the luopan radiates the effect. One luopan at a time; it IS the GEO's
  pet (`Player.PetIndex != 0`, `Pet.HealthPercent >= 1`). Luopans have HP,
  lose it over time and to AoE damage, and die — detection is pet-based,
  not timer-based.
  - Beneficial Geo-* spells target a party member (luopan spawns at their
    feet; legacy: resource `ValidTargets == 5`).
  - Offensive Geo-* spells (Geo-Frailty, Geo-Torpor, ...) target an enemy.
- **Job abilities** (all verified against Windower `res/buffs.lua`):
  - **Blaze of Glory** (buff 569) — boosts the NEXT Geocolure; the luopan
    gets halved HP. Fire immediately before the drop, verified.
  - **Ecliptic Attrition** (buff 516) — boosts the luopan's effect, drains
    its HP faster. Only sensible on a healthy luopan (legacy: >= 90%).
  - **Dematerialize** (buff 518) — luopan avoids damage briefly.
  - **Life Cycle** — sacrifice own HP to heal the luopan (legacy: pet
    5–30%, self HP >= 90%).
  - **Full Circle** — destroys the luopan and refunds MP. Legacy use:
    reclaim a luopan that ended up >= 10 yalms from where the fight is
    (off-position luopans buff nothing), so it can be re-dropped.
  - **Entrust** (buff 584) — the next Indicolure cast lands on a single
    party member at increased potency instead of self-auraing. Consumed
    by the cast, so "cast entrusted spell while 584 is up" naturally
    fires exactly once.
  - **Radial Arcana** — consumes the luopan for AoE damage + MP restore.
    Legacy uses it as an MP emergency valve (MP below threshold, luopan
    up and near the player). The normal luopan logic re-drops after.
  - Bolster / Widened Compass / Lasting Emanation / Collimated Fervor /
    Mending Halation: legacy never used them; out of scope for v1.

## Detection sources (all readable today)

| State            | Source                                                        |
|------------------|---------------------------------------------------------------|
| Indi up          | PL buff 612 (Colure Active)                                    |
| Entrust pending  | PL buff 584                                                    |
| BoG active       | PL buff 569                                                    |
| Ecliptic active  | PL buff 516                                                    |
| Luopan up        | `Player.PetIndex != 0` && pet entity `HealthPercent >= 1`      |
| Luopan HP        | pet entity `HealthPercent`                                     |
| Luopan position  | pet entity X/Y/Z (distance to an anchor entity)                |
| Engaged          | monitored player's status == Fighting, else a named entity's   |
|                  | status == Fighting (legacy `specifiedEngageTarget`), else the  |
|                  | PL's own status when neither is available                      |

`FFXI.Memory` exposes all of it: `Player.PetIndex`, `Player.Pet`
(`XiEntity` with HealthPercent/Distance/X/Y/Z), and the entity array for
named-entity lookup. One caveat carried over from legacy: when no pet is
out, `PetIndex` is 0 and entity slot 0 must not be mistaken for a pet —
the module treats luopan-up as `PetIndex != 0 && HealthPercent >= 1`.

## Targeting model

Beneficial luopans are centered on a character, chosen in order:
`GeoSettings.LuopanTarget` (explicit name) -> the monitored character ->
`<me>`. Offensive luopans are centered on the enemy: `EngagedTargetName`
when set (cast by name — `/ma "Geo-Frailty" "Hrekken"` style), otherwise
the PL's current target via `<t>` while the PL is engaged. Legacy's extra
"lock target, wait 1 s, cast, wait 4 s, unlock" dance is not repeated:
casting by name or `<t>` needs no target-lock round-trips.

## Sequencing

1. Entrust pending (buff 584): cast `EntrustSpell` on `EntrustTarget`
   (monitored character when unset). Highest GEO priority — the buff is
   short-lived.
2. Indi upkeep: buff 612 missing -> cast `IndiSpell` on `<me>` (gated by
   engagement when `IndiOnlyWhenEngaged`).
3. Luopan drop: no luopan and engaged ->
   a. `UseBlazeOfGlory`: offer the JA, then HOLD the Geo-* cast until
      buff 569 is actually visible (bard JA-verify pattern; 10 s window,
      then cast without it). Legacy fired blind.
   b. Cast `GeoSpell` on the anchor.
4. Luopan management while a luopan is up (each gated by its setting):
   Life Cycle (pet 5–30%), Ecliptic Attrition (pet >= 90%, buff 516
   absent), Dematerialize (pet >= 90%), Full Circle (pet >= 10 yalms from
   the anchor — off-position), Radial Arcana (MP <= threshold, pet within
   9 yalms of the PL).
5. Failure handling matches the bard module: transient (not started /
   interrupted) retries in 3 s, real failures back off 15 s, three
   consecutive genuine failures session-disable that spell with a log
   line.

## Settings (GeoSettings)

Names, not legacy combo indices. `Enabled` requires main or sub job GEO
(21). `IndiSpell`, `IndiOnlyWhenEngaged`, `LuopanEnabled`, `GeoSpell`,
`LuopanTarget`, `EngagedTargetName`, `UseBlazeOfGlory`,
`UseEclipticAttrition`, `UseDematerialize`, `UseLifeCycle`,
`UseFullCircle`, `UseEntrust`, `EntrustSpell`, `EntrustTarget`,
`UseRadialArcana`, `RadialArcanaMpBelow`. Priority weight:
`Priorities.GeoWeight` (default 24 — below songs, above buff upkeep;
prep JAs score +10 over it).

Legacy import maps `IndiSpell_Spell` / `GeoSpell_Spell` /
`EntrustedSpell_Spell` indices through the legacy GeomancerInfo table
(the fixed 30-entry Indi/Geo pair list in Form1.cs) into names, and the
dual-purpose `LuopanSpell_Target` into `LuopanTarget` plus
`EngagedTargetName` when `specifiedEngageTarget` was set.
