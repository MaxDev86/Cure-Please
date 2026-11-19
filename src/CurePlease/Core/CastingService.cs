using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CurePlease.Core
{
    public enum CastOutcome
    {
        /// <summary>Cast bar reached completion.</summary>
        Success,

        /// <summary>Cast started but the bar dropped before completing (interrupted / moved / stunned).</summary>
        Interrupted,

        /// <summary>The cast never started within the start window (recast, no MP, invalid target, silence).</summary>
        NotStarted,

        /// <summary>The cast bar stalled or ran past its expected duration.</summary>
        Timeout,

        /// <summary>Command could not be sent (detached / connection lost).</summary>
        SendFailed,

        /// <summary>Caller cancelled.</summary>
        Cancelled,
    }

    public sealed class CastResult
    {
        public CastOutcome Outcome { get; set; }
        public string SpellName { get; set; } = string.Empty;
        public string TargetName { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; }

        /// <summary>Diagnostic context for a failed outcome (why the watcher decided), empty on success.</summary>
        public string Detail { get; set; } = string.Empty;

        public bool Succeeded => Outcome == CastOutcome.Success;

        public override string ToString()
        {
            string s = Outcome + ": " + SpellName + " -> " + TargetName + " (" + Elapsed.TotalSeconds.ToString("0.0") + "s)";
            return Detail.Length > 0 ? s + " [" + Detail + "]" : s;
        }
    }

    /// <summary>
    /// Awaitable casting state machine. Replaces the legacy CastingBackground_Check
    /// bool + ProtectCasting BackgroundWorker pair, which could deadlock the bot
    /// forever if the worker faulted. Every path through here releases the cast
    /// slot — there is no state in which the engine stays locked.
    /// </summary>
    public sealed class CastingService
    {
        private readonly GameStateService _state;

        /// <summary>Guarantees one cast in flight; released in finally on every path.</summary>
        private readonly SemaphoreSlim _castSlot = new SemaphoreSlim(1, 1);

        private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxCastDuration = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Bar unchanged this long means the cast completed mid-ramp (fast cast) or,
        /// in WaitForCastBarIdle, that no cast is in progress (frozen leftover bar).
        /// </summary>
        private static readonly TimeSpan FcCompleteStall = TimeSpan.FromMilliseconds(750);

        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// How long the player must stand still before a cast is sent. The client
        /// stops rendering movement immediately, but the server (~Japan RTT) still
        /// considers the character mid-step for a few hundred ms, so casting the
        /// instant movement stops gets the cast eaten server-side.
        /// </summary>
        private static readonly TimeSpan StationarySettle = TimeSpan.FromMilliseconds(500);

        /// <summary>Max time to wait for the player to stop moving before giving up the cast.</summary>
        private static readonly TimeSpan StationaryWindow = TimeSpan.FromSeconds(3);

        /// <summary>
        /// A "blocked" (category 8 / 24931) event is only decisive when no cast ever
        /// starts. When another addon (GearSwap re-issue) or a stale reject produces a
        /// blocked event for a cast that IS going through, the reject races ahead of
        /// the real cast — observed live: songs landing while logged NotStarted. Give
        /// the bar / a Finished event this long to prove the blocked event was stray
        /// before declaring NotStarted.
        /// </summary>
        private static readonly TimeSpan BlockedGrace = TimeSpan.FromMilliseconds(1500);

        // Latest addon cast event, stored as (int)CastEventKind + 1 so that 0 means
        // "no event" (Interlocked works on ints, not Nullable<CastEventKind>).
        // Always go through TakeAddonEvent to decode.
        private int _addonCastEventRaw;

        public CastingService(GameStateService state)
        {
            _state = state;
        }

        /// <summary>
        /// Feeds an addon cast event into the state machine. Unlike legacy (two racing
        /// unlock paths), this is just another input to the single WatchCastBar loop —
        /// the cast slot is still released exactly once, in finally.
        /// </summary>
        public void NotifyCastEvent(CastEventKind kind)
        {
            Interlocked.Exchange(ref _addonCastEventRaw, (int)kind + 1);
        }

        /// <summary>Consumes the pending addon cast event, or null when none has arrived.</summary>
        private CastEventKind? TakeAddonEvent()
        {
            int raw = Interlocked.Exchange(ref _addonCastEventRaw, 0);
            return raw == 0 ? (CastEventKind?)null : (CastEventKind)(raw - 1);
        }

        /// <summary>True while a cast is being executed (for UI display only — never used as a gate).</summary>
        public bool IsCasting => _castSlot.CurrentCount == 0;

        /// <summary>
        /// Sends /ma "spell" target and awaits the outcome.
        /// Returns as soon as the cast resolves; never blocks the caller's thread.
        /// </summary>
        public async Task<CastResult> CastAsync(string spellName, string targetName, CancellationToken ct)
        {
            CastResult result = new CastResult { SpellName = spellName, TargetName = targetName };
            Stopwatch sw = Stopwatch.StartNew();

            await _castSlot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WaitForCastBarIdle(ct).ConfigureAwait(false);

                // Movement interrupts casting — wait for the settle window rather
                // than send a cast the server will eat.
                if (!await WaitForStationary(ct).ConfigureAwait(false))
                {
                    result.Outcome = CastOutcome.NotStarted;
                    result.Detail = "player moving";
                    result.Elapsed = sw.Elapsed;
                    return result;
                }

                // Drop stale addon events as late as possible: anything that arrived
                // during the idle/stationary waits belongs to someone else's cast,
                // and clearing here keeps it from being misattributed to ours.
                Interlocked.Exchange(ref _addonCastEventRaw, 0);

                if (!_state.SendCommand("/ma \"" + spellName + "\" " + targetName))
                {
                    result.Outcome = CastOutcome.SendFailed;
                    result.Detail = "SendCommand returned false (detached?)";
                    return result;
                }

                result.Outcome = await WatchCastBar(result, ct).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Outcome = CastOutcome.Cancelled;
                return result;
            }
            finally
            {
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                _castSlot.Release();
            }
        }

        /// <summary>Sends /item "name" target and waits out the use delay (no cast bar for items).</summary>
        public async Task<CastResult> UseItemAsync(string itemName, string target, CancellationToken ct)
        {
            CastResult result = new CastResult { SpellName = itemName, TargetName = target };
            Stopwatch sw = Stopwatch.StartNew();

            await _castSlot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _addonCastEventRaw, 0);

                if (!_state.SendCommand("/item \"" + itemName + "\" " + target))
                {
                    result.Outcome = CastOutcome.SendFailed;
                    result.Detail = "SendCommand returned false (detached?)";
                    return result;
                }

                // Item use animation is 1-2 s; also covers the post-use lockout so a
                // follow-up /ma is not rejected (same reasoning as JobAbilityAsync).
                await Task.Delay(TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
                result.Outcome = CastOutcome.Success;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Outcome = CastOutcome.Cancelled;
                return result;
            }
            finally
            {
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                _castSlot.Release();
            }
        }

        /// <summary>Sends a job ability (/ja "name" &lt;me&gt;) and verifies the buff appears or recast starts.</summary>
        public async Task<CastResult> JobAbilityAsync(string abilityName, string target, CancellationToken ct)
        {
            CastResult result = new CastResult { SpellName = abilityName, TargetName = target };
            Stopwatch sw = Stopwatch.StartNew();

            await _castSlot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // JAs generate no cast events themselves, but a stale event left over
                // from an earlier cast would poison the next CastAsync — keep the
                // slot clean here too.
                Interlocked.Exchange(ref _addonCastEventRaw, 0);

                if (!_state.SendCommand("/ja \"" + abilityName + "\" " + target))
                {
                    result.Outcome = CastOutcome.SendFailed;
                    result.Detail = "SendCommand returned false (detached?)";
                    return result;
                }

                // JAs have no cast bar; give the server a moment to apply the effect.
                // The wait also covers the post-JA casting lockout — a /ma sent too
                // soon after a JA is rejected with "Unable to cast spells at this
                // time" (category 8 / param 24931). Observed live: JA and a blocked
                // /ma landing in the same second at a 1.5 s gap; 2.5 s clears it.
                await Task.Delay(TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
                result.Outcome = CastOutcome.Success;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Outcome = CastOutcome.Cancelled;
                return result;
            }
            finally
            {
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                _castSlot.Release();
            }
        }

        /// <summary>
        /// Waits until no cast is in progress before a command is sent — a previous
        /// engine cast the watcher mis-detected, or a manual cast by the player (or
        /// another tool). Firing into an active cast makes the game reject the command
        /// with "Unable to cast spells at this time" (action category 8 / param 24931).
        ///
        /// Fast-cast semantics (live-verified with BRD fast-cast gear + NiTro): the
        /// gauge ramps at the BASE cast time, so an adjusted cast completes mid-ramp
        /// and the bar then FREEZES at that value (e.g. 0.25) until the next cast or
        /// a slow idle reset. A frozen mid-ramp value therefore means "no cast in
        /// progress" — a genuinely active cast moves every sample. Bounded so a
        /// stuck bar can never deadlock the engine; a broken read (constant idle
        /// value) degrades this to a no-op, matching prior behavior.
        /// </summary>
        private async Task WaitForCastBarIdle(CancellationToken ct)
        {
            Stopwatch wait = Stopwatch.StartNew();
            float lastRatio = -1f;
            Stopwatch stall = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(12))
            {
                float ratio = _state.GetCastBarPercent();
                if (ratio < 0f || ratio >= 0.98f)
                {
                    return;
                }

                if (Math.Abs(ratio - lastRatio) > 0.001f)
                {
                    stall.Restart();
                    lastRatio = ratio;
                }
                else if (stall.Elapsed > FcCompleteStall)
                {
                    return;
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits until the player has been stationary for <see cref="StationarySettle"/>,
        /// giving up after <see cref="StationaryWindow"/>. Polls the movement tracking
        /// in <see cref="GameStateService"/> (position deltas per snapshot tick).
        /// </summary>
        /// <returns><c>true</c> when the player is settled and safe to cast.</returns>
        private async Task<bool> WaitForStationary(CancellationToken ct)
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (_state.StationaryFor < StationarySettle)
            {
                if (wait.Elapsed > StationaryWindow)
                {
                    return false;
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>
        /// Polls the cast bar: waits for the cast to start, then for completion.
        /// The addon's 0x028 action events (checked first every iteration) are the
        /// primary outcome authority; the bar is the fallback for when the addon is
        /// not loaded. One exception: a Blocked event is reconciled against reality
        /// before it is believed — a stray reject (GearSwap re-issue, duplicate
        /// command) races ahead of the real cast, and trusting it blindly logged
        /// NotStarted for songs that actually landed (live, 2025-11-14 run). Blocked
        /// is ignored once the bar shows a cast in flight, and before a dip it gets a
        /// <see cref="BlockedGrace"/> window for a dip/Finished to disprove it.
        ///
        /// Bar semantics (live-verified, BRD fast-cast gear + NiTro): 1.0 when idle;
        /// ramps 0 -&gt; 1 at the BASE cast time while casting, so a fast-cast-adjusted
        /// cast completes mid-ramp and the bar FREEZES there (e.g. 0.25) until the
        /// next cast or a slow idle reset; a no-fast-cast finish passes through ~1.0;
        /// an interrupt snaps back to 1.0 from mid-ramp. Outcome mapping: freeze
        /// (stall) = completed, snap to 1.0 after reaching ~0.85 = completed,
        /// snap to 1.0 before that with no preceding stall = interrupted.
        /// </summary>
        private async Task<CastOutcome> WatchCastBar(CastResult result, CancellationToken ct)
        {
            Stopwatch total = Stopwatch.StartNew();
            bool started = false;
            bool sawReadError = false;
            bool strayBlocked = false;
            DateTime blockedAt = DateTime.MinValue;
            float maxRatio = 0f;
            float minRatio = 1f;
            float lastRatio = 1f;
            Stopwatch stall = Stopwatch.StartNew();

            while (total.Elapsed < MaxCastDuration)
            {
                ct.ThrowIfCancellationRequested();

                CastEventKind? addonEvent = TakeAddonEvent();
                if (addonEvent == CastEventKind.Finished)
                {
                    if (strayBlocked)
                    {
                        result.Detail = "addon reported blocked first, but the cast went through (stray reject — duplicate command?)";
                    }

                    await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                    return CastOutcome.Success;
                }

                if (addonEvent == CastEventKind.Interrupted)
                {
                    return CastOutcome.Interrupted;
                }

                if (addonEvent == CastEventKind.Blocked)
                {
                    if (started)
                    {
                        // A cast is observably in flight — this reject belongs to a
                        // duplicate/other command, not to ours. The real cast's
                        // Finished event (or bar completion) decides the outcome.
                        strayBlocked = true;
                    }
                    else if (blockedAt == DateTime.MinValue)
                    {
                        // No cast started yet — possibly a genuine reject. Wait out the
                        // grace window: if the bar dips or Finished arrives, the blocked
                        // event was stray (GearSwap re-issue racing the real cast).
                        blockedAt = DateTime.UtcNow;
                    }
                }

                float ratio = _state.GetCastBarPercent();
                if (ratio < 0f)
                {
                    // Read error — hold state and try again next sample.
                    sawReadError = true;
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }

                if (!started)
                {
                    if (ratio < 0.98f)
                    {
                        // The dip below idle marks the cast actually starting.
                        started = true;
                        if (blockedAt != DateTime.MinValue)
                        {
                            strayBlocked = true;
                            blockedAt = DateTime.MinValue;
                        }

                        maxRatio = ratio;
                        lastRatio = ratio;
                        stall.Restart();
                    }
                    else
                    {
                        if (ratio < minRatio)
                        {
                            minRatio = ratio;
                        }

                        if (blockedAt != DateTime.MinValue && DateTime.UtcNow - blockedAt > BlockedGrace)
                        {
                            result.Detail = "game rejected the command (addon blocked event; no cast started within "
                                + BlockedGrace.TotalSeconds.ToString("0.0") + "s grace)";
                            return CastOutcome.NotStarted;
                        }

                        if (total.Elapsed > StartWindow)
                        {
                            result.Detail = "no cast-bar dip in " + StartWindow.TotalSeconds.ToString("0")
                                + "s (min ratio " + minRatio.ToString("0.00") + (sawReadError ? ", read errors seen" : "")
                                + ") — cast either rejected silently or the bar read is broken";
                            return CastOutcome.NotStarted;
                        }
                    }
                }
                else
                {
                    if (ratio > maxRatio)
                    {
                        maxRatio = ratio;
                    }

                    if (ratio >= 0.99f)
                    {
                        await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                        return maxRatio >= 0.85f ? CastOutcome.Success : CastOutcome.Interrupted;
                    }

                    if (Math.Abs(ratio - lastRatio) > 0.001f)
                    {
                        stall.Restart();
                        lastRatio = ratio;
                    }
                    else if (stall.Elapsed > FcCompleteStall)
                    {
                        // Fast-cast completion: the bar froze where the adjusted cast
                        // finished. When the addon is loaded its 0x028 event (above)
                        // normally wins the race; the freeze alone is still decisive —
                        // an interrupted cast snaps back to 1.0 rather than freezing.
                        result.Detail = "completed mid-ramp (fast cast; bar froze at "
                            + lastRatio.ToString("0.00") + ")"
                            + (strayBlocked ? "; stray addon blocked event ignored" : string.Empty);
                        await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                        return CastOutcome.Success;
                    }
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            result.Detail = "cast bar never resolved within " + MaxCastDuration.TotalSeconds.ToString("0")
                + "s (started=" + started + ", max ratio " + maxRatio.ToString("0.00") + ")";
            return CastOutcome.Timeout;
        }
    }
}
