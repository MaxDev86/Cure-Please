using System;
using System.Collections.Generic;
using System.Linq;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Bard song engine (docs/bard-design-notes.md).
    ///
    /// Key mechanics implemented:
    ///  - Songs are tracked per scope ("&lt;me&gt;" = party AoE rotation, otherwise a
    ///    Pianissimo target) as an ordered list of (name, family, landed-at, duration) —
    ///    the slot model in miniature. Trimming against the observed family buff count
    ///    handles expiry/dispel; the overwrite-lowest-duration and overwrite-same rules
    ///    make re-singing always safe.
    ///  - Detection is presence-based, not timer-based: the bard's own buffs come from
    ///    memory every tick, party members from the addon. Duration estimates are only
    ///    used to refresh BEFORE a song wears (no gap), never as the sole source of truth.
    ///  - JA sequencing is verified: a prep JA (Nightingale/Troubadour/Marcato/Clarion
    ///    Call/Pianissimo) is offered, then the dependent song is held until the JA buff
    ///    is actually visible on the PL — legacy flaw 2.11 (blind 1.5 s JA wait) is not
    ///    repeated. If the JA never lands (blocked), its recast timer prevents spam and
    ///    the songs proceed without it.
    ///  - Range gate: AoE songs assume 9.9 yalms; a song offered only for an addon-visible
    ///    member is skipped when that member is out of range.
    ///  - Dummy songs pad spare instrument slots. On a cold start (all song buffs gone:
    ///    death/zone/full dispel) with a multi-song instrument whose slots the rotation
    ///    fills, dummies are sacrificial: placed per BardSettings.DummyPlacement
    ///    ("Middle" = after the first two real songs; "First" = before everything),
    ///    retired once all seen up, and overwritten by the remaining real songs.
    ///  - Instrument-aware slot model: the declared OwnedInstruments list (Marsyas,
    ///    Gjallarhorn, Daurdabla, Blurred Harp, Terpander, Loughnashade) sets the slot
    ///    count; Clarion Call adds one while active. Only Daurdabla, Terpander, Blurred
    ///    Harp, and Loughnashade grant additional song slots — Marsyas and Gjallarhorn
    ///    are potency/duration instruments. Honor March requires Marsyas, Aria of
    ///    Passion requires Loughnashade. Cure-Please only sends /ma and /ja — the
    ///    player's GearSwap lua owns equipment (including singing dummies WITHOUT the
    ///    duration midcast set, which is what guarantees the overwrite targets the
    ///    dummies), so declared instruments are trusted over the live equipped state.
    ///  - Soul Voice (opt-in): offered before a refresh pass when enabled, and active
    ///    songs not sung under Soul Voice / Troubadour are re-sung to lock in the
    ///    doubled duration (Singer's sv/nt upgrade flags).
    ///  - Enemy songs (Elegy/Nocturne/Threnody/Lullaby on the battle target) are
    ///    timer-tracked — enemy debuffs are not readable from memory.
    ///  - Addon action landings (0x028) give a 15 s grace window before a member whose
    ///    buff packet lags is treated as missing a song.
    ///
    /// Songs the resource DB cannot resolve (Honor March without Marsyas data) are still
    /// offered, but three consecutive failures disable that song for the session.
    /// </summary>
    public sealed class BardModule : IDecisionModule
    {
        private const byte JobBard = 10;

        /// <summary>After a failed song cast, wait this long before offering it again.</summary>
        private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(15);

        /// <summary>How long to wait for a JA buff to appear before singing without it.</summary>
        private static readonly TimeSpan JaVerifyWindow = TimeSpan.FromSeconds(10);

        /// <summary>Consecutive cast failures after which a song is given up on (session).</summary>
        private const int MaxConsecutiveFailures = 3;

        private enum AbilityState
        {
            /// <summary>Buff is on the PL right now.</summary>
            Active,

            /// <summary>Off recast and known — can be offered.</summary>
            Ready,

            /// <summary>Recently executed; waiting for the buff to appear.</summary>
            PendingVerify,

            /// <summary>On recast or not learned — sing without it.</summary>
            Unavailable,
        }

        private sealed class SungSong
        {
            public string Name;
            public StatusEffect Family;
            public DateTime LandedUtc;
            public double DurationSeconds;

            /// <summary>Sung while Troubadour was up (double duration locked in).</summary>
            public bool WithTroubadour;

            /// <summary>Sung while Soul Voice was up (double duration + potency locked in).</summary>
            public bool WithSoulVoice;
        }

        /// <summary>Candidate context carried to OnActionExecuted (engine passes the same object back).</summary>
        private sealed class SongContext
        {
            public StatusEffect Family;
            public double DurationSeconds;
            public bool WithTroubadour;
            public bool WithSoulVoice;

            /// <summary>Enemy-song target server id (0 for party/pianissimo songs).</summary>
            public uint TargetId;
        }

        /// <summary>Believed state of one debuff song on the battle target (timer-based: enemy buffs are unreadable).</summary>
        private sealed class EnemySongState
        {
            public uint TargetId;
            public DateTime ExpiresUtc;
        }

        /// <summary>Per-tick song availability derived from the instrument model.</summary>
        private struct SongAvailability
        {
            /// <summary>Base slots from the detected/declared instrument (2 + instrument bonus).</summary>
            public int BaseSlots;

            /// <summary>BaseSlots + 1 while Clarion Call is active.</summary>
            public int EffectiveSlots;

            public bool HonorMarch;
            public bool AriaOfPassion;
            public string InstrumentName;
        }

        private readonly GameStateService _state;

        /// <summary>Scope ("&lt;me&gt;" or pianissimo target name) -> ordered songs believed active.</summary>
        private readonly Dictionary<string, List<SungSong>> _sung = new Dictionary<string, List<SungSong>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Transient outcomes (rejected cast, interrupted) retry this soon — no failure strike.</summary>
        private static readonly TimeSpan TransientRetry = TimeSpan.FromSeconds(3);

        /// <summary>"song|target" -> last attempt (any outcome), for backoff.</summary>
        private readonly Dictionary<string, DateTime> _lastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"song|target" -> last transient failure (NotStarted/Interrupted): short retry, never strikes.</summary>
        private readonly Dictionary<string, DateTime> _shortRetry = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"song|target" -> consecutive failure count.</summary>
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>JA name -> last execution time.</summary>
        private readonly Dictionary<string, DateTime> _jaAttempted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<ActionCandidate, SongContext> _pending = new Dictionary<ActionCandidate, SongContext>();

        /// <summary>Family -> believed debuff state on the battle target.</summary>
        private readonly Dictionary<StatusEffect, EnemySongState> _enemySongs = new Dictionary<StatusEffect, EnemySongState>();

        /// <summary>Family -> (when, who received it) of the last addon-confirmed song landing.</summary>
        private readonly Dictionary<StatusEffect, KeyValuePair<DateTime, HashSet<string>>> _landings =
            new Dictionary<StatusEffect, KeyValuePair<DateTime, HashSet<string>>>();

        /// <summary>Family and time of the last song this module executed (correlates addon landings).</summary>
        private StatusEffect _lastSongFamily;
        private DateTime _lastSongCastUtc = DateTime.MinValue;

        /// <summary>Skip-reason dedup: reason key -> last time it was logged.</summary>
        private readonly Dictionary<string, DateTime> _skipLogged = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan SkipLogInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Cold-start pass state: the full song structure was lost (death/zone/full
        /// dispel) and is being rebuilt with sacrificial dummies placed per
        /// <see cref="BardSettings.DummyPlacement"/>. Cleared once every real rotation
        /// family is observed on the PL. <see cref="_coldStartDummiesDone"/> latches
        /// once all dummies were simultaneously observed — they are then retired from
        /// the rotation so the remaining real songs overwrite them (GearSwap sings
        /// dummies without the duration midcast set, so they are always the
        /// lowest-duration song on the player).
        /// </summary>
        private bool _coldStartPass;
        private bool _coldStartDummiesDone;

        /// <summary>Optional diagnostic output (why a configured song is NOT being sung). Wired by the UI.</summary>
        public event Action<string> Log;

        public BardModule(GameStateService state)
        {
            _state = state;
        }

        /// <summary>Logs a skip reason at most once per 30 s per reason — the engine ticks twice a second.</summary>
        private void LogSkip(string key, string message)
        {
            if (Log == null)
            {
                return;
            }

            if (_skipLogged.TryGetValue(key, out DateTime logged) && DateTime.UtcNow - logged < SkipLogInterval)
            {
                return;
            }

            _skipLogged[key] = DateTime.UtcNow;
            Log.Invoke(message);
        }

        public string Name => "Bard";

        /// <summary>
        /// Zoning wipes all songs (legacy: song_casting = 0 + ForceSongRecast).
        /// Clearing the tracking makes every configured song missing again, which
        /// re-engages the cold-start pass for the rebuild.
        /// </summary>
        public void OnZoneChanged()
        {
            _sung.Clear();
            _lastAttempt.Clear();
            _shortRetry.Clear();
            _failures.Clear();
            _jaAttempted.Clear();
            _pending.Clear();
            _enemySongs.Clear();
            _landings.Clear();
            _lastSongFamily = 0;
            _lastSongCastUtc = DateTime.MinValue;
            _coldStartPass = false;
            _coldStartDummiesDone = false;
        }

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            BardSettings bard = settings.Bard;
            PlayerState pl = snapshot.Player;

            if (!bard.Enabled || (pl.MainJob != JobBard && pl.SubJob != JobBard))
            {
                return;
            }

            SongAvailability availability = ComputeAvailability(bard, pl);

            // Single-target top-ups and enemy debuffs interleave freely with the party
            // rotation; the engine picks one action per tick by score.
            AddPianissimoCandidates(snapshot, settings, availability, candidates);
            AddEnemySongCandidates(snapshot, settings, availability, candidates);

            List<string> rotation = BuildRotation(bard, pl, availability.EffectiveSlots, out int realCount);
            if (rotation.Count == 0)
            {
                return;
            }

            if (realCount > availability.EffectiveSlots)
            {
                LogSkip("slots", "Bard: rotation has " + realCount + " songs but " + availability.EffectiveSlots
                    + " slot(s) (instrument: " + availability.InstrumentName + ") — songs past the slot count will keep "
                    + "overwriting earlier ones. Slots beyond 2 come from a multi-song instrument (Daurdabla, "
                    + "Terpander, Blurred Harp, Loughnashade) — declare OwnedInstruments in settings.json if GearSwap "
                    + "swaps one in mid-cast, because the equipped slot alone will not show it.");
            }

            int missingIndex;
            string refreshSong = FindNeededSong(snapshot, settings, rotation, out missingIndex);
            if (refreshSong == null)
            {
                return;
            }

            OfferPrepAbilities(settings, pl, candidates, missingIndex, availability.BaseSlots, refreshSong);

            AbilityState nightingale = AbilityStateOf(pl, "Nightingale", StatusEffect.Nightingale);
            AbilityState troubadour = AbilityStateOf(pl, "Troubadour", StatusEffect.Troubadour);
            AbilityState soulVoice = AbilityStateOf(pl, "Soul Voice", StatusEffect.Soul_Voice);
            bool holdForJa = (bard.UseNitro
                    && (nightingale == AbilityState.Ready || nightingale == AbilityState.PendingVerify
                        || troubadour == AbilityState.Ready || troubadour == AbilityState.PendingVerify))
                || (bard.UseSoulVoice
                    && (soulVoice == AbilityState.Ready || soulVoice == AbilityState.PendingVerify));

            if (!holdForJa)
            {
                OfferSong(settings, availability, rotation, missingIndex, refreshSong, pl, candidates);
            }
        }

        private void OfferSong(
            CurePleaseSettings settings,
            SongAvailability availability,
            List<string> rotation,
            int index,
            string songName,
            PlayerState pl,
            List<ActionCandidate> candidates)
        {
            BardSettings bard = settings.Bard;

            // Marcato pairing: hold the song until Marcato is up (or known unavailable).
            if (bard.UseMarcato && songName.Equals(bard.MarcatoSong?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                AbilityState marcato = AbilityStateOf(pl, "Marcato", StatusEffect.Marcato);
                if (marcato == AbilityState.Ready || marcato == AbilityState.PendingVerify)
                {
                    return;
                }
            }

            string key = songName + "|<me>";
            if (OnBackoff(key) || IsDisabled(key))
            {
                return;
            }

            if (!IsSongCastable(availability, songName, out string reason))
            {
                LogSkip("cast|" + songName, "Bard: skipping \"" + songName + "\" — " + reason);
                return;
            }

            var candidate = new ActionCandidate
            {
                Category = ActionCategory.Song,
                Score = settings.Priorities.SongWeight + (rotation.Count - index) * 0.05,
                ActionName = songName,
                TargetName = "<me>",
                Description = songName + " (song " + (index + 1) + "/" + rotation.Count + ")",
            };

            bool troubadour = pl.HasBuff(StatusEffect.Troubadour);
            bool soulVoice = pl.HasBuff(StatusEffect.Soul_Voice);
            _pending[candidate] = new SongContext
            {
                Family = FamilyOf(songName),
                DurationSeconds = bard.BaseDurationSeconds * (troubadour ? 2 : 1) * (soulVoice ? 2 : 1),
                WithTroubadour = troubadour,
                WithSoulVoice = soulVoice,
            };

            candidates.Add(candidate);
        }

        private void OfferPrepAbilities(CurePleaseSettings settings, PlayerState pl, List<ActionCandidate> candidates, int missingIndex, int baseSlots, string refreshSong)
        {
            BardSettings bard = settings.Bard;
            double jaScore = settings.Priorities.SongWeight + 10;

            // Soul Voice first: it multiplies everything sung after it (Singer parity).
            if (bard.UseSoulVoice && missingIndex >= 0
                && AbilityStateOf(pl, "Soul Voice", StatusEffect.Soul_Voice) == AbilityState.Ready)
            {
                candidates.Add(Ja("Soul Voice", jaScore));
                return;
            }

            if (bard.UseNitro)
            {
                AbilityState nightingale = AbilityStateOf(pl, "Nightingale", StatusEffect.Nightingale);
                AbilityState troubadour = AbilityStateOf(pl, "Troubadour", StatusEffect.Troubadour);

                if (nightingale == AbilityState.Ready)
                {
                    candidates.Add(Ja("Nightingale", jaScore));
                    return;
                }

                if (pl.HasBuff(StatusEffect.Nightingale) && troubadour == AbilityState.Ready)
                {
                    candidates.Add(Ja("Troubadour", jaScore));
                    return;
                }

                if (nightingale == AbilityState.Unavailable)
                {
                    LogSkip("ja|Nightingale", "Bard: Nightingale unavailable (" + JaUnavailableReason("Nightingale") + ") — singing without NiTro");
                }
                else if (nightingale == AbilityState.Active && troubadour == AbilityState.Unavailable)
                {
                    LogSkip("ja|Troubadour", "Bard: Troubadour unavailable (" + JaUnavailableReason("Troubadour") + ") — singing without it");
                }
            }

            // Marcato pairs with one song (typically Honor March) and applies to the next
            // song sung — fire it immediately before that song. OfferSong holds the song
            // itself until the buff lands (or Marcato proves unavailable).
            if (bard.UseMarcato && missingIndex >= 0
                && refreshSong.Equals(bard.MarcatoSong?.Trim(), StringComparison.OrdinalIgnoreCase)
                && AbilityStateOf(pl, "Marcato", StatusEffect.Marcato) == AbilityState.Ready)
            {
                candidates.Add(Ja("Marcato", jaScore));
                return;
            }

            // Clarion Call is only needed when the rotation overflows the instrument's slots.
            if (bard.UseClarionCall && missingIndex >= baseSlots
                && AbilityStateOf(pl, "Clarion Call", StatusEffect.Clarion_Call) == AbilityState.Ready)
            {
                candidates.Add(Ja("Clarion Call", jaScore));
                return;
            }
        }

        private string JaUnavailableReason(string abilityName)
        {
            int recast = _state.GetAbilityRecast(abilityName);
            if (recast != 0)
            {
                return "on recast (" + recast + " s)";
            }

            return _state.HasAbility(abilityName)
                ? "state temporarily unreadable"
                : "not learned at this job level" + _state.AbilityDebugProbe(abilityName);
        }

        /// <summary>
        /// Finds the first rotation song that needs (re)singing.
        /// Returns null when everything is up and fresh. A member missing the song
        /// triggers a re-sing too — AnyMemberMissingFamily applies the range gate.
        /// During a cold-start pass the walk is POSITIONAL (first rotation song not
        /// yet sung this pass) so the configured order is honored exactly — the
        /// family-deficit view would let a later same-family song jump the queue.
        /// </summary>
        private string FindNeededSong(
            GameSnapshot snapshot,
            CurePleaseSettings settings,
            List<string> rotation,
            out int missingIndex)
        {
            BardSettings bard = settings.Bard;
            PlayerState pl = snapshot.Player;

            // Group rotation songs by family, preserving order; index of first song of each group.
            List<SungSong> sung = SungFor("<me>");

            if (_coldStartPass)
            {
                for (int i = 0; i < rotation.Count; i++)
                {
                    // A session-disabled song (3 genuine failures) must not freeze the
                    // whole pass; a transient backoff still waits so the dummy contract
                    // order is preserved.
                    if (IsDisabled(rotation[i] + "|<me>"))
                    {
                        continue;
                    }

                    bool tracked = false;
                    foreach (SungSong s in sung)
                    {
                        if (s.Name.Equals(rotation[i], StringComparison.OrdinalIgnoreCase))
                        {
                            tracked = true;
                            break;
                        }
                    }

                    if (!tracked)
                    {
                        missingIndex = i;
                        return rotation[i];
                    }
                }

                // Every pass song was sung at least once — fall through to normal
                // detection (BuildRotation clears the pass once families are observed).
            }

            for (int i = 0; i < rotation.Count; i++)
            {
                string name = rotation[i];
                StatusEffect family = FamilyOf(name);
                if (family == 0)
                {
                    continue;
                }

                int configuredCount = CountInRotation(rotation, family);
                int observed = CountFamily(pl.Buffs, family);
                TrimSungToObserved(sung, family, observed);

                if (observed < configuredCount)
                {
                    string needed = SongToResing(sung, rotation, family) ?? name;
                    if (Offerable(needed))
                    {
                        missingIndex = i;
                        return needed;
                    }

                    continue; // backed off / disabled: do not freeze the rotation behind it
                }

                // PL is covered — but an addon-visible member may have lost it.
                if (AnyMemberMissingFamily(snapshot, pl, bard, family, configuredCount))
                {
                    string needed = SongToResing(sung, rotation, family) ?? name;
                    if (Offerable(needed))
                    {
                        missingIndex = i;
                        return needed;
                    }

                    continue;
                }

                // Upgrade pass: while Soul Voice / Troubadour is up, re-sing tracked
                // rotation songs that were not sung under it (Singer's sv/nt flags).
                SungSong upgrade = NeedsUpgrade(sung, rotation, family, pl);
                if (upgrade != null && Offerable(upgrade.Name))
                {
                    missingIndex = i;
                    return upgrade.Name;
                }

                // All present: refresh the tracked song closest to expiry when inside the margin.
                SungSong expiring = EarliestExpiring(sung, rotation, family, bard.RefreshMarginSeconds);
                if (expiring != null && Offerable(expiring.Name))
                {
                    missingIndex = i;
                    return expiring.Name;
                }
            }

            missingIndex = -1;
            return null;
        }

        private void AddPianissimoCandidates(GameSnapshot snapshot, CurePleaseSettings settings, SongAvailability availability, List<ActionCandidate> candidates)
        {
            BardSettings bard = settings.Bard;
            PlayerState pl = snapshot.Player;
            double score = settings.Priorities.SongWeight;

            foreach (PianissimoTarget pair in bard.PianissimoTargets)
            {
                if (string.IsNullOrWhiteSpace(pair.Song) || string.IsNullOrWhiteSpace(pair.Target))
                {
                    continue;
                }

                string songName = pair.Song.Trim();
                string targetName = pair.Target.Trim();
                StatusEffect family = FamilyOf(songName);
                if (family == 0)
                {
                    continue;
                }

                short[] buffs = BuffsFor(snapshot, targetName);
                if (buffs == null)
                {
                    // No buff visibility on this target (not self/monitored/own party) — skip.
                    continue;
                }

                List<SungSong> sung = SungFor(targetName);
                int observed = CountFamily(buffs, family);
                TrimSungToObserved(sung, family, observed);

                SungSong expiring = null;
                if (observed >= 1)
                {
                    expiring = EarliestExpiring(sung, new List<string> { songName }, family, bard.RefreshMarginSeconds);
                    if (expiring == null)
                    {
                        continue; // present and fresh
                    }
                }

                if (!InRangeOf(snapshot, pl, targetName, bard.SongRangeYalms))
                {
                    continue;
                }

                if (!pl.HasBuff(StatusEffect.Pianissimo))
                {
                    if (AbilityStateOf(pl, "Pianissimo", StatusEffect.Pianissimo) == AbilityState.Ready)
                    {
                        candidates.Add(Ja("Pianissimo", score + 10));
                    }

                    continue;
                }

                string key = songName + "|" + targetName;
                if (OnBackoff(key) || IsDisabled(key))
                {
                    continue;
                }

                if (!IsSongCastable(availability, songName, out string reason))
                {
                    LogSkip("cast|" + key, "Bard: skipping \"" + songName + "\" -> " + targetName + " — " + reason);
                    continue;
                }

                var candidate = new ActionCandidate
                {
                    Category = ActionCategory.Song,
                    Score = score + 2,
                    ActionName = songName,
                    TargetName = targetName,
                    Description = songName + " -> " + targetName + " (pianissimo)",
                };

                _pending[candidate] = new SongContext
                {
                    Family = family,
                    DurationSeconds = bard.BaseDurationSeconds,
                };

                candidates.Add(candidate);
            }
        }

        /// <summary>
        /// Offensive songs on the battle target. Enemy buffs/debuffs cannot be read from
        /// memory, so tracking is a conservative timer (base durations, no gear/JA mods)
        /// reset whenever the target id changes. Instant effects (Magic Finale) have no
        /// family and are ignored here.
        /// </summary>
        private void AddEnemySongCandidates(GameSnapshot snapshot, CurePleaseSettings settings, SongAvailability availability, List<ActionCandidate> candidates)
        {
            BardSettings bard = settings.Bard;
            if (bard.EnemyDebuffSongs.Count == 0)
            {
                return;
            }

            PlayerState pl = snapshot.Player;
            if (pl.EntityStatus != EntityStatus.Engaged || snapshot.TargetId == 0 || snapshot.TargetHpp == 0)
            {
                return;
            }

            foreach (string configured in bard.EnemyDebuffSongs)
            {
                if (string.IsNullOrWhiteSpace(configured))
                {
                    continue;
                }

                string songName = configured.Trim();
                StatusEffect family = FamilyOf(songName);
                if (family == 0)
                {
                    LogSkip("enemyfam|" + songName, "Bard: enemy song \"" + songName + "\" has no trackable debuff (dispels like Magic Finale are unsupported) — skipping");
                    continue;
                }

                bool newTarget = true;
                if (_enemySongs.TryGetValue(family, out EnemySongState state))
                {
                    newTarget = state.TargetId != snapshot.TargetId;
                    if (!newTarget && DateTime.UtcNow < state.ExpiresUtc.AddSeconds(-10))
                    {
                        continue; // believed still up on this target
                    }
                }

                string key = songName + "|<t>";
                // Backoff does not apply on a target switch: the debuff is per-mob.
                if ((!newTarget && OnBackoff(key)) || IsDisabled(key))
                {
                    continue;
                }

                if (!IsSongCastable(availability, songName, out string reason))
                {
                    LogSkip("cast|" + key, "Bard: skipping \"" + songName + "\" -> " + snapshot.TargetName + " — " + reason);
                    continue;
                }

                var candidate = new ActionCandidate
                {
                    Category = ActionCategory.Song,
                    Score = settings.Priorities.SongWeight + 3,
                    ActionName = songName,
                    TargetName = "<t>",
                    Description = songName + " -> " + snapshot.TargetName + " (enemy song)",
                };

                _pending[candidate] = new SongContext
                {
                    Family = family,
                    DurationSeconds = EnemySongDurationSeconds(songName, family),
                    TargetId = snapshot.TargetId,
                };

                candidates.Add(candidate);
            }
        }

        /// <summary>Base (unmodified) durations; underestimates only cause an early re-sing, which is safe.</summary>
        private static double EnemySongDurationSeconds(string songName, StatusEffect family)
        {
            if (songName.IndexOf("Carnage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 180;
            }

            if (songName.IndexOf("Lullaby", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Foe/Horde Lullaby II last 90 s, base Lullabies 45 s.
                return songName.IndexOf("II", StringComparison.OrdinalIgnoreCase) >= 0 ? 90 : 45;
            }

            if (family == StatusEffect.Threnody)
            {
                return 60;
            }

            return 120; // Elegy (Battlefield), Nocturne, Requiem
        }

        /// <summary>
        /// Feeds an addon-confirmed action landing (0x028) into the module. Correlated to
        /// the last song we executed; recipients are remembered briefly so a party member
        /// whose buff packet (0x076) lags behind is not immediately re-sung at.
        /// </summary>
        public void OnSpellLanded(IReadOnlyList<string> recipientNames)
        {
            if (_lastSongFamily == 0 || DateTime.UtcNow - _lastSongCastUtc > TimeSpan.FromSeconds(3))
            {
                return;
            }

            _landings[_lastSongFamily] = new KeyValuePair<DateTime, HashSet<string>>(
                DateTime.UtcNow, new HashSet<string>(recipientNames, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>True when the addon confirmed this family landed on the member within the last 15 s.</summary>
        private bool RecentlyLandedOn(StatusEffect family, string memberName)
        {
            if (!_landings.TryGetValue(family, out KeyValuePair<DateTime, HashSet<string>> landing)
                || DateTime.UtcNow - landing.Key > TimeSpan.FromSeconds(15))
            {
                return false;
            }

            return landing.Value.Contains(memberName);
        }

        public void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
            if (candidate.IsJobAbility)
            {
                _jaAttempted[candidate.ActionName] = DateTime.UtcNow;
                return;
            }

            if (candidate.Category != ActionCategory.Song)
            {
                return;
            }

            string key = candidate.ActionName + "|" + candidate.TargetName;
            SongContext ctx = _pending.TryGetValue(candidate, out SongContext c) ? c : null;
            _pending.Remove(candidate);

            if (!result.Succeeded)
            {
                if (result.Outcome == CastOutcome.NotStarted || result.Outcome == CastOutcome.Interrupted)
                {
                    // Transient: the game rejected the command (a cast was still in
                    // progress) or the song was interrupted by movement — retry soon
                    // instead of striking the song toward session-disable.
                    _lastAttempt.Remove(key);
                    _shortRetry[key] = DateTime.UtcNow;
                    return;
                }

                _lastAttempt[key] = DateTime.UtcNow;
                _failures[key] = (_failures.TryGetValue(key, out int f) ? f : 0) + 1;
                return;
            }

            _lastAttempt[key] = DateTime.UtcNow;
            _failures.Remove(key);

            // Enemy debuff song: timer-based tracking keyed to the target it was cast on.
            if (ctx != null && ctx.TargetId != 0)
            {
                _enemySongs[ctx.Family] = new EnemySongState
                {
                    TargetId = ctx.TargetId,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(ctx.DurationSeconds),
                };
                return;
            }

            _lastSongFamily = ctx != null ? ctx.Family : FamilyOf(candidate.ActionName);
            _lastSongCastUtc = DateTime.UtcNow;

            List<SungSong> sung = SungFor(candidate.TargetName);

            SungSong existing = null;
            foreach (SungSong s in sung)
            {
                if (s.Name.Equals(candidate.ActionName, StringComparison.OrdinalIgnoreCase))
                {
                    existing = s;
                    break;
                }
            }

            if (existing != null)
            {
                // Overwrite-same rule: re-singing refreshes that song in place.
                existing.LandedUtc = DateTime.UtcNow;
                if (ctx != null)
                {
                    existing.DurationSeconds = ctx.DurationSeconds;
                    existing.WithTroubadour = ctx.WithTroubadour;
                    existing.WithSoulVoice = ctx.WithSoulVoice;
                }
            }
            else
            {
                sung.Add(new SungSong
                {
                    Name = candidate.ActionName,
                    Family = ctx != null ? ctx.Family : FamilyOf(candidate.ActionName),
                    LandedUtc = DateTime.UtcNow,
                    DurationSeconds = ctx != null ? ctx.DurationSeconds : 180,
                    WithTroubadour = ctx != null && ctx.WithTroubadour,
                    WithSoulVoice = ctx != null && ctx.WithSoulVoice,
                });

                // Overwrite-lowest-duration rule: the oldest estimate drops past the
                // maximum trackable set (5 song slots + 2 cold-start dummies).
                if (sung.Count > 7)
                {
                    sung.RemoveAt(0);
                }
            }
        }

        // ---------- rotation / slot model ----------

        /// <summary>
        /// Effective rotation. Cold start (no song buffs at all) with a multi-song
        /// instrument whose slots the real rotation fills: dummies are sacrificial
        /// placeholders for the instrument-granted slots, placed per
        /// <see cref="BardSettings.DummyPlacement"/> ("Middle": first two real songs,
        /// then dummies, then the rest; "First": dummies before everything) and retired
        /// once they were all seen up together — the remaining real songs then overwrite
        /// them (dummies are sung without duration gear, so they are always the
        /// lowest-duration song). The end state is identical either way: every party
        /// song up at full strength. Otherwise dummies only pad SPARE slots (more
        /// instrument slots than real songs) and are kept up like any other rotation
        /// song (Singer parity); a cold start without spare slots sings the real
        /// rotation directly, since dummies could not survive the overwrites anyway. <paramref name="realCount"/> is the non-dummy
        /// count, for the slot-overflow warning.
        /// </summary>
        private List<string> BuildRotation(BardSettings bard, PlayerState pl, int slots, out int realCount)
        {
            var real = new List<string>();

            if (bard.UseClarionCall && !string.IsNullOrWhiteSpace(bard.ClarionSong))
            {
                AddValid(real, bard.ClarionSong);
            }

            AddValid(real, bard.PartySongs);
            realCount = real.Count;

            var dummies = new List<string>();
            AddValid(dummies, bard.DummySongs);

            int songBuffs = CountSongBuffs(pl.Buffs);
            if (songBuffs == 0)
            {
                // Full structure lost: (re)start the pass, but only when dummies would
                // actually be sacrificial — extra instrument slots and no free slot.
                _coldStartPass = dummies.Count > 0 && slots > 2 && real.Count >= slots;
                _coldStartDummiesDone = false;
            }
            else if (_coldStartPass)
            {
                if (!_coldStartDummiesDone && AllFamiliesObserved(dummies, pl.Buffs))
                {
                    _coldStartDummiesDone = true;
                }

                if (AllFamiliesObserved(real, pl.Buffs))
                {
                    _coldStartPass = false;
                }
            }

            var rotation = new List<string>();
            if (_coldStartPass)
            {
                bool middle = !string.Equals(bard.DummyPlacement, "First", StringComparison.OrdinalIgnoreCase);
                if (middle)
                {
                    rotation.Add(real[0]);
                    rotation.Add(real[1]);
                }

                if (!_coldStartDummiesDone)
                {
                    rotation.AddRange(dummies);
                }

                for (int i = middle ? 2 : 0; i < real.Count; i++)
                {
                    rotation.Add(real[i]);
                }
            }
            else if (slots > real.Count)
            {
                // Spare slots: dummies are permanent padding (Singer parity). Dummies are
                // NOT padded on a cold start without spare slots (no multi-song instrument
                // or none declared) — they could not survive the real songs' overwrites,
                // so singing them first would be two wasted casts.
                rotation.AddRange(dummies);
                rotation.AddRange(real);
            }
            else
            {
                rotation.AddRange(real);
            }

            return rotation;
        }

        /// <summary>Every song family in the list is observed on the PL at its configured count.</summary>
        private static bool AllFamiliesObserved(List<string> songs, short[] buffs)
        {
            if (songs.Count == 0)
            {
                return false;
            }

            var seen = new HashSet<StatusEffect>();
            foreach (string name in songs)
            {
                StatusEffect family = FamilyOf(name);
                if (family == 0 || !seen.Add(family))
                {
                    continue;
                }

                if (CountFamily(buffs, family) < CountInRotation(songs, family))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddValid(List<string> rotation, List<string> songs)
        {
            foreach (string song in songs)
            {
                AddValid(rotation, song);
            }
        }

        private static void AddValid(List<string> rotation, string song)
        {
            if (string.IsNullOrWhiteSpace(song))
            {
                return;
            }

            string trimmed = song.Trim();
            if (FamilyOf(trimmed) == 0)
            {
                return;
            }

            if (!rotation.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                rotation.Add(trimmed);
            }
        }

        private List<SungSong> SungFor(string scope)
        {
            if (!_sung.TryGetValue(scope, out List<SungSong> sung))
            {
                sung = new List<SungSong>();
                _sung[scope] = sung;
            }

            return sung;
        }

        /// <summary>Dispel/expiry removes the lowest-duration song: drop oldest tracked entries past the observed count.</summary>
        private static void TrimSungToObserved(List<SungSong> sung, StatusEffect family, int observed)
        {
            for (int i = sung.Count - 1; i >= 0 && CountSung(sung, family) > observed; i--)
            {
                if (sung[i].Family == family)
                {
                    sung.RemoveAt(i);
                }
            }
        }

        /// <summary>First configured family song not believed active; null when all are tracked.</summary>
        private static string SongToResing(List<SungSong> sung, List<string> rotation, StatusEffect family)
        {
            foreach (string name in rotation)
            {
                if (FamilyOf(name) != family)
                {
                    continue;
                }

                bool tracked = false;
                foreach (SungSong s in sung)
                {
                    if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        tracked = true;
                        break;
                    }
                }

                if (!tracked)
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>
        /// First tracked rotation song of this family that predates the currently active
        /// Soul Voice / Troubadour buff. Duration and potency are locked in at cast time,
        /// so re-singing under the JA is a straight upgrade (overwrite-same refreshes it).
        /// </summary>
        private static SungSong NeedsUpgrade(List<SungSong> sung, List<string> rotation, StatusEffect family, PlayerState pl)
        {
            bool soulVoice = pl.HasBuff(StatusEffect.Soul_Voice);
            bool troubadour = pl.HasBuff(StatusEffect.Troubadour);
            if (!soulVoice && !troubadour)
            {
                return null;
            }

            foreach (SungSong s in sung)
            {
                if (s.Family != family || !rotation.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((soulVoice && !s.WithSoulVoice) || (troubadour && !s.WithTroubadour))
                {
                    return s;
                }
            }

            return null;
        }

        private SungSong EarliestExpiring(List<SungSong> sung, List<string> rotation, StatusEffect family, double marginSeconds)
        {
            SungSong earliest = null;
            foreach (SungSong s in sung)
            {
                if (s.Family != family || !rotation.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                double remaining = (s.LandedUtc.AddSeconds(s.DurationSeconds) - DateTime.UtcNow).TotalSeconds;
                if (remaining < marginSeconds && (earliest == null || s.LandedUtc < earliest.LandedUtc))
                {
                    earliest = s;
                }
            }

            return earliest;
        }

        // ---------- abilities ----------

        private AbilityState AbilityStateOf(PlayerState pl, string abilityName, StatusEffect buff)
        {
            if (pl.HasBuff(buff))
            {
                return AbilityState.Active;
            }

            if (_state.GetAbilityRecast(abilityName) != 0 || !_state.HasAbility(abilityName))
            {
                return AbilityState.Unavailable;
            }

            if (_jaAttempted.TryGetValue(abilityName, out DateTime attempted)
                && DateTime.UtcNow - attempted < JaVerifyWindow)
            {
                return AbilityState.PendingVerify;
            }

            return AbilityState.Ready;
        }

        private static ActionCandidate Ja(string name, double score)
        {
            return new ActionCandidate
            {
                Category = ActionCategory.JobAbility,
                Score = score,
                ActionName = name,
                TargetName = "<me>",
                IsJobAbility = true,
                Description = name + " (bard JA)",
            };
        }

        // ---------- helpers ----------

        // Songs cost no MP — only known + recast gate them.
        private bool IsSongCastable(SongAvailability availability, string songName, out string reason)
        {
            bool instrumentGranted = songName.Equals("Honor March", StringComparison.OrdinalIgnoreCase)
                || songName.Equals("Aria of Passion", StringComparison.OrdinalIgnoreCase);

            if (songName.Equals("Honor March", StringComparison.OrdinalIgnoreCase) && !availability.HonorMarch)
            {
                reason = "requires Marsyas (not declared in settings and not found in inventory)";
                return false;
            }

            if (songName.Equals("Aria of Passion", StringComparison.OrdinalIgnoreCase) && !availability.AriaOfPassion)
            {
                reason = "requires Loughnashade (not declared in settings and not found in inventory)";
                return false;
            }

            EliteAPI.ISpell spell = _state.FindSpell(songName);
            if (spell == null)
            {
                // Honor March is missing from some resource DB versions (legacy special-cased
                // its recast the same way). Offer it; repeated failures disable it.
                if (instrumentGranted)
                {
                    reason = null;
                    return true;
                }

                reason = "not in the resource database (check the spelling)";
                return false;
            }

            if (!instrumentGranted)
            {
                // NB: ISpell.Index is the real spell id (known-mask/recast key); ISpell.ID is a
                // different identifier for songs (e.g. Minuet V: Index 398, ID 763).
                // Instrument-granted songs skip this: the client only "knows" Honor March /
                // Aria of Passion while Marsyas / Loughnashade is equipped, and GearSwap only
                // equips the instrument mid-cast — the mask is never set at decision time.
                // The instrument gate above already vouched for availability.
                if (!_state.KnowsSpell(spell.Index) || !_state.MeetsSpellLevelRequirement(spell))
                {
                    reason = "not learned at this job level";
                    return false;
                }
            }

            int recast = _state.GetSpellRecast(spell.Index);
            if (recast != 0)
            {
                reason = "on recast (" + recast + ")";
                return false;
            }

            reason = null;
            return true;
        }

        // ---------- instrument / slot model ----------

        /// <summary>
        /// Known instruments: item id, name, ADDITIONAL SONG SLOTS granted.
        /// Marsyas and Gjallarhorn are potency/duration instruments and grant NO extra
        /// song slots — they remain in the table so Honor March (Marsyas only) and
        /// named declaration/detection resolve. The slot-granting instruments are
        /// Daurdabla (+2 at 90/99, +1 below), Terpander, Blurred Harp/+1, and
        /// Loughnashade (stage-dependent: 22305/22306/22307 = stage I/II/III).
        /// Honor March requires Marsyas; Aria of Passion requires any Loughnashade.
        /// </summary>
        private static readonly (int ItemId, string Name, int BonusSlots)[] Instruments =
        {
            (21398, "Marsyas", 0),
            (18342, "Gjallarhorn", 0),
            (18572, "Gjallarhorn", 0),
            (18577, "Gjallarhorn", 0),
            (18578, "Gjallarhorn", 0),
            (18579, "Gjallarhorn", 0),
            (18580, "Gjallarhorn", 0),
            (18840, "Gjallarhorn", 0),
            (18571, "Daurdabla", 2),
            (18839, "Daurdabla", 2),
            (18574, "Daurdabla", 1),
            (18575, "Daurdabla", 1),
            (18576, "Daurdabla", 1),
            (21400, "Blurred Harp", 1),
            (21401, "Blurred Harp +1", 1),
            (21407, "Terpander", 1),
            (22305, "Loughnashade", 0),
            (22306, "Loughnashade", 1),
            (22307, "Loughnashade", 2),
        };

        private const int MarsyasItemId = 21398;

        /// <summary>Distinct known instrument names, for the settings UI pickers.</summary>
        public static readonly IReadOnlyList<string> KnownInstrumentNames =
            Instruments.Select(i => i.Name).Distinct().ToList();

        /// <summary>
        /// Resolves the slot count and instrument-gated song availability. A declared
        /// <see cref="BardSettings.OwnedInstruments"/> list wins: slots = 2 + best bonus
        /// in the list, Honor March / Aria of Passion from list membership. Cure-Please
        /// never touches equipment — the player's GearSwap lua equips the right
        /// instrument per song, so the live equipped state is NOT consulted when a
        /// declaration exists (it is transient GearSwap output, not input). With no
        /// declaration: equipped Range slot first, then best known instrument found in
        /// the owned-items scan.
        /// </summary>
        private SongAvailability ComputeAvailability(BardSettings bard, PlayerState pl)
        {
            var availability = new SongAvailability { BaseSlots = 2, InstrumentName = "none (2 slots)" };

            if (bard.OwnedInstruments != null && bard.OwnedInstruments.Count > 0)
            {
                int bestBonus = 0;
                var matchedNames = new List<string>();
                foreach (string declared in bard.OwnedInstruments)
                {
                    string trimmed = declared?.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        continue;
                    }

                    bool matched = false;
                    foreach ((int itemId, string name, int bonus) in Instruments)
                    {
                        if (trimmed.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (bonus > bestBonus)
                            {
                                bestBonus = bonus;
                            }
                            availability.HonorMarch = availability.HonorMarch || itemId == MarsyasItemId;
                            availability.AriaOfPassion = availability.AriaOfPassion
                                || name.Equals("Loughnashade", StringComparison.OrdinalIgnoreCase);
                            if (!matchedNames.Contains(name))
                            {
                                matchedNames.Add(name);
                            }
                            matched = true;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        LogSkip("instr", "Bard: declared instrument \"" + trimmed + "\" is not a known slot instrument — ignored");
                    }
                }

                if (matchedNames.Count > 0)
                {
                    availability.BaseSlots = 2 + bestBonus;
                    availability.InstrumentName = string.Join("+", matchedNames) + " (declared, +" + bestBonus + ")";
                }
            }
            else
            {
                // Auto-detect. The equipped Range-slot instrument is the best guess for
                // players without GearSwap; fall back to the owned-items scan when the
                // equipment table or the slot itself is unreadable.
                int equippedId = EquippedRangeItemId();
                bool resolved = false;
                if (equippedId > 0)
                {
                    foreach ((int itemId, string name, int bonus) in Instruments)
                    {
                        if (itemId == equippedId)
                        {
                            availability.BaseSlots = 2 + bonus;
                            availability.InstrumentName = name + " (equipped, +" + bonus + ")";
                            availability.HonorMarch = itemId == MarsyasItemId;
                            availability.AriaOfPassion = name.Equals("Loughnashade", StringComparison.OrdinalIgnoreCase);
                            resolved = true;
                            break;
                        }
                    }

                    if (!resolved)
                    {
                        // A non-slot instrument is equipped (or id unknown): 2 slots.
                        resolved = true;
                    }
                }

                if (!resolved)
                {
                    int bestBonus = -1;
                    foreach ((int itemId, string name, int bonus) in Instruments)
                    {
                        if (bonus > bestBonus && Owns(itemId))
                        {
                            bestBonus = bonus;
                            availability.BaseSlots = 2 + bonus;
                            availability.InstrumentName = name + " (detected, +" + bonus + ")";
                            availability.HonorMarch = itemId == MarsyasItemId;
                            availability.AriaOfPassion = name.Equals("Loughnashade", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                // Covers "Marsyas owned but not equipped/detected" in auto-detect mode.
                availability.HonorMarch = availability.HonorMarch || Owns(MarsyasItemId);
            }

            availability.EffectiveSlots = availability.BaseSlots + (pl.HasBuff(StatusEffect.Clarion_Call) ? 1 : 0);
            return availability;
        }

        private bool Owns(int itemId)
        {
            return _state.GetOwnedItemIds().Contains(itemId);
        }

        /// <summary>
        /// Item id in the Range equipment slot, or 0 when empty/unreadable (a 0 also
        /// covers "equipment table unavailable", so callers must fall back to the
        /// owned-items scan in that case).
        /// </summary>
        private int EquippedRangeItemId()
        {
            IReadOnlyDictionary<int, int> equipped = _state.GetEquippedItemIds();
            return equipped.TryGetValue((int)EquipmentSlot.Range, out int id) ? id : 0;
        }

        private bool OnBackoff(string key)
        {
            if (_lastAttempt.TryGetValue(key, out DateTime attempted)
                && DateTime.UtcNow - attempted < RetryBackoff)
            {
                return true;
            }

            return _shortRetry.TryGetValue(key, out DateTime retry)
                && DateTime.UtcNow - retry < TransientRetry;
        }

        /// <summary>Not on any backoff and not session-disabled — OfferSong would accept it.</summary>
        private bool Offerable(string songName)
        {
            string key = songName + "|<me>";
            return !OnBackoff(key) && !IsDisabled(key);
        }

        private bool IsDisabled(string key)
        {
            return _failures.TryGetValue(key, out int f) && f >= MaxConsecutiveFailures;
        }

        private bool AnyMemberMissingFamily(GameSnapshot snapshot, PlayerState pl, BardSettings bard, StatusEffect family, int configuredCount)
        {
            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (!member.Active || member.CurrentHP == 0 || member.Zone != pl.ZoneId)
                {
                    continue;
                }

                short[] buffs = BuffsFor(snapshot, member.Name);
                if (buffs == null || CountFamily(buffs, family) >= configuredCount)
                {
                    continue;
                }

                // The addon may have confirmed a landing whose buff packet hasn't arrived yet.
                if (RecentlyLandedOn(family, member.Name))
                {
                    continue;
                }

                if (_state.GetEntityDistance(member.TargetIndex) <= bard.SongRangeYalms)
                {
                    return true;
                }
            }

            return false;
        }

        private bool InRangeOf(GameSnapshot snapshot, PlayerState pl, string targetName, double range)
        {
            if (targetName.Equals(pl.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (PartyMemberState member in snapshot.PartyMembers)
            {
                if (member.Active && member.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return member.Zone == pl.ZoneId && _state.GetEntityDistance(member.TargetIndex) <= range;
                }
            }

            // Monitored character may be out of party; without an entity index we cannot
            // measure it — assume in range (it is typically the character being played).
            return !string.IsNullOrEmpty(snapshot.MonitoredName)
                && targetName.Equals(snapshot.MonitoredName, StringComparison.OrdinalIgnoreCase);
        }

        private static short[] BuffsFor(GameSnapshot snapshot, string name)
        {
            if (name.Equals(snapshot.Player.Name, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot.Player.Buffs;
            }

            if (!string.IsNullOrEmpty(snapshot.MonitoredName)
                && name.Equals(snapshot.MonitoredName, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot.MonitoredBuffs;
            }

            return snapshot.PartyBuffs.TryGetValue(name, out short[] buffs) ? buffs : null;
        }

        private static int CountFamily(short[] buffs, StatusEffect family)
        {
            int count = 0;
            foreach (short buff in buffs)
            {
                if (buff == (short)family)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSung(List<SungSong> sung, StatusEffect family)
        {
            int count = 0;
            foreach (SungSong s in sung)
            {
                if (s.Family == family)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountInRotation(List<string> rotation, StatusEffect family)
        {
            int count = 0;
            foreach (string name in rotation)
            {
                if (FamilyOf(name) == family)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSongBuffs(short[] buffs)
        {
            int count = 0;
            foreach (short buff in buffs)
            {
                if (buff >= 192 && buff <= 222)
                {
                    count++;
                }
            }

            return count;
        }

        private static readonly (string Keyword, StatusEffect Family)[] Families =
        {
            ("Requiem", StatusEffect.Requiem),
            ("Lullaby", StatusEffect.Lullaby),
            ("Elegy", StatusEffect.Elegy),
            ("Nocturne", StatusEffect.Nocturne),
            ("Aria", StatusEffect.Aria),
            ("Paeon", StatusEffect.Paeon),
            ("Ballad", StatusEffect.Ballad),
            ("Minne", StatusEffect.Minne),
            ("Minuet", StatusEffect.Minuet),
            ("Madrigal", StatusEffect.Madrigal),
            ("Prelude", StatusEffect.Prelude),
            ("Mambo", StatusEffect.Mambo),
            ("March", StatusEffect.March),
            ("Etude", StatusEffect.Etude),
            ("Carol", StatusEffect.Carol),
            ("Threnody", StatusEffect.Threnody),
            ("Hymnus", StatusEffect.Hymnus),
            ("Mazurka", StatusEffect.Mazurka),
            ("Sirvente", StatusEffect.Sirvente),
            ("Dirge", StatusEffect.Dirge),
            ("Scherzo", StatusEffect.Scherzo),
        };

        /// <summary>Song buff family for a song name (e.g. "Valor Minuet V" -> Minuet). 0 when not a song.</summary>
        public static StatusEffect FamilyOf(string songName)
        {
            if (string.IsNullOrWhiteSpace(songName))
            {
                return 0;
            }

            foreach ((string keyword, StatusEffect family) in Families)
            {
                if (songName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return family;
                }
            }

            return 0;
        }
    }
}
