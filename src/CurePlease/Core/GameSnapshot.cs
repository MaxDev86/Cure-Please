using System;
using System.Collections.Generic;
using FFXI.Memory;

namespace CurePlease.Core
{
    /// <summary>
    /// Immutable view of one party/alliance slot, copied from the API once per tick.
    /// </summary>
    public sealed class PartyMemberState
    {
        public byte MemberNumber { get; set; }
        public byte EntityIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint ServerId { get; set; }
        public uint TargetIndex { get; set; }
        public uint CurrentHP { get; set; }
        public uint CurrentMP { get; set; }
        public uint CurrentTP { get; set; }
        public byte CurrentHPP { get; set; }
        public byte CurrentMPP { get; set; }
        public ushort Zone { get; set; }
        public byte MainJob { get; set; }
        public byte MainJobLevel { get; set; }
        public byte SubJob { get; set; }
        public byte SubJobLevel { get; set; }
        public bool Active { get; set; }

        /// <summary>Estimated HP missing, derived from HPP (0 when HPP is 0 or 100).</summary>
        public uint EstimatedHpLoss
        {
            get
            {
                if (CurrentHP == 0 || CurrentHPP == 0 || CurrentHPP >= 100)
                {
                    return 0;
                }

                return (CurrentHP * 100 / CurrentHPP) - CurrentHP;
            }
        }
    }

    /// <summary>
    /// Immutable view of the local player (the character running the bot), copied once per tick.
    /// </summary>
    public sealed class PlayerState
    {
        public string Name { get; set; } = string.Empty;
        public uint HP { get; set; }
        public uint MP { get; set; }
        public uint HPP { get; set; }
        public uint MPP { get; set; }
        public uint TP { get; set; }
        public uint HPMax { get; set; }
        public uint MPMax { get; set; }
        public byte MainJob { get; set; }
        public byte MainJobLevel { get; set; }
        public byte SubJob { get; set; }
        public byte SubJobLevel { get; set; }
        public int ZoneId { get; set; }
        public EntityStatus EntityStatus { get; set; }
        public short[] Buffs { get; set; } = Array.Empty<short>();

        public uint EstimatedHpLoss
        {
            get
            {
                if (HP == 0 || HPP == 0 || HPP >= 100)
                {
                    return 0;
                }

                return (HP * 100 / HPP) - HP;
            }
        }

        public bool HasBuff(StatusEffect effect)
        {
            foreach (short buff in Buffs)
            {
                if (buff == (short)effect)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// One consistent read of everything the engine needs for a decision tick.
    /// Built once per tick by <see cref="GameStateService"/> and shared by all modules.
    /// </summary>
    public sealed class GameSnapshot
    {
        public DateTime Timestamp { get; set; }

        /// <summary>The character running the bot. Null when not attached or not logged in.</summary>
        public PlayerState Player { get; set; }

        /// <summary>All 18 party/alliance slots; check <see cref="PartyMemberState.Active"/>.</summary>
        public IReadOnlyList<PartyMemberState> PartyMembers { get; set; } = Array.Empty<PartyMemberState>();

        /// <summary>Current main target's server id; 0 when nothing is targeted.</summary>
        public uint TargetId { get; set; }

        /// <summary>Current main target's display name; empty when nothing is targeted.</summary>
        public string TargetName { get; set; } = string.Empty;

        /// <summary>Current main target's HP percentage (0..100).</summary>
        public byte TargetHpp { get; set; }

        /// <summary>Name of the monitored character (second attached process), if any.</summary>
        public string MonitoredName { get; set; }

        /// <summary>The monitored character's buffs, when a second process is attached.</summary>
        public short[] MonitoredBuffs { get; set; } = Array.Empty<short>();

        /// <summary>
        /// Party-member buffs from the addon provider (own party only — the server
        /// never sends other parties' buffs). Keyed by character name, case-insensitive.
        /// Empty when the addon is not running.
        /// </summary>
        public IReadOnlyDictionary<string, short[]> PartyBuffs { get; set; } =
            new Dictionary<string, short[]>(StringComparer.OrdinalIgnoreCase);

        public bool IsLoggedIn => Player != null;
    }
}
