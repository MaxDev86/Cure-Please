using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFXI.Memory;

namespace CurePlease.Core
{
    public enum ConnectionState
    {
        Detached,
        AttachedNotLoggedIn,
        Connected,
        Lost,
    }

    /// <summary>
    /// Owns the FFXI.Memory API instances (local player + optional monitored process)
    /// and produces one consistent <see cref="GameSnapshot"/> per engine tick.
    /// Replaces the legacy pattern of ~143 GetPartyMembers() calls per tick and
    /// unguarded API field access. Also watches for game-process death.
    /// </summary>
    public sealed class GameStateService
    {
        private EliteAPI _pl;
        private EliteAPI _monitored;
        private int _plProcessId;
        private int _monitoredProcessId;

        public ConnectionState State { get; private set; } = ConnectionState.Detached;

        /// <summary>Raised whenever <see cref="State"/> changes.</summary>
        public event Action<ConnectionState> StateChanged;

        /// <summary>Last error that drove the state to <see cref="ConnectionState.Lost"/>, if any.</summary>
        public string LastError { get; private set; }

        public bool IsAttached => _pl != null;

        public void Attach(int processId)
        {
            Detach();
            _pl = new EliteAPI(processId);
            _plProcessId = processId;
            SetState(ConnectionState.AttachedNotLoggedIn);
        }

        public void AttachMonitored(int processId)
        {
            DetachMonitored();
            _monitored = new EliteAPI(processId);
            _monitoredProcessId = processId;
        }

        public void DetachMonitored()
        {
            _monitored = null;
            _monitoredProcessId = 0;
        }

        public void Detach()
        {
            DetachMonitored();
            _pl = null;
            _plProcessId = 0;
            SetState(ConnectionState.Detached);
        }

        /// <summary>
        /// Reads the game once and returns an immutable snapshot, or null when the
        /// player is not logged in / the process is gone. Never throws: API access
        /// failures transition the service to <see cref="ConnectionState.Lost"/>.
        /// </summary>
        public GameSnapshot TakeSnapshot()
        {
            if (_pl == null)
            {
                return null;
            }

            if (ProcessGone(_plProcessId) || (_monitoredProcessId != 0 && ProcessGone(_monitoredProcessId)))
            {
                LastError = "Game process exited";
                SetState(ConnectionState.Lost);
                return null;
            }

            try
            {
                if (_pl.Player.LoginStatus != (int)LoginStatus.LoggedIn)
                {
                    SetState(ConnectionState.AttachedNotLoggedIn);
                    return null;
                }

                EliteAPI.PlayerInfo info = _pl.Player.GetPlayerInfo();

                PlayerState player = new PlayerState
                {
                    Name = _pl.Player.Name ?? string.Empty,
                    HP = _pl.Player.HP,
                    MP = _pl.Player.MP,
                    HPP = _pl.Player.HPP,
                    MPP = _pl.Player.MPP,
                    TP = _pl.Player.TP,
                    HPMax = _pl.Player.HPMax,
                    MPMax = _pl.Player.MPMax,
                    MainJob = _pl.Player.MainJob,
                    MainJobLevel = _pl.Player.MainJobLevel,
                    SubJob = _pl.Player.SubJob,
                    SubJobLevel = _pl.Player.SubJobLevel,
                    ZoneId = _pl.Player.ZoneId,
                    EntityStatus = _pl.Player.PlayerState,
                    Buffs = info.Buffs ?? Array.Empty<short>(),
                };

                List<PartyMemberState> members = new List<PartyMemberState>(18);
                foreach (EliteAPI.PartyMember m in _pl.Party.GetPartyMembers())
                {
                    members.Add(new PartyMemberState
                    {
                        MemberNumber = m.MemberNumber,
                        EntityIndex = m.Index,
                        Name = m.Name ?? string.Empty,
                        ServerId = m.ID,
                        TargetIndex = m.TargetIndex,
                        CurrentHP = m.CurrentHP,
                        CurrentMP = m.CurrentMP,
                        CurrentTP = m.CurrentTP,
                        CurrentHPP = m.CurrentHPP,
                        CurrentMPP = m.CurrentMPP,
                        Zone = m.Zone,
                        MainJob = m.MainJob,
                        MainJobLevel = m.MainJobLvl,
                        SubJob = m.SubJob,
                        SubJobLevel = m.SubJobLvl,
                        Active = m.Active != 0,
                    });
                }

                GameSnapshot snapshot = new GameSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    Player = player,
                    PartyMembers = members,
                };

                if (_monitored != null && _monitored.Player.LoginStatus == (int)LoginStatus.LoggedIn)
                {
                    snapshot.MonitoredName = _monitored.Player.Name ?? string.Empty;
                    snapshot.MonitoredBuffs = _monitored.Player.GetPlayerInfo().Buffs ?? Array.Empty<short>();
                }

                SetState(ConnectionState.Connected);
                return snapshot;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetState(ConnectionState.Lost);
                return null;
            }
        }

        /// <summary>Sends a command line to the game (e.g. /ma, /ja, /item). Returns false when detached.</summary>
        public bool SendCommand(string command)
        {
            if (_pl == null || State != ConnectionState.Connected)
            {
                return false;
            }

            _pl.ThirdParty.SendString(command);
            return true;
        }

        /// <summary>Cast bar progress 0..100 for the local player; -1 when unavailable.</summary>
        public float GetCastBarPercent()
        {
            if (_pl == null)
            {
                return -1f;
            }

            try
            {
                return _pl.CastBar.Percent;
            }
            catch
            {
                return -1f;
            }
        }

        /// <summary>Resolves a spell from the resource database; null when unknown. Never throws on bad names.</summary>
        public EliteAPI.ISpell FindSpell(string name)
        {
            if (_pl == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                EliteAPI.ISpell spell = _pl.Resources.GetSpell(name.Trim(), 0);
                if (spell == null || spell.Name == null || spell.Name.Length == 0 || string.IsNullOrEmpty(spell.Name[0]))
                {
                    return null;
                }

                return spell;
            }
            catch
            {
                return null;
            }
        }

        public bool KnowsSpell(uint spellId)
        {
            return _pl != null && _pl.Player.HasSpell(spellId);
        }

        /// <summary>Distance in yalms to an entity by its target index; float.MaxValue when unknown.</summary>
        public float GetEntityDistance(uint targetIndex)
        {
            if (_pl == null)
            {
                return float.MaxValue;
            }

            try
            {
                EliteAPI.XiEntity entity = _pl.Entity.GetEntity((int)targetIndex);
                return entity == null ? float.MaxValue : entity.Distance;
            }
            catch
            {
                return float.MaxValue;
            }
        }

        public int GetSpellRecast(int spellIndex)
        {
            return _pl == null ? -1 : _pl.Recast.GetSpellRecast(spellIndex);
        }

        private static bool ProcessGone(int processId)
        {
            if (processId == 0)
            {
                return true;
            }

            try
            {
                using (Process p = Process.GetProcessById(processId))
                {
                    return p.HasExited;
                }
            }
            catch
            {
                return true;
            }
        }

        private void SetState(ConnectionState newState)
        {
            if (State == newState)
            {
                return;
            }

            State = newState;
            StateChanged?.Invoke(newState);
        }
    }
}
