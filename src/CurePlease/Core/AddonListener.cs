using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CurePlease.Core
{
    public enum CastEventKind
    {
        Started,
        Finished,
        Interrupted,
        Blocked,
        Rejected,
    }

    /// <summary>
    /// Listens for the CurePlease3 Windower addon's UDP messages and exposes
    /// them as typed, timestamped state. Replaces the legacy AddonReader
    /// BackgroundWorker (swallowed errors, infinite restart, no staleness).
    ///
    /// Protocol (port 19771 by default; legacy addon used 19769):
    ///   CUREPLEASE_buffs_&lt;Name&gt;_&lt;id1,id2,...&gt;   full buff list for a party member
    ///   CUREPLEASE_bufftimes_&lt;Name&gt;_&lt;id:seconds,...&gt;
    ///                                                     local buff remaining times (0x063)
    ///   CUREPLEASE_confirmed_&lt;CharName&gt;               v2 handshake reply to //cp3 verify
    ///   CUREPLEASE_confirmed                            legacy handshake (no sender name)
    ///   CUREPLEASE_casting3_&lt;CharName&gt;_&lt;spellId&gt;_&lt;outcome&gt;
    ///                                                     spell-correlated cast events (0x028)
    ///   CUREPLEASE_casting2_&lt;CharName&gt;_finished|interrupted|blocked
    ///                                                     v2 cast outcome events, name-keyed so
    ///                                                     multiple game instances don't cross-talk
    ///   CUREPLEASE_casting_finished|interrupted|blocked   legacy cast events (sender null)
    ///   CUREPLEASE_action2_&lt;CharName&gt;_&lt;spellId&gt;_&lt;name1,...&gt;
    ///                                                     caster-keyed landing report (0x028)
    ///   CUREPLEASE_action_&lt;spellId&gt;_&lt;name1,...&gt;       legacy anonymous landing report
    ///   CUREPLEASE_command_&lt;cmd&gt;                       (ignored; bot-side commands not needed)
    /// </summary>
    public sealed class AddonListener : IDisposable
    {
        private sealed class BuffEntry
        {
            public short[] Buffs;
            public DateTime LastSeenUtc;
        }

        private sealed class BuffTimeEntry
        {
            public BuffTimeRemaining[] Buffs;
            public DateTime LastSeenUtc;
        }

        /// <summary>
        /// Cap on name-keyed dictionaries (_buffs, _handshakesByName). The port is
        /// loopback-only, but any local process can send packets — a flood of random
        /// names must not grow these without bound. 64 is far above the largest
        /// possible roster (18-member alliance + trusts).
        /// </summary>
        private const int MaxTrackedNames = 64;

        private readonly ConcurrentDictionary<string, BuffEntry> _buffs =
            new ConcurrentDictionary<string, BuffEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, BuffTimeEntry> _buffTimes =
            new ConcurrentDictionary<string, BuffTimeEntry>(StringComparer.OrdinalIgnoreCase);

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private Task _loop;

        /// <summary>
        /// Fires on cast outcome events from the addon: caster name (null for legacy
        /// anonymous messages), spell id (0 for legacy casting2), and outcome kind.
        /// </summary>
        public event Action<string, int, CastEventKind> CastEvent;

        /// <summary>
        /// Fires when the addon reports a finished action's landing (0x028): caster name
        /// (null for the legacy anonymous format), spell id, and recipient display names.
        /// </summary>
        public event Action<string, int, string[]> SpellLanded;

        /// <summary>Fires when the addon answers a //cp3 verify handshake. Argument is the character name (v2) or null (legacy).</summary>
        public event Action<string> Handshake;

        /// <summary>Fires on listener errors (bind failures, malformed packets are counted, not reported).</summary>
        public event Action<string> Error;

        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        /// <summary>Time the last CUREPLEASE_confirmed arrived; null if never.</summary>
        public DateTime? LastHandshakeUtc { get; private set; }

        /// <summary>Time the last packet of any kind arrived; null if never.</summary>
        public DateTime? LastPacketUtc { get; private set; }

        /// <summary>True when the addon has answered a handshake recently.</summary>
        public bool Connected => LastHandshakeUtc.HasValue
                                 && (DateTime.UtcNow - LastHandshakeUtc.Value) < TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, DateTime> _handshakesByName =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when the named character's addon answered a handshake recently (v2 name-keyed handshake).</summary>
        public bool ConnectedFor(string characterName)
        {
            return characterName != null
                && _handshakesByName.TryGetValue(characterName, out DateTime when)
                && DateTime.UtcNow - when < TimeSpan.FromMinutes(2);
        }

        public void Start(int port)
        {
            Stop();

            _cts = new CancellationTokenSource();
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (Exception ex)
            {
                Error?.Invoke("UDP bind failed on port " + port + ": " + ex.Message);
                return;
            }

            _loop = Task.Run(() => ReceiveLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _udp?.Close();
            }
            catch
            {
                // Closing a socket during shutdown must never throw.
            }

            _udp = null;
            _loop = null;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Returns a copy of the current buff table, excluding entries older than
        /// <paramref name="staleAfter"/> and, when supplied, names outside
        /// <paramref name="activeNames"/>. Keyed by character name (ordinal-ignore-case).
        /// Stale entries are removed from the shared table without pruning other
        /// instances' live rosters.
        /// </summary>
        public Dictionary<string, short[]> GetBuffSnapshot(
            TimeSpan staleAfter, IReadOnlyCollection<string> activeNames = null)
        {
            Dictionary<string, short[]> result = new Dictionary<string, short[]>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> active = activeNames == null
                ? null
                : new HashSet<string>(activeNames, StringComparer.OrdinalIgnoreCase);
            DateTime cutoff = DateTime.UtcNow - staleAfter;

            foreach (KeyValuePair<string, BuffEntry> pair in _buffs)
            {
                if (pair.Value.LastSeenUtc < cutoff)
                {
                    _buffs.TryRemove(pair.Key, out _);
                }
                else if (active == null || active.Contains(pair.Key))
                {
                    result[pair.Key] = pair.Value.Buffs;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the named local player's buff remaining times, aged down from the
        /// packet's arrival time. Reports remain valid until their longest initial
        /// duration plus the stale grace has elapsed; 0x063 arrives on buff changes,
        /// not at a steady heartbeat.
        /// </summary>
        public BuffTimeRemaining[] GetBuffTimeSnapshot(string characterName, TimeSpan staleAfter)
        {
            if (characterName == null || !_buffTimes.TryGetValue(characterName, out BuffTimeEntry entry))
            {
                return Array.Empty<BuffTimeRemaining>();
            }

            DateTime now = DateTime.UtcNow;
            int longest = entry.Buffs.Length == 0 ? 0 : entry.Buffs.Max(b => b.Seconds);
            if (entry.LastSeenUtc.AddSeconds(longest) < now - staleAfter)
            {
                _buffTimes.TryRemove(characterName, out _);
                return Array.Empty<BuffTimeRemaining>();
            }

            int elapsed = Math.Max(0, (int)(now - entry.LastSeenUtc).TotalSeconds);
            var result = new BuffTimeRemaining[entry.Buffs.Length];
            for (int i = 0; i < entry.Buffs.Length; i++)
            {
                BuffTimeRemaining buff = entry.Buffs[i];
                result[i] = new BuffTimeRemaining
                {
                    BuffId = buff.BuffId,
                    Seconds = Math.Max(0, buff.Seconds - elapsed),
                };
            }

            return result;
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try
                {
                    packet = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Error?.Invoke("UDP receive error: " + ex.Message);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                LastPacketUtc = DateTime.UtcNow;
                string message = Encoding.UTF8.GetString(packet.Buffer);

                try
                {
                    Dispatch(message);
                }
                catch (Exception ex)
                {
                    // A throwing subscriber (or a malformed packet) must not kill the
                    // receive loop — log and keep listening.
                    Error?.Invoke("dispatch error: " + ex.Message);
                    Serilog.Log.Warning(ex, "AddonListener dispatch failed for {Message}", message);
                }
            }
        }

        private void Dispatch(string message)
        {
            if (TryParseHandshake(message, out string confirmedBy))
            {
                LastHandshakeUtc = DateTime.UtcNow;
                if (confirmedBy != null
                    && (_handshakesByName.Count < MaxTrackedNames || _handshakesByName.ContainsKey(confirmedBy)))
                {
                    _handshakesByName[confirmedBy] = DateTime.UtcNow;
                }

                Handshake?.Invoke(confirmedBy);
                return;
            }

            if (TryParseCastEvent(message, out string caster, out int castEventSpellId, out CastEventKind kind))
            {
                CastEvent?.Invoke(caster, castEventSpellId, kind);
                return;
            }

            if (TryParseActionMessage(message, out string actor, out int spellId, out string[] recipients))
            {
                SpellLanded?.Invoke(actor, spellId, recipients);
                return;
            }

            if (TryParseBuffTimeMessage(message, out string timedName, out BuffTimeRemaining[] timedBuffs)
                && (_buffTimes.Count < MaxTrackedNames || _buffTimes.ContainsKey(timedName)))
            {
                _buffTimes[timedName] = new BuffTimeEntry { Buffs = timedBuffs, LastSeenUtc = DateTime.UtcNow };
                return;
            }

            if (TryParseBuffMessage(message, out string name, out short[] buffs)
                && (_buffs.Count < MaxTrackedNames || _buffs.ContainsKey(name)))
            {
                _buffs[name] = new BuffEntry { Buffs = buffs, LastSeenUtc = DateTime.UtcNow };
            }
        }

        /// <summary>
        /// Parses CUREPLEASE_confirmed (legacy; name null) and CUREPLEASE_confirmed_&lt;Name&gt;
        /// (v2 name-keyed). Public and static so the format can be verified without a live socket.
        /// </summary>
        public static bool TryParseHandshake(string message, out string characterName)
        {
            characterName = null;
            const string prefix = "CUREPLEASE_confirmed";

            if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (message.Length == prefix.Length)
            {
                return true; // legacy bare handshake
            }

            if (message[prefix.Length] != '_' || message.Length == prefix.Length + 1)
            {
                return false;
            }

            characterName = message.Substring(prefix.Length + 1);
            return true;
        }

        /// <summary>
        /// Parses spell-correlated casting3 and legacy casting2/casting events.
        /// The casting3 form is CUREPLEASE_casting3_&lt;Name&gt;_&lt;spellId&gt;_&lt;outcome&gt;.
        /// </summary>
        public static bool TryParseCastEvent(string message, out string caster, out CastEventKind kind)
        {
            return TryParseCastEvent(message, out caster, out _, out kind);
        }

        public static bool TryParseCastEvent(
            string message, out string caster, out int spellId, out CastEventKind kind)
        {
            caster = null;
            spellId = 0;
            kind = default;
            if (message == null)
            {
                return false;
            }

            const string v3Prefix = "CUREPLEASE_casting3_";
            if (message.StartsWith(v3Prefix, StringComparison.Ordinal))
            {
                int casterEnd = message.IndexOf('_', v3Prefix.Length);
                if (casterEnd <= v3Prefix.Length)
                {
                    return false;
                }

                int separator = message.LastIndexOf('_');
                if (separator <= casterEnd + 1 || separator == message.Length - 1)
                {
                    return false;
                }

                if (!int.TryParse(message.Substring(casterEnd + 1, separator - casterEnd - 1), out spellId)
                    || !TryParseOutcome(message.Substring(separator + 1), out kind))
                {
                    return false;
                }

                caster = message.Substring(v3Prefix.Length, casterEnd - v3Prefix.Length);
                return true;
            }

            const string v2Prefix = "CUREPLEASE_casting2_";
            if (message.StartsWith(v2Prefix, StringComparison.Ordinal))
            {
                int separator = message.LastIndexOf('_');
                if (separator <= v2Prefix.Length || separator == message.Length - 1)
                {
                    return false;
                }

                if (!TryParseOutcome(message.Substring(separator + 1), out kind))
                {
                    return false;
                }

                caster = message.Substring(v2Prefix.Length, separator - v2Prefix.Length);
                return true;
            }

            const string v1Prefix = "CUREPLEASE_casting_";
            if (message.StartsWith(v1Prefix, StringComparison.Ordinal))
            {
                return TryParseOutcome(message.Substring(v1Prefix.Length), out kind);
            }

            return false;
        }

        private static bool TryParseOutcome(string outcome, out CastEventKind kind)
        {
            switch (outcome)
            {
                case "started": kind = CastEventKind.Started; return true;
                case "finished": kind = CastEventKind.Finished; return true;
                case "interrupted": kind = CastEventKind.Interrupted; return true;
                case "blocked": kind = CastEventKind.Blocked; return true;
                case "rejected": kind = CastEventKind.Rejected; return true;
                default: kind = default; return false;
            }
        }

        /// <summary>
        /// Parses caster-keyed CUREPLEASE_action2_&lt;CharName&gt;_&lt;spellId&gt;_&lt;names&gt; and
        /// legacy anonymous CUREPLEASE_action_&lt;spellId&gt;_&lt;names&gt; landing reports.
        /// </summary>
        public static bool TryParseActionMessage(string message, out string caster, out int spellId, out string[] recipients)
        {
            caster = null;
            spellId = 0;
            recipients = null;
            if (message == null)
            {
                return false;
            }

            const string keyedPrefix = "CUREPLEASE_action2_";
            int spellStart;
            if (message.StartsWith(keyedPrefix, StringComparison.Ordinal))
            {
                int casterEnd = message.IndexOf('_', keyedPrefix.Length);
                if (casterEnd <= keyedPrefix.Length)
                {
                    return false;
                }

                caster = message.Substring(keyedPrefix.Length, casterEnd - keyedPrefix.Length);
                spellStart = casterEnd + 1;
            }
            else
            {
                const string legacyPrefix = "CUREPLEASE_action_";
                if (!message.StartsWith(legacyPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                spellStart = legacyPrefix.Length;
            }

            int separator = message.IndexOf('_', spellStart);
            if (separator <= spellStart || separator == message.Length - 1
                || !int.TryParse(message.Substring(spellStart, separator - spellStart), out spellId))
            {
                return false;
            }

            recipients = message.Substring(separator + 1).Split(',');
            return recipients.Length > 0;
        }

        /// <summary>Legacy parser signature for callers that do not need the caster.</summary>
        public static bool TryParseActionMessage(string message, out int spellId, out string[] recipients)
        {
            return TryParseActionMessage(message, out _, out spellId, out recipients);
        }

        /// <summary>
        /// Parses CUREPLEASE_bufftimes_&lt;Name&gt;_&lt;id:remainingSeconds,...&gt;.
        /// Duplicate buff ids are retained because song families share status ids.
        /// </summary>
        public static bool TryParseBuffTimeMessage(
            string message, out string name, out BuffTimeRemaining[] buffs)
        {
            name = null;
            buffs = null;
            const string prefix = "CUREPLEASE_bufftimes_";
            if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            int separator = message.IndexOf('_', prefix.Length);
            if (separator <= prefix.Length)
            {
                return false;
            }

            name = message.Substring(prefix.Length, separator - prefix.Length);
            string list = message.Substring(separator + 1);
            if (list.Length == 0)
            {
                buffs = Array.Empty<BuffTimeRemaining>();
                return true;
            }

            string[] parts = list.Split(',');
            var parsed = new List<BuffTimeRemaining>(parts.Length);
            foreach (string part in parts)
            {
                string[] fields = part.Split(':');
                if (fields.Length != 2
                    || !short.TryParse(fields[0], out short id)
                    || !int.TryParse(fields[1], out int seconds)
                    || seconds < 0)
                {
                    return false;
                }

                parsed.Add(new BuffTimeRemaining { BuffId = id, Seconds = seconds });
            }

            buffs = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// Parses CUREPLEASE_buffs_&lt;Name&gt;_&lt;id1,id2,...&gt;. Public and static so the
        /// format can be verified without a live socket.
        /// </summary>
        public static bool TryParseBuffMessage(string message, out string name, out short[] buffs)
        {
            name = null;
            buffs = null;

            const string prefix = "CUREPLEASE_buffs_";
            if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            int separator = message.IndexOf('_', prefix.Length);
            if (separator <= prefix.Length)
            {
                return false;
            }

            name = message.Substring(prefix.Length, separator - prefix.Length);
            string list = message.Substring(separator + 1);
            if (list.Length == 0)
            {
                buffs = Array.Empty<short>();
                return true;
            }

            string[] parts = list.Split(',');
            List<short> parsed = new List<short>(parts.Length);
            foreach (string part in parts)
            {
                if (!short.TryParse(part, out short id))
                {
                    return false;
                }

                parsed.Add(id);
            }

            buffs = parsed.ToArray();
            return true;
        }
    }
}
