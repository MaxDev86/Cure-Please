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

        /// <summary>
        /// Which party of the alliance this slot belongs to: 0 = own party (slots
        /// 0-5), 1/2 = alliance parties (slots 6-11 / 12-17). Live-verified: the
        /// client always shows the reader's own party in slots 0-5. AoE spells
        /// (Curaga, Protectra, Accession) only hit party index 0.
        /// </summary>
        public byte PartyIndex { get; set; }

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
        public int MainJobMasterLevel { get; set; }
        public byte SubJob { get; set; }
        public byte SubJobLevel { get; set; }
        public int ZoneId { get; set; }
        public EntityStatus EntityStatus { get; set; }
        public short[] Buffs { get; set; } = Array.Empty<short>();

        /// <summary>World position (yalms); follow distance/stuck tracking.</summary>
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

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

    /// <summary>Alliance composition from the client's alliance block (leader ids + per-party member counts).</summary>
    public sealed class AllianceState
    {
        /// <summary>Visible member counts for own party / alliance party 1 / alliance party 2.</summary>
        public byte[] PartyCounts { get; set; } = new byte[3];

        /// <summary>Server ids of each party's leader (0 when that party is absent).</summary>
        public uint[] PartyLeaderIds { get; set; } = new uint[3];

        public bool IsInAlliance => PartyCounts[1] > 0 || PartyCounts[2] > 0;
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

        /// <summary>Alliance composition; IsInAlliance false for solo/single party.</summary>
        public AllianceState Alliance { get; set; } = new AllianceState();

        /// <summary>Current main target's server id; 0 when nothing is targeted.</summary>
        public uint TargetId { get; set; }

        /// <summary>Current main target's entity-array index; 0 when nothing is targeted.</summary>
        public uint TargetIndex { get; set; }

        /// <summary>Current main target's display name; empty when nothing is targeted.</summary>
        public string TargetName { get; set; } = string.Empty;

        /// <summary>Current main target's HP percentage (0..100).</summary>
        public byte TargetHpp { get; set; }

        /// <summary>Name of the monitored character (second attached process), if any.</summary>
        public string MonitoredName { get; set; }

        /// <summary>The monitored character's buffs, when a second process is attached.</summary>
        public short[] MonitoredBuffs { get; set; } = Array.Empty<short>();

        /// <summary>
        /// The monitored character's live entity status (engaged, idle, ...). Only
        /// meaningful when <see cref="MonitoredName"/> is non-empty.
        /// </summary>
        public EntityStatus MonitoredStatus { get; set; }

        /// <summary>The local player's pet entity index; 0 when no pet (e.g. no luopan) is out.</summary>
        public ushort PetIndex { get; set; }

        /// <summary>Pet health percent (0..100); 0 when no pet or unreadable.</summary>
        public byte PetHealthPercent { get; set; }

        /// <summary>Pet distance from the local player in yalms; 0 when no pet or unreadable.</summary>
        public float PetDistance { get; set; }

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
