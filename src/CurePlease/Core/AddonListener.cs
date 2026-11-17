using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CurePlease.Core
{
    public enum CastEventKind
    {
        Finished,
        Interrupted,
        Blocked,
    }

    /// <summary>
    /// Listens for the CurePlease3 Windower addon's UDP messages and exposes
    /// them as typed, timestamped state. Replaces the legacy AddonReader
    /// BackgroundWorker (swallowed errors, infinite restart, no staleness).
    ///
    /// Protocol (port 19771 by default; legacy addon used 19769):
    ///   CUREPLEASE_buffs_&lt;Name&gt;_&lt;id1,id2,...&gt;   full buff list for a party member
    ///   CUREPLEASE_confirmed_&lt;CharName&gt;               v2 handshake reply to //cp3 verify
    ///   CUREPLEASE_confirmed                            legacy handshake (no sender name)
    ///   CUREPLEASE_casting2_&lt;CharName&gt;_finished|interrupted|blocked
    ///                                                     v2 cast outcome events, name-keyed so
    ///                                                     multiple game instances don't cross-talk
    ///   CUREPLEASE_casting_finished|interrupted|blocked   legacy cast events (sender null)
    ///   CUREPLEASE_action_&lt;spellId&gt;_&lt;name1,...&gt;       per-target landing report (0x028)
    ///   CUREPLEASE_command_&lt;cmd&gt;                       (ignored; bot-side commands not needed)
    /// </summary>
    public sealed class AddonListener : IDisposable
    {
        private sealed class BuffEntry
        {
            public short[] Buffs;
            public DateTime LastSeenUtc;
        }

        private readonly ConcurrentDictionary<string, BuffEntry> _buffs =
            new ConcurrentDictionary<string, BuffEntry>(StringComparer.OrdinalIgnoreCase);

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private Task _loop;

        /// <summary>
        /// Fires on cast outcome events from the addon. The string is the casting
        /// character's name (v2 name-keyed messages), or null for legacy anonymous
        /// messages — callers driving one attachment should ignore events that name
        /// a different character.
        /// </summary>
        public event Action<string, CastEventKind> CastEvent;

        /// <summary>
        /// Fires when the addon reports a finished action's landing (0x028): the spell
        /// id and the display names of every entity it landed on.
        /// </summary>
        public event Action<int, string[]> SpellLanded;

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
        /// <paramref name="staleAfter"/>. Keyed by character name (ordinal-ignore-case).
        /// </summary>
        public Dictionary<string, short[]> GetBuffSnapshot(TimeSpan staleAfter)
        {
            Dictionary<string, short[]> result = new Dictionary<string, short[]>(StringComparer.OrdinalIgnoreCase);
            DateTime cutoff = DateTime.UtcNow - staleAfter;

            foreach (KeyValuePair<string, BuffEntry> pair in _buffs)
            {
                if (pair.Value.LastSeenUtc >= cutoff)
                {
                    result[pair.Key] = pair.Value.Buffs;
                }
            }

            return result;
        }

        /// <summary>Drops buff entries for anyone not in the given roster (left party, zoned out).</summary>
        public void PruneRoster(IReadOnlyCollection<string> activeNames)
        {
            HashSet<string> active = new HashSet<string>(activeNames, StringComparer.OrdinalIgnoreCase);
            foreach (string name in _buffs.Keys)
            {
                if (!active.Contains(name))
                {
                    _buffs.TryRemove(name, out _);
                }
            }
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
                Dispatch(message);
            }
        }

        private void Dispatch(string message)
        {
            if (TryParseHandshake(message, out string confirmedBy))
            {
                LastHandshakeUtc = DateTime.UtcNow;
                if (confirmedBy != null)
                {
                    _handshakesByName[confirmedBy] = DateTime.UtcNow;
                }

                Handshake?.Invoke(confirmedBy);
                return;
            }

            if (TryParseCastEvent(message, out string caster, out CastEventKind kind))
            {
                CastEvent?.Invoke(caster, kind);
                return;
            }

            if (TryParseActionMessage(message, out int spellId, out string[] recipients))
            {
                SpellLanded?.Invoke(spellId, recipients);
                return;
            }

            if (TryParseBuffMessage(message, out string name, out short[] buffs))
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
        /// Parses cast outcome events: CUREPLEASE_casting2_&lt;Name&gt;_&lt;outcome&gt; (v2,
        /// name-keyed) and CUREPLEASE_casting_&lt;outcome&gt; (legacy; caster null).
        /// Public and static so the format can be verified without a live socket.
        /// </summary>
        public static bool TryParseCastEvent(string message, out string caster, out CastEventKind kind)
        {
            caster = null;
            kind = default;

            if (message == null)
            {
                return false;
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
                case "finished": kind = CastEventKind.Finished; return true;
                case "interrupted": kind = CastEventKind.Interrupted; return true;
                case "blocked": kind = CastEventKind.Blocked; return true;
                default: kind = default; return false;
            }
        }

        /// <summary>
        /// Parses CUREPLEASE_action_&lt;spellId&gt;_&lt;name1,name2,...&gt;. Public and static so the
        /// format can be verified without a live socket.
        /// </summary>
        public static bool TryParseActionMessage(string message, out int spellId, out string[] recipients)
        {
            spellId = 0;
            recipients = null;

            const string prefix = "CUREPLEASE_action_";
            if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            int separator = message.IndexOf('_', prefix.Length);
            if (separator <= prefix.Length || separator == message.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(message.Substring(prefix.Length, separator - prefix.Length), out spellId))
            {
                return false;
            }

            recipients = message.Substring(separator + 1).Split(',');
            return recipients.Length > 0;
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
            if (separator <= prefix.Length || separator == message.Length - 1)
            {
                return false;
            }

            name = message.Substring(prefix.Length, separator - prefix.Length);
            string list = message.Substring(separator + 1);

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
