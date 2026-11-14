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
    /// Listens for the CurePlease Windower/Ashita addon's UDP messages and exposes
    /// them as typed, timestamped state. Replaces the legacy AddonReader
    /// BackgroundWorker (swallowed errors, infinite restart, no staleness).
    ///
    /// Protocol (port 19769 by default):
    ///   CUREPLEASE_buffs_&lt;Name&gt;_&lt;id1,id2,...&gt;   full buff list for a party member
    ///   CUREPLEASE_confirmed                            handshake reply to //cpaddon verify
    ///   CUREPLEASE_casting_finished|interrupted|blocked cast outcome events
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

        /// <summary>Fires on cast outcome events from the addon.</summary>
        public event Action<CastEventKind> CastEvent;

        /// <summary>
        /// Fires when the addon reports a finished action's landing (0x028): the spell
        /// id and the display names of every entity it landed on.
        /// </summary>
        public event Action<int, string[]> SpellLanded;

        /// <summary>Fires when the addon answers a //cpaddon verify handshake.</summary>
        public event Action Handshake;

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
            if (message.Equals("CUREPLEASE_confirmed", StringComparison.Ordinal))
            {
                LastHandshakeUtc = DateTime.UtcNow;
                Handshake?.Invoke();
                return;
            }

            if (message.Equals("CUREPLEASE_casting_finished", StringComparison.Ordinal))
            {
                CastEvent?.Invoke(CastEventKind.Finished);
                return;
            }

            if (message.Equals("CUREPLEASE_casting_interrupted", StringComparison.Ordinal))
            {
                CastEvent?.Invoke(CastEventKind.Interrupted);
                return;
            }

            if (message.Equals("CUREPLEASE_casting_blocked", StringComparison.Ordinal))
            {
                CastEvent?.Invoke(CastEventKind.Blocked);
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
