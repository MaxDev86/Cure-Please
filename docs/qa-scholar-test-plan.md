# CurePlease3 — Scholar (SCH) Testing Guide

This guide checks that CurePlease3 handles Scholar correctly — both as a main job
and as a sub job (/SCH). Anyone can run these tests. If something different happens,
write down the test number and what you saw.

**Words used in this guide:**

- **Arts** — Light Arts (white magic stance) and Dark Arts (black magic stance).
  Only one can be active at a time, and most Scholar abilities need one of them up.
- **Stratagem** — Scholar's special abilities (Accession, Rapture, etc.). You only
  hold a few **charges** at a time; charges come back slowly over time.
- **Addendum: White** — an upgrade to Light Arts that unlocks extra white magic
  (like the -na removal spells) for Scholar.
- **Accession** — makes your next buff or small cure hit the whole party.
- **Log** — the text box at the bottom of the CurePlease3 window.

---

## Before you start

1. Attach CurePlease3 to your scholar (or to any job using /SCH) and open
   **Settings**.
2. On the **Scholar** page, turn Scholar support **on**.
3. Turn on **Light Arts** upkeep. Save the profile and press **Start**.

---

## Test 1 — Arts upkeep and the one-stance rule

1. With Light Arts enabled, **you should see:** Light Arts is used in game and its
   icon appears. It is re-used when it wears off.
2. Now also tick **Dark Arts** in settings. **You should see:** ticking one Arts
   unticks the other — you can never pick both.
3. Tick **Addendum: White**. **You should see:** Light Arts is ticked automatically
   (the addendum needs it). Untick Light Arts — the addendum unticks too.
4. Same check for Addendum: Black with Dark Arts.

**Pass:** exactly one Arts at a time, and each addendum always travels with its Arts.

---

## Test 2 — Stratagem charges are respected

1. Watch the log while the scholar uses stratagems (Accession etc.).
2. **You should see:** the scholar never tries a stratagem with zero charges — no
   "unable to use" errors in the game chat. When charges run out, the scholar waits.
3. Note: the number of charges and recharge speed depend on your SCH level and job
   points (e.g. /SCH gives 1–3 charges; SCH main 99 with 550 JP recharges in 33
   seconds). The program reads this automatically.

**Pass:** zero failed stratagem attempts in the game chat.

---

## Test 3 — Accession with buffs

*Checks: one buff becomes a party-wide buff.*

1. On the **Buff Upkeep** page, pick a buff (e.g. Protect) and tick its
   **Accession** column. Note: Haste, Refresh II/III and Boost- spells do not work
   with Accession and cannot be ticked.
2. **You should see:** Light Arts (if needed) → Accession → Protect, and the whole
   party in range gets Protect from one cast.
3. If someone is too far away, they simply miss it — the log keeps going; this is
   normal (there is no range check for every member).

**Pass:** one cast, whole party buffed, in the right order.

---

## Test 4 — Accession is not stolen by a cure

*Checks a flaw the old CurePlease had: Accession meant for a buff gets eaten by a
healing spell.*

1. Set up an Accession buff (Test 3), then have a party member take damage just as
   the buff is about to refresh.
2. **You should see:** Accession is used for the BUFF as planned. If a cure is
   urgent, the log shows the cure either waits for the buff or goes out without
   Accession — Accession is never silently wasted on the wrong spell.

**Pass:** the log shows Accession always paired with its intended spell.

---

## Test 5 — Accession with cures and removals

1. On the **Debuff Removal** page, turn on group removals with Accession and set
   "group when at least 2 members share an ailment".
2. Get two party members poisoned. **You should see:** (Light Arts →) Accession →
   Poisona, and both members are cured by one cast. On SCH main, Addendum: White is
   used first — SCH needs it for -na spells.
3. Enable Accession for cures (Healing/Scholar options). Hurt several members a
   little. **You should see:** a small Cure hits the whole party in range.

**Pass:** group removal and group cures work, with Addendum: White used first on
SCH main.

---

## Test 6 — Rapture (stronger cure)

1. On the Scholar page, enable **Rapture**, set "HP at or below" to 40% and
   "missing HP at least" to 500.
2. Have a member drop below 40% HP with a big chunk missing.
   **You should see:** Rapture → cure, a noticeably bigger heal.
3. Have a member drop to 39% but missing only 100 HP. **You should see:** a normal
   cure — Rapture is not wasted on small damage.
4. Turn on "pre-charge Rapture at full charges". With charges full and nobody hurt,
   **you should see:** Rapture is used anyway (so the charge timer keeps ticking).
5. Set "reserve stratagem charges" to 1. **You should see:** the scholar always
   keeps one charge in the bank.
6. Note: Rapture via /SCH needs a master-leveled job — on a character without one,
   the log should warn once at Start that Rapture is unavailable, then stay quiet.

**Pass:** Rapture fires only on big heals or at full charges; the reserve is kept.

---

## Test 7 — Sublimation

1. Enable **Sublimation** and set "consume at MP deficit" to 300.
2. Let it charge up (its icon fills in game). Spend MP until you are missing 300+.
   **You should see:** Sublimation is used and you get MP back.

**Pass:** Sublimation is collected only when the MP deficit is big enough.

---

## Test 8 — Perpetuance (SCH main only)

*Skip on /SCH — Perpetuance cannot be used from a sub job.*

1. On the Buff Upkeep page, tick **Perpetuance** for a buff the scholar can cast
   (e.g. Regen).
2. **You should see:** Perpetuance is used before the buff, and the buff lasts much
   longer than normal.
3. Confirm Perpetuance is NOT offered for spells SCH cannot cast (e.g. Phalanx II).

**Pass:** only SCH-castable buffs offer Perpetuance, and extended durations apply.

---

## When you are done

The scholar passes overall if:

- Exactly one Arts runs at a time; addenda always run with their Arts.
- Stratagems are never attempted with zero charges.
- Accession always pairs with the spell it was meant for.
- Rapture follows the HP/missing-HP rules and keeps its reserve.
- The game chat is never flooded with errors; every skip in the log has a reason.

Report any test number that did not match, along with what the log showed
(logs are saved in the `logs` folder next to the program).
