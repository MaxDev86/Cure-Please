# CurePlease3 — Geomancer (GEO) Testing Guide

This guide checks that CurePlease3 handles Geomancer correctly. Anyone can run
these tests. If something different happens, write down the test number and what
you saw.

**Words used in this guide:**

- **Bubble / luopan** — the floating ball a GEO places on the ground. An
  *offensive* bubble weakens the monster standing in it; a *beneficial* bubble
  helps party members standing near it. "Bubble" and "luopan" mean the same thing
  here.
- **Indi spell (Indicolure)** — a buff aura centered on the GEO themselves instead
  of a placed bubble.
- **Hate (enmity)** — a monster only suffers from an offensive bubble if the GEO
  has "hate" on it — meaning the GEO has done *something* to that monster (cast on
  it, or healed/cured someone the monster is attacking).
- **Log** — the text box at the bottom of the CurePlease3 window.

---

## Before you start

1. Attach CurePlease3 to your geomancer and open **Settings**.
2. On the **Geomancer** page, turn Geomancer support **on**.
3. Pick an **Indi spell** (e.g. Indi-Fury) and a **Geo spell** (e.g. Geo-Frailty
   for offense, or Geo-Haste for support).
4. Save the profile and press **Start**.

---

## Test 1 — Indi aura upkeep

1. **You should see:** your Indi spell is cast and its aura icon appears on you.
2. Let it wear off. **You should see:** it is re-cast automatically.
3. The aura only helps party members standing near you — walk away and back to
   confirm the icon comes and goes for them (that part is the game, not the app).

**Pass:** the Indi aura is always up.

---

## Test 2 — Beneficial bubble on a player

1. Set the Geo spell to a beneficial one (e.g. Geo-Haste) and set
   "center beneficial luopan on" to a party member's name (blank = yourself).
2. **You should see:** the bubble appears next to that player, and party members
   near it get the buff icon.
3. Have everyone move away and regroup. **You should see:** when the bubble ends up
   10+ yalms from the fight, it is removed (Full Circle) and re-placed where the
   party actually is.

**Pass:** the bubble follows the fight instead of being left behind.

---

## Test 3 — Offensive bubble WITH hate (cure-a-player method)

*The most common real setup: the GEO cures whoever the monster is attacking.*

1. On the Geomancer page, set the engaged enemy name (e.g. the monster family you
   are fighting — you can list several names, one per line).
2. Set the hate method to cure a player, and name your tank (the player the monster
   attacks). Pick a healing spell your job combo knows — GEO learns none, so this
   comes from your sub job. Use the sub-job dropdown to filter the spell list
   ("Any" shows every sub-job spell; picking RDM shows only RDM ones).
3. Pull the monster with the tank. **You should see:** the GEO first casts the cure
   on the tank (this gives the GEO hate), *then* places the offensive bubble, and
   the monster gets the debuff icon.
4. Watch the log: it should show the hate step and then the bubble, in that order.

**Pass:** the offensive bubble always lands *after* hate is established, and the
monster actually gets debuffed.

---

## Test 4 — Offensive bubble WITH hate (cast-on-enemy method)

1. Change the hate method to "cast the hate spell on the enemy itself" and pick an
   offensive spell (e.g. a nuke or Dia from your sub job).
2. Engage the monster. **You should see:** the GEO casts the spell on the monster
   first, then places the bubble, and the monster gets the debuff.

**Pass:** same end result as Test 3 via the other method.

---

## Test 5 — Bubble health management

*Checks the three "save the bubble" abilities.*

1. Enable **Blaze of Glory** before placing. **You should see:** it fires before
   the bubble is placed (the bubble gets halved HP but a stronger effect).
2. Enable **Life Cycle** and get the bubble badly hurt (let the monster hit it, or
   place it somewhere dangerous). When the bubble drops to 5–30% HP while you are
   at 90%+ HP, **you should see:** Life Cycle heals the bubble using your HP.
3. Enable **Dematerialize**. **You should see:** the bubble shrugs off damage while
   it is up.
4. Enable **Ecliptic Attrition**. **You should see:** the bubble's effect is
   stronger but it drains faster — the program still keeps it up.

**Pass:** the bubble is protected and re-placed as needed; abilities fire in the
right situations.

---

## Test 6 — Entrust

1. Enable **Entrust** and set its target (blank = the monitored character).
2. **You should see:** Entrust is used, then a strong single-target Indicolure is
   cast on that player.

**Pass:** the Entrusted buff lands on the chosen target.

---

## Test 7 — Engagement rules

1. Turn on "Only place the luopan while engaged". Stand around with no monster claimed.
   **You should see:** the offensive bubble is NOT placed while idle.
2. Claim a monster whose name is NOT in your engaged-enemy list.
   **You should see:** the bubble is not placed on it.
3. Add the name (the list accepts several names, one per line) and re-test.
   **You should see:** now it works.

**Pass:** offensive bubbles are only placed on listed enemies while engaged.

---

## Test 8 — Re-probe timer

1. Set "re-probe every" to 0. Kill the monster and fight another of the same kind.
   **You should see:** the bubble is placed once per enemy (plus once per fresh
   bubble) — no constant re-casting.
2. Set it to 300 seconds. **You should see:** on long fights the bubble is
   refreshed on that timer even if nothing else changed.

**Pass:** re-cast pacing follows the timer setting.

---

## When you are done

The geomancer passes overall if:

- The Indi aura is always up.
- The beneficial bubble stays near the party (and moves when the fight moves).
- The offensive bubble is only placed after hate is established, and the monster
  visibly gets the debuff.
- Bubble-saving abilities fire in their situations.
- The game chat is never flooded with errors; every skip in the log has a reason.

Report any test number that did not match, along with what the log showed
(logs are saved in the `logs` folder next to the program).
