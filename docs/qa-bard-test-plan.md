# CurePlease3 — Bard (BRD) Testing Guide

This guide checks that CurePlease3 sings bard songs correctly. Anyone can run these
tests — no programming knowledge needed. Each test tells you what to do and what you
should see. If what you see is different, write down the test number and what happened.

**Words used in this guide:**

- **Song rotation** — the list of songs the bard keeps active on the party, in order.
- **Dummy song** — a cheap, throwaway song (Army's Paeon) sung only to hold a song
  slot open so a real song can replace it later.
- **Buff** — the icon a song gives you, shown at the top of the game screen.
- **Log** — the text box at the bottom of the CurePlease3 window. It tells you what
  the program is doing and why.
- **JA (Job Ability)** — bard abilities like Nightingale, Troubadour, Marcato,
  Clarion Call, Pianissimo.

**Your bard's instruments and what they do:**

| Instrument | What it does |
|---|---|
| Marsyas | Needed to sing Honor March |
| Loughnashade | Needed to sing Aria of Passion; final stage also lets you keep 4 songs active instead of 2 |
| Daurdabla (level 95 or higher) | Lets you keep 4 songs active instead of 2 |

Normally a bard keeps 2 songs up. With a level 95+ Daurdabla **or** a final-stage
Loughnashade: 4 songs. With the Clarion Call ability on top of that: 5 songs.

---

## Before you start (IMPORTANT — read fully)

CurePlease3 only *says* the song commands. Your **GearSwap script** (a lua addon in
the game) is what actually changes your instruments. Before testing, make sure your
GearSwap script does these three things:

1. Puts on **Marsyas** when you sing Honor March.
2. Puts on **Loughnashade** when you sing Aria of Passion.
3. Puts on **Daurdabla** when you sing the dummy songs (Army's Paeon I and II) —
   and does NOT use any song-duration gear for them. The dummies must wear off
   quickly so the real songs replace them and not each other.

**Now set up CurePlease3:**

1. Attach CurePlease3 to your bard character and open **Settings**.
2. On the **Bard** page, turn Bard **on**.
3. In the instruments list, add: `Marsyas`, `Daurdabla`, `Loughnashade`.
4. In the party songs list, add in this order:
   `Honor March`, `Aria of Passion`, `Valor Minuet V`, `Valor Minuet IV`.
5. In the dummy songs list, add: `Army's Paeon II`, `Army's Paeon`.
6. For the 5th-song test later, set the Clarion song to `Blade Madrigal`.
7. Save the profile.
8. Be in a party with at least one other player for these tests.

---

## Test 1 — Singing from scratch (4 songs)

*Checks: the bard builds up songs correctly when starting with no song buffs.*

1. Zone into a new area or re-log so you have **no song buffs at all**.
2. Use all 4 party songs. Set dummy placement to **Middle**. Keep the dummy songs
   listed.
3. Press **Start** in CurePlease3.
4. Watch the log. You should see the songs being sung in this rough order:
   Honor March, Aria of Passion, then the two dummy songs, then Minuet V and
   Minuet IV (each real song replaces a dummy).
5. When it finishes, check your buffs in game. **You should see:** all 4 real songs
   on you and every party member, and **no Paeon dummy left over**.
6. Repeat this test with dummy placement set to **First**. The singing order changes
   (dummies come first), but the end result must be the same: 4 real songs, no
   dummies.

**Pass:** both placements end with exactly the 4 real songs up on everyone.

---

## Test 2 — The 5th song (Clarion Call)

*Checks: the 5th song works using the Clarion Call ability.*

1. In settings, add `Blade Madrigal` as a 5th party song. Turn **Clarion Call on**
   and make sure the Clarion song is `Blade Madrigal`.
2. Make sure Clarion Call is ready in game (not on cooldown).
3. Zone or re-log so you have no song buffs, then press **Start**.
4. **You should see:** Clarion Call used in game *before* the 5th song is sung, and
   all 5 songs active at the end.
5. Use Clarion Call yourself manually, wait a few seconds, then strip your songs
   (re-log) and press Start again while Clarion Call is still on cooldown.
   **You should see:** the bard keeps 4 songs up and does NOT spam errors trying
   to sing the 5th.

**Pass:** 5 songs when Clarion Call is ready; a clean 4-song rotation when it is
not.

---

## Test 2A — Loughnashade for every song (no Daurdabla)

*Checks: the final-stage Loughnashade also grants 2 extra song slots, so a bard can
sing everything with Loughnashade and only swap to Marsyas for Honor March.*

1. In settings, remove `Daurdabla` from the instruments list so only `Marsyas` and
   `Loughnashade` remain. Keep all 4 party songs and both dummy songs.
2. Change your GearSwap script: equip **Loughnashade for every song except Honor
   March** (which uses Marsyas) — including the dummy songs (still with no
   song-duration gear on the dummies).
3. Zone or re-log for a clean start, then press **Start**.
4. **You should see:** the same build-up as Test 1 — 4 real songs on everyone, no
   dummies left over. Aria of Passion still works (it needs Loughnashade), and
   Honor March still works (Marsyas).
5. Turn Clarion Call on and repeat Test 2. **You should see:** the 5th song works
   here too.
6. **You should also see:** the instrument swaps in game — Loughnashade for
   everything except Honor March.

> Caveat for the tester: declaring "Loughnashade" by name makes the app assume the
> final stage (+2 slots). A stage I or II Loughnashade grants fewer slots — run this
> test with the final-stage instrument only.

**Pass:** identical results to Tests 1–2 with Loughnashade as the workhorse
instrument.

---

## Test 3 — Nightingale + Troubadour (longer songs)

*Checks: the bard uses these two abilities before re-singing, making songs last
twice as long.*

1. Turn **Nightingale + Troubadour on** in settings.
2. Have your songs running. Now remove one song in game (use Finale, or have a
   monster dispel you).
3. Watch the log and the game. **You should see:** Nightingale used first, then
   Troubadour, and only *then* the song. The song must never come before the
   abilities.
4. Bonus check: get hit with **Amnesia** (or use both abilities yourself so they
   are on cooldown), then remove a song again. **You should see:** the log says it
   is singing without the abilities, and the song still gets sung. The bard does
   not freeze or spam.

**Pass:** abilities always come before the song when available; the bard still works
when they are not.

---

## Test 4 — Marcato with Honor March

*Checks: the Marcato ability is spent only on the song you choose.*

1. Turn **Marcato on** and set its song to `Honor March`.
2. Remove just Honor March (Finale works if you sing only it on yourself, or dispel).
3. **You should see:** Marcato is used, then Honor March is sung right after — and
   no other song "steals" the Marcato.
4. If two songs need re-singing at the same time, only Honor March gets Marcato.

**Pass:** Marcato always lands on Honor March.

---

## Test 5 — Soul Voice (optional, off by default)

*Checks: Soul Voice is only used when you ask for it.*

1. Leave Soul Voice **off**. Run the bard for a while.
   **You should see:** Soul Voice is never used.
2. Turn Soul Voice **on** while it is ready in game. Wait for a refresh.
   **You should see:** Soul Voice is used, and any song that was already running
   without it is sung again once, so every song gets the stronger version.

**Pass:** Soul Voice never fires on its own; when enabled, all songs get upgraded.

---

## Test 6 — Keeping songs up

*Checks: the bard maintains songs without wasting casts.*

1. With all songs running, watch the bard for a few minutes.
   **You should see:** no songs are sung while everything is already up.
2. Songs should be re-sung *before* they wear off — your buff icons should never
   reach zero. Watch for any song blinking out before being re-sung.
3. Have **one** song dispelled by a monster. **You should see:** only that one song
   is re-sung — the bard does not rebuild everything.
4. Die and get raised (or zone). **You should see:** the full rebuild from Test 1
   happens again, including the dummy songs.

**Pass:** no idle casts, no gaps in buffs, single-song fixes stay small, full wipe
rebuilds fully.

---

## Test 7 — Pianissimo (song for one player)

*Checks: single-target songs land on the right person.*

1. In settings, add a Pianissimo entry: pick a party member's name and a song
   (for example, a mage gets `Mage's Ballad II`).
2. **You should see:** the Pianissimo ability is used first, then the song lands on
   that one player only. Your party songs are not disturbed.
3. Have that player run far away or leave the party. **You should see:** the log
   says the target is not reachable; the bard skips it and tries again later —
   no error spam.

**Pass:** the right person gets the song; missing targets are handled quietly.

---

## Test 8 — Enemy songs (Elegy etc.)

*Checks: debuff songs are kept on the monster you are fighting.*

1. In settings, add `Carnage Elegy` to the enemy song list.
2. Attack a monster. **You should see:** Elegy is sung on the monster, and re-sung
   before it wears off.
3. Switch to a different monster. **You should see:** the bard promptly puts Elegy
   on the new target.

**Pass:** the current target always has the debuff songs.

---

## Test 9 — Honor March and Aria of Passion

*Checks: the instrument-only songs are handled correctly.*

1. With Marsyas in your instrument list, press Start.
   **You should see:** Honor March is sung normally. (The game tells CurePlease3
   the song is "not learned" — the program is supposed to ignore that. This is
   correct behavior, not a bug.)
2. Now remove `Marsyas` from the instrument list in settings and remove Honor
   March from your buffs. **You should see:** Honor March is tried, fails, and
   after a few tries the log says it is giving up on it for this session — it does
   not keep failing forever.
3. Same checks apply to Aria of Passion with Loughnashade.

**Pass:** the song sings with the instrument declared; without it, the bard stops
trying after a few attempts.

---

## Test 10 — Awkward situations

1. **Party member too far away:** have one member stand more than ~10 yalms away,
   then trigger a refresh. **You should see:** the song still goes out for everyone
   in range; the log notes the far member instead of stalling.
2. **Bard as sub-job (/BRD):** only 2 songs fit. **You should see:** the rotation
   cleanly keeps the first 2 songs; dummies and Clarion Call are never attempted.
3. **Silenced bard:** get silenced by a monster. **You should see:** singing stops
   quietly (no error spam) and resumes once silence is removed.
4. **Pause button:** press Pause in the middle of singing. **You should see:** no
   new commands are sent; pressing Pause again resumes right where it left off.

**Pass:** each situation is handled calmly — nothing crashes, nothing spams.

---

## When you are done

The bard passes overall if:

- After any rebuild, everyone in the party has exactly the songs from your settings
  list — no dummies left, no missing songs.
- Abilities (Nightingale, Troubadour, Marcato, Clarion Call, Pianissimo) are always
  used *before* the song they belong to, never after.
- Nothing ever floods the game chat with error messages.
- Every time the bard skips something, the log says why.

Report any test number that did not match, along with what the log showed at the time
(logs are saved in the `logs` folder next to the program).
