# CurePlease3 — Red Mage (RDM) Testing Guide

This guide checks that CurePlease3 handles Red Mage correctly — as a main job and
as a sub job (/RDM). Anyone can run these tests. If something different happens,
write down the test number and what you saw.

**Words used in this guide:**

- **Convert** — the RDM ability that swaps your current HP and MP. It is the
  classic "refill my MP using my HP" button.
- **Composure** — a RDM stance that makes your enhancing magic (buffs) last much
  longer. Main-job RDM only — it does not work from /RDM.
- **Buff upkeep** — the settings page where you choose which buffs the program
  keeps on you (Haste, Refresh, Protect, etc.).
- **Log** — the text box at the bottom of the CurePlease3 window.

---

## Before you start

1. Attach CurePlease3 to your red mage and open **Settings**.
2. On the **Red Mage** page, turn RDM job abilities **on**.
3. On the **Buff Upkeep** page, pick a couple of simple buffs: `Refresh` and
   `Haste`. Save the profile and press **Start**.

---

## Test 1 — Basic buff upkeep

1. **You should see:** Refresh and Haste are cast on you and their icons appear.
2. Wait. **You should see:** each buff is re-cast before it wears off — the icons
   never disappear.
3. Dispel yourself (or have a friend do it). **You should see:** the missing buff
   is re-cast promptly.

**Pass:** your buffs stay up with no gaps and no idle re-casts.

---

## Test 2 — The buff fallback timer

*Checks a tricky case: another player casts the same buff on you, then it wears
off — you get no "wore off" message, so the program must rely on its timer.*

1. Ask a friend to cast Haste on you (overwriting yours).
2. Wait for it to wear off naturally. **You should see:** even with no wear-off
   message, the program re-casts Haste when its own timer runs out.

**Pass:** buffs recover even when the game never tells us they wore off.

---

## Test 3 — Convert at the MP threshold

1. On the Red Mage page, enable **Convert** and set it to 10% MP.
2. Burn your MP (cast spells) until you drop below 10%.
   **You should see:** Convert is used — your HP and MP swap — and the log notes it.
3. Repeat with the threshold at 25%: Convert should now fire earlier.
4. Turn Convert off. Burn your MP again. **You should see:** Convert is never used.

**Pass:** Convert fires exactly at the configured MP level, and never when off.

> Safety note for the tester: Convert leaves you with low HP. Run this test in a
> safe area, or make sure cures are enabled so the program (or a friend) heals you
> right after.

---

## Test 4 — Composure (main-job RDM only)

1. On a main-job RDM, enable **Composure**. **You should see:** it is used in game
   and re-used when it wears off. Your buffs now last noticeably longer — and the
   program's re-cast timers respect the longer duration.
2. Switch to /RDM as a sub job. **You should see:** the settings note that Composure
   is main-job-only, and the program does not try to use it (the log would say so
   once at Start if it were left enabled).

**Pass:** Composure is kept up on main RDM and never attempted from /RDM.

---

## Test 5 — RDM as a support sub job

1. Switch to WHM/RDM or BLM/RDM.
2. **You should see:** Convert still works from /RDM (it is a level-40 ability, so
   /RDM 40+ or higher with master levels has it). Buff upkeep uses RDM spells like
   Refresh and Haste as normal.
3. On the Buff Upkeep page, use the job filter for your main+sub combo.
   **You should see:** only spells your combo can actually cast are offered
   (e.g. WHM/SCH 99/59 must not offer Regen V, which is SCH-main-only).

**Pass:** the offered spells match what your job combination can really cast.

---

## Test 6 — Gain/Boost and one-of-each rules

1. On the Buff Upkeep page, try to pick two Gain- spells (e.g. Gain-MND and
   Gain-STR). **You should see:** only one can be selected — picking one clears
   the other. Same for Boost- spells, En- spells, and Storms.
2. Note the job tags: Gain- spells are RDM-main; Boost- spells are WHM-main. On a
   job that cannot cast them, they should not be selectable.

**Pass:** mutually exclusive buffs can never be active together in settings.

---

## When you are done

The red mage passes overall if:

- Refresh/Haste (and any other chosen buffs) never lapse, with or without the
  game's wear-off messages.
- Convert fires at exactly the configured MP threshold.
- Composure runs on main RDM only; longer buff durations are respected.
- The spell lists never offer spells your job combo cannot cast.
- The game chat is never flooded with errors; every skip in the log has a reason.

Report any test number that did not match, along with what the log showed
(logs are saved in the `logs` folder next to the program).
