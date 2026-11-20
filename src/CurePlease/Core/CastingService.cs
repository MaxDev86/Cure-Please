using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CurePlease.Core
{
    public enum CastOutcome
    {
        /// <summary>The addon reported the completed action packet.</summary>
        Success,

        /// <summary>The addon reported an interrupt (movement, stun, etc.).</summary>
        Interrupted,

        /// <summary>The addon reported a reject with no subsequent finish (recast, no MP, invalid target, silence).</summary>
        NotStarted,

        /// <summary>No action-packet outcome arrived within the maximum wait.</summary>
        Timeout,

        /// <summary>No completion arrived; live buffs and recasts must reconcile the result.</summary>
        Unknown,

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

        private static readonly TimeSpan MaxCastDuration = TimeSpan.FromSeconds(20);
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
        /// GearSwap duplicate commands can produce a blocked report immediately before
        /// the real cast finishes. Allow a Finished packet this long to disprove the
        /// reject before declaring NotStarted; no cast-bar input is consulted.
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
        /// unlock paths), this is just another input to the single WatchCastEvents loop —
        /// the cast slot is still released exactly once, in finally.
        /// </summary>
        private int _addonCastEventSpellId;

        public void NotifyCastEvent(CastEventKind kind, int spellId = 0)
        {
            Interlocked.Exchange(ref _addonCastEventSpellId, spellId);
            Interlocked.Exchange(ref _addonCastEventRaw, (int)kind + 1);
        }

        /// <summary>Consumes the pending addon cast event, or null when none has arrived.</summary>
        private CastEventKind? TakeAddonEvent(out int spellId)
        {
            int raw = Interlocked.Exchange(ref _addonCastEventRaw, 0);
            spellId = raw == 0 ? 0 : Interlocked.Exchange(ref _addonCastEventSpellId, 0);
            return raw == 0 ? (CastEventKind?)null : (CastEventKind)(raw - 1);
        }

        /// <summary>True while a cast is being executed (for UI display only — never used as a gate).</summary>
        public bool IsCasting => _castSlot.CurrentCount == 0;

        /// <summary>
        /// Sends /ma "spell" target and awaits the outcome.
        /// Returns as soon as the cast resolves; never blocks the caller's thread.
        /// </summary>
        public async Task<CastResult> CastAsync(
            string spellName, string targetName, CancellationToken ct, TimeSpan? successSettle = null)
        {
            CastResult result = new CastResult { SpellName = spellName, TargetName = targetName };
            Stopwatch sw = Stopwatch.StartNew();

            await _castSlot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
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
                // during the stationary wait belongs to someone else's cast, and
                // clearing here keeps it from being misattributed to ours.
                Interlocked.Exchange(ref _addonCastEventRaw, 0);

                if (!_state.SendCommand("/ma \"" + spellName + "\" " + targetName))
                {
                    result.Outcome = CastOutcome.SendFailed;
                    result.Detail = "SendCommand returned false (detached?)";
                    return result;
                }

                result.Outcome = await WatchCastEvents(result, ct, successSettle).ConfigureAwait(false);
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
        /// Waits for spell-correlated 0x028 action events. The cast bar is NEVER used.
        /// Started is only progress evidence, Finished is completion, and Rejected is
        /// a failed start. A rejected report gets a short grace window because
        /// GearSwap duplicate commands can produce a stray reject before the finish.
        /// </summary>
        private async Task<CastOutcome> WatchCastEvents(
            CastResult result, CancellationToken ct, TimeSpan? successSettle)
        {
            Stopwatch total = Stopwatch.StartNew();
            DateTime blockedAt = DateTime.MinValue;
            bool started = false;
            var spell = _state.FindSpell(result.SpellName);
            int expectedSpellId = spell != null
                ? spell.Index
                : result.SpellName.Equals("Honor March", StringComparison.OrdinalIgnoreCase) ? 417
                : result.SpellName.Equals("Aria of Passion", StringComparison.OrdinalIgnoreCase) ? 418
                : 0;

            while (total.Elapsed < MaxCastDuration)
            {
                ct.ThrowIfCancellationRequested();

                CastEventKind? addonEvent = TakeAddonEvent(out int eventSpellId);
                if (addonEvent.HasValue
                    && (expectedSpellId == 0 || eventSpellId == 0 || eventSpellId == expectedSpellId))
                {
                    if (addonEvent == CastEventKind.Started)
                    {
                        started = true;
                    }
                    else if (addonEvent == CastEventKind.Finished)
                    {
                        if (blockedAt != DateTime.MinValue)
                        {
                            result.Detail = "addon reported rejected first, but the cast went through (stray reject — duplicate command?)";
                        }

                        await Task.Delay(successSettle ?? SettleDelay, ct).ConfigureAwait(false);
                        return CastOutcome.Success;
                    }
                    else if (addonEvent == CastEventKind.Interrupted)
                    {
                        return CastOutcome.Interrupted;
                    }
                    else if ((addonEvent == CastEventKind.Blocked || addonEvent == CastEventKind.Rejected)
                        && blockedAt == DateTime.MinValue)
                    {
                        blockedAt = DateTime.UtcNow;
                    }
                }

                if (blockedAt != DateTime.MinValue && DateTime.UtcNow - blockedAt > BlockedGrace)
                {
                    result.Detail = "game rejected the command (addon rejected event; no finish within "
                        + BlockedGrace.TotalSeconds.ToString("0.0") + "s grace)";
                    return CastOutcome.NotStarted;
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            result.Detail = "no matching action-packet outcome within " + MaxCastDuration.TotalSeconds.ToString("0")
                + "s (started=" + started + "; addon missing, event lost, or cast unresolved)";
            return CastOutcome.Unknown;
        }
    }
}
