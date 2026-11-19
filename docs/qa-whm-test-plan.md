# CurePlease3 — White Mage (WHM) Testing Guide

This guide checks that CurePlease3 heals and supports correctly as a White Mage.
Anyone can run these tests — no programming knowledge needed. Each test tells you
what to do and what you should see. If something different happens, write down the
test number and what you saw.

**Words used in this guide:**

- **Log** — the text box at the bottom of the CurePlease3 window. It explains every
  action the program takes or skips.
- **Threshold** — the HP percentage you choose. Example: "cure at 75%" means the
  healer casts when someone drops to 75% HP or lower.
- **-na spells** — the removal spells: Poisona, Paralyna, Silena, Cursna, etc.
- **Afflatus** — WHM's stance ability: Solace (stronger cures) or Misery
  (stronger Cura/Banish). Only one can be active at a time.

---

## Before you start

1. Attach CurePlease3 to your white mage and open **Settings**.
2. On the **Healing** page, turn cures **on** and set simple starting values:
   party members cure at 75%, only 1 injured member needed, Curaga off for now.
3. On the **White Mage** page, turn WHM job abilities **on**.
4. On the **Debuff Removal** page, turn it **on** and tick Poisona and Paralyna.
5. Save the profile. Be in a party with at least one other player.
6. Press **Start**.

---

## Test 1 — Basic curing

1. Have a party member take damage so they drop below 75% HP.
2. **You should see:** the WHM casts a Cure on them within a second or two, and the
   log names the player and the cure tier.
3. Let them get healed to full. **You should see:** no more cures are cast.
4. Hurt them again, but only slightly (still above your threshold).
   **You should see:** no cure — small damage is ignored.

**Pass:** cures happen only below the threshold, and stop at full HP.

---

## Test 2 — Cure tiers

*Checks: the WHM picks a sensible cure strength — not always the biggest.*

1. Set the cure threshold to 50%. Have a member lose a little HP past the line.
   **You should see:** a small cure (Cure II/III), not Cure VI.
2. Have them lose a LOT of HP. **You should see:** a bigger cure tier.
3. Turn on "undercure" and "overcure" options and put your best cure on cooldown
   (cast it manually first). **You should see:** the WHM falls back to another tier
   instead of doing nothing.

**Pass:** cure size matches the damage; a missing tier never stalls healing.

---

## Test 3 — Curaga (area healing)

1. Turn **Curaga on**: require at least 3 injured members at 70% HP.
2. Have 3+ members stand near you, all below 70%.
   **You should see:** Curaga is cast, healing everyone around you.
3. Now have only 1–2 members hurt. **You should see:** single cures instead —
   Curaga is saved until enough people need it.

**Pass:** Curaga fires only when the member count and HP rules are both met.

---

## Test 4 — Cure priority

1. On the **Priorities** page, add one party member's name as high priority.
2. Hurt that member AND another member at the same time.
   **You should see:** the high-priority member is cured first.

**Pass:** listed names get healed before everyone else.

---

## Test 5 — Afflatus stances

1. On the White Mage page, tick **Afflatus Solace**. Wait a moment.
   **You should see:** Afflatus Solace is used and its icon appears in game.
2. Now tick **Afflatus Misery** instead. **You should see:** Solace is unticked
   automatically (only one stance can be chosen), and Misery is used in game.
3. Try this on a job that is NOT main-job WHM (e.g. RDM/SCH): the page should make
   clear these are main-job-only.

**Pass:** only one stance selectable at a time; the chosen one is kept up.

---

## Test 6 — Divine Seal (stronger cure)

1. On the **Healing** page, set "Divine Seal before critical cures" to 35% HP.
2. Have a member drop below 35%. **You should see:** Divine Seal is used first,
   then the cure — the cure lands roughly twice as big.
3. Drop them below 35% again while Divine Seal is on cooldown.
   **You should see:** a normal cure — no waiting, no error spam.

**Pass:** Divine Seal is spent only on critical cures and only when ready.

---

## Test 7 — Devotion (give your MP away)

1. On the White Mage page, enable **Devotion**, set "target MP at or below" to 20%,
   and leave the target blank (any member).
2. Have a mage party member burn their MP below 20%.
   **You should see:** the WHM uses Devotion on them and they gain MP.
3. Note: Devotion is a merit ability — it only works on main-job WHM.

**Pass:** Devotion fires at the MP threshold on the right target.

---

## Test 8 — Removing ailments (-na spells)

1. Have a party member get poisoned. **You should see:** Poisona is cast on them.
2. Have two members paralyzed. **You should see:** Paralyna on each.
3. Get a party member put to **sleep**. **You should see:** they are woken with a
   cure (per the Debuff Removal page setting).
4. Get the **WHM silenced**. **You should see:** the WHM uses Echo Drops (or Remedy,
   whichever you configured) on themselves — as long as the item is in inventory.

**Pass:** ailments are removed with the right spell; a silenced WHM self-recovers
with an item.

---

## Test 9 — Yagrush (mythic weapon, if you own it)

*Skip this test if you do not have Yagrush.*

1. Equip Yagrush (any version, even level 75).
2. Have two members poisoned. **You should see:** one Poisona removes poison from
   everyone in range — the WHM does not need Accession for group -na spells.

**Pass:** -na spells (including Erase) act as area-of-effect with Yagrush equipped.

---

## Test 10 — Alliance healing

1. Join an alliance (2–3 parties). **You should see:** alliance members appear in
   the CurePlease3 party grid.
2. Have an alliance member from ANOTHER party drop below the cure threshold.
   **You should see:** they are cured like any party member. (Their buffs will not
   show — that is a game limitation, not a bug.)

**Pass:** alliance members are healed; missing buff icons for them are expected.

---

## When you are done

The white mage passes overall if:

- Nobody who needs a cure is ignored, and nobody at full HP is cured.
- Cure size matches the damage; Divine Seal and priority rules are respected.
- Only one Afflatus stance runs, and it stays up.
- Ailments are removed with the correct spell or item every time.
- The game chat is never flooded with error messages; every skip in the log has a
  reason.

Report any test number that did not match, along with what the log showed
(logs are saved in the `logs` folder next to the program).
