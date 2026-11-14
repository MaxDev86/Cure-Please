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

        public bool Succeeded => Outcome == CastOutcome.Success;

        public override string ToString()
        {
            return Outcome + ": " + SpellName + " -> " + TargetName + " (" + Elapsed.TotalSeconds.ToString("0.0") + "s)";
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
        private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

        // Latest cast event reported by the addon: 0 none, 1 finished, 2 interrupted, 3 blocked.
        private int _addonCastEvent;

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
            Interlocked.Exchange(ref _addonCastEvent, (int)kind + 1);
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
                Interlocked.Exchange(ref _addonCastEvent, 0);

                if (!_state.SendCommand("/ma \"" + spellName + "\" " + targetName))
                {
                    result.Outcome = CastOutcome.SendFailed;
                    return result;
                }

                result.Outcome = await WatchCastBar(ct).ConfigureAwait(false);
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
                if (!_state.SendCommand("/ja \"" + abilityName + "\" " + target))
                {
                    result.Outcome = CastOutcome.SendFailed;
                    return result;
                }

                // JAs have no cast bar; give the server a moment to apply the effect.
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
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
        /// Polls the cast bar: waits for the cast to start, then for completion.
        /// Completion = bar reaches ~100. Interrupted = bar returns to 0 after starting.
        /// </summary>
        private async Task<CastOutcome> WatchCastBar(CancellationToken ct)
        {
            Stopwatch total = Stopwatch.StartNew();
            bool started = false;
            float lastPercent = 0f;
            Stopwatch stall = Stopwatch.StartNew();

            while (total.Elapsed < MaxCastDuration)
            {
                ct.ThrowIfCancellationRequested();

                int addonEvent = Interlocked.Exchange(ref _addonCastEvent, 0);
                if (addonEvent == (int)CastEventKind.Finished + 1)
                {
                    await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                    return CastOutcome.Success;
                }

                if (addonEvent == (int)CastEventKind.Interrupted + 1)
                {
                    return CastOutcome.Interrupted;
                }

                if (addonEvent == (int)CastEventKind.Blocked + 1)
                {
                    return CastOutcome.NotStarted;
                }

                float percent = _state.GetCastBarPercent();

                if (!started)
                {
                    if (percent > 0f)
                    {
                        started = true;
                    }
                    else if (total.Elapsed > StartWindow)
                    {
                        return CastOutcome.NotStarted;
                    }
                }
                else
                {
                    if (percent >= 100f)
                    {
                        await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                        return CastOutcome.Success;
                    }

                    if (percent <= 0f)
                    {
                        return CastOutcome.Interrupted;
                    }

                    if (Math.Abs(percent - lastPercent) > 0.01f)
                    {
                        stall.Restart();
                        lastPercent = percent;
                    }
                    else if (stall.Elapsed > StallLimit)
                    {
                        return CastOutcome.Timeout;
                    }
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            return CastOutcome.Timeout;
        }
    }
}
