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

    /// <summary>Snapshot of one entity-array slot, as returned by <see cref="GameStateService.FindEntityByName"/>.</summary>
    public sealed class EntityInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint ServerId { get; set; }

        /// <summary>Raw entity status field (1 = engaged/fighting).</summary>
        public uint Status { get; set; }

        /// <summary>Distance from the local player in yalms.</summary>
        public float Distance { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    /// <summary>
    /// Owns the FFXI.Memory API instances (local player + optional monitored process)
    /// and produces one consistent <see cref="GameSnapshot"/> per engine tick.
    /// Replaces the legacy pattern of ~143 GetPartyMembers() calls per tick and
    /// unguarded API field access. Also watches for game-process death.
    /// </summary>
    public class GameStateService
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
        /// Virtual so headless tests can feed the engine synthetic snapshots.
        /// </summary>
        public virtual GameSnapshot TakeSnapshot()
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

                EliteAPI.TargetInfo target = _pl.Target.GetTargetInfo();
                if (target != null)
                {
                    snapshot.TargetId = target.TargetId;
                    snapshot.TargetName = target.TargetName ?? string.Empty;
                    snapshot.TargetHpp = target.TargetHealthPercent;
                }

                if (_monitored != null && _monitored.Player.LoginStatus == (int)LoginStatus.LoggedIn)
                {
                    snapshot.MonitoredName = _monitored.Player.Name ?? string.Empty;
                    snapshot.MonitoredBuffs = _monitored.Player.GetPlayerInfo().Buffs ?? Array.Empty<short>();
                    snapshot.MonitoredStatus = _monitored.Player.PlayerState;
                }

                try
                {
                    // Pet data is best-effort (GEO luopan). PetIndex is 0 when no pet is
                    // out — entity slot 0 must NOT be read as a pet in that case.
                    snapshot.PetIndex = _pl.Player.PetIndex;
                    if (snapshot.PetIndex != 0)
                    {
                        EliteAPI.XiEntity pet = _pl.Entity.GetEntity(snapshot.PetIndex);
                        if (pet != null)
                        {
                            snapshot.PetHealthPercent = pet.HealthPercent;
                            snapshot.PetDistance = pet.Distance;
                        }
                    }
                }
                catch
                {
                    snapshot.PetIndex = 0;
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
        public virtual bool SendCommand(string command)
        {
            if (_pl == null || State != ConnectionState.Connected)
            {
                return false;
            }

            _pl.ThirdParty.SendString(command);
            return true;
        }

        /// <summary>Cast bar fill ratio for the local player: 1.0 idle, ramps 0 -&gt; 1 while casting; -1 when unavailable.</summary>
        public virtual float GetCastBarPercent()
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
        public virtual EliteAPI.ISpell FindSpell(string name)
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

        public virtual bool KnowsSpell(uint spellId)
        {
            return _pl != null && _pl.Player.HasSpell(spellId);
        }

        /// <summary>
        /// True when the spell is castable on the current main or sub job at its level.
        /// <see cref="KnowsSpell"/> only reports "ever learned" — the client's
        /// known-spell mask is account-wide (Windower's get_spells() exposes the same
        /// mask and likewise lists Cure III on a BRD who once leveled WHM), so this
        /// gate is what stops off-job casts. Mirrors GearSwap's check_spell job/level
        /// test. LevelRequired is indexed by job id; &lt;= 0 = not available to that job.
        /// </summary>
        public virtual bool MeetsSpellLevelRequirement(EliteAPI.ISpell spell)
        {
            if (_pl == null || spell == null || spell.LevelRequired == null)
            {
                return false;
            }

            EliteAPI.PlayerInfo info = _pl.Player.GetPlayerInfo();
            if (info == null || info.MainJob == 0)
            {
                return true; // job unreadable — don't block what we can't evaluate
            }

            return LevelOk(spell.LevelRequired, info.MainJob, info.MainJobLevel)
                || LevelOk(spell.LevelRequired, info.SubJob, info.SubJobLevel);
        }

        private static bool LevelOk(short[] levels, int job, int level)
        {
            return job >= 0 && job < levels.Length && levels[job] > 0 && levels[job] <= level;
        }

        /// <summary>Distance in yalms to an entity by its target index; float.MaxValue when unknown.</summary>
        public virtual float GetEntityDistance(uint targetIndex)
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

        /// <summary>
        /// Finds a spawned entity by display name (case-insensitive) in the client entity
        /// array (2304 slots). Null when not found or unreadable. Costs one full array
        /// scan — callers should use it at tick rate at most, not per candidate.
        /// </summary>
        public virtual EntityInfo FindEntityByName(string name)
        {
            if (_pl == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string wanted = name.Trim();
            for (int i = 0; i < 2304; i++)
            {
                EliteAPI.XiEntity entity;
                try
                {
                    entity = _pl.Entity.GetEntity(i);
                }
                catch
                {
                    return null;
                }

                if (entity != null && !string.IsNullOrEmpty(entity.Name)
                    && entity.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return new EntityInfo
                    {
                        Index = i,
                        Name = entity.Name,
                        ServerId = entity.ServerID,
                        Status = entity.Status,
                        Distance = entity.Distance,
                        X = entity.X,
                        Y = entity.Y,
                        Z = entity.Z,
                    };
                }
            }

            return null;
        }

        /// <summary>3D distance in yalms between two entity-array slots; float.MaxValue when either is unreadable.</summary>
        public virtual float GetDistanceBetweenEntities(int indexA, int indexB)
        {
            if (_pl == null)
            {
                return float.MaxValue;
            }

            try
            {
                EliteAPI.XiEntity a = _pl.Entity.GetEntity(indexA);
                EliteAPI.XiEntity b = _pl.Entity.GetEntity(indexB);
                if (a == null || b == null)
                {
                    return float.MaxValue;
                }

                float dx = a.X - b.X;
                float dy = a.Y - b.Y;
                float dz = a.Z - b.Z;
                return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch
            {
                return float.MaxValue;
            }
        }

        public virtual int GetSpellRecast(int spellIndex)
        {
            return _pl == null ? -1 : _pl.Recast.GetSpellRecast(spellIndex);
        }

        private HashSet<int> _ownedItemIds;
        private DateTime _ownedItemsScannedUtc = DateTime.MinValue;

        /// <summary>
        /// Item ids owned across the portable containers (inventory, satchel, sack, case,
        /// wardrobes 1-8). Used for gear detection as a fallback when the equipment table
        /// (see <see cref="GetEquippedItemIds"/>) is unavailable. Cached for 30 s —
        /// scans are ~1300 slots.
        /// </summary>
        public virtual IReadOnlyCollection<int> GetOwnedItemIds()
        {
            if (_pl == null)
            {
                return new HashSet<int>();
            }

            if (_ownedItemIds != null && DateTime.UtcNow - _ownedItemsScannedUtc < TimeSpan.FromSeconds(30))
            {
                return _ownedItemIds;
            }

            var ids = new HashSet<int>();
            int[] containers =
            {
                (int)StorageContainer.Inventory,
                (int)StorageContainer.Satchel,
                (int)StorageContainer.Sack,
                (int)StorageContainer.Case,
                (int)StorageContainer.Wardrobe,
                (int)StorageContainer.Wardrobe2,
                (int)StorageContainer.Wardrobe3,
                (int)StorageContainer.Wardrobe4,
                (int)StorageContainer.Wardrobe5,
                (int)StorageContainer.Wardrobe6,
                (int)StorageContainer.Wardrobe7,
                (int)StorageContainer.Wardrobe8,
            };

            foreach (int container in containers)
            {
                try
                {
                    int count = _pl.Inventory.GetContainerCount(container);
                    for (int i = 0; i < count; i++)
                    {
                        EliteAPI.InventoryItem item = _pl.Inventory.GetContainerItem(container, i);
                        if (item != null && item.Id != 0)
                        {
                            ids.Add((int)item.Id);
                        }
                    }
                }
                catch
                {
                    // A disabled/absent container must not fail the whole scan.
                }
            }

            _ownedItemIds = ids;
            _ownedItemsScannedUtc = DateTime.UtcNow;
            return ids;
        }

        private Dictionary<int, int> _equippedItemIds;
        private DateTime _equippedItemsScannedUtc = DateTime.MinValue;

        /// <summary>
        /// Item ids currently equipped, keyed by <see cref="EquipmentSlot"/> value
        /// (Main=0, Sub=1, Range=2, ...). Read from the client's live equipment table;
        /// cached for 5 s. Empty when not attached or the table is unavailable.
        /// </summary>
        public virtual IReadOnlyDictionary<int, int> GetEquippedItemIds()
        {
            if (_pl == null)
            {
                return new Dictionary<int, int>();
            }

            if (_equippedItemIds != null && DateTime.UtcNow - _equippedItemsScannedUtc < TimeSpan.FromSeconds(5))
            {
                return _equippedItemIds;
            }

            var ids = new Dictionary<int, int>();
            for (int slot = 0; slot < 16; slot++)
            {
                try
                {
                    EliteAPI.InventoryItem item = _pl.Inventory.GetEquippedItem(slot);
                    if (item != null && item.Id != 0 && item.Id != 65535)
                    {
                        ids[slot] = item.Id;
                    }
                }
                catch
                {
                    // A missing equipment table must not fail the whole scan.
                }
            }

            _equippedItemIds = ids;
            _equippedItemsScannedUtc = DateTime.UtcNow;
            return ids;
        }

        /// <summary>True when the local player has the named job ability learned/available.</summary>
        public virtual bool HasAbility(string abilityName)
        {
            if (_pl == null || string.IsNullOrWhiteSpace(abilityName))
            {
                return false;
            }

            try
            {
                EliteAPI.IAbility ability = _pl.Resources.GetAbility(abilityName.Trim(), 0);
                return ability != null && ability.ID != 0 && _pl.Player.HasAbility(ability.ID);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Temporary diagnostic for the HasAbility false-negative seen live: reports the
        /// resource id and raw mask probes through the live attachment so we can tell a
        /// resource-lookup miss apart from a broken ability-mask read. Soul Voice (bit 25)
        /// and Valor Minuet V (spell 398) are known-set references on the test character.
        /// </summary>
        public virtual string AbilityDebugProbe(string abilityName)
        {
            if (_pl == null)
            {
                return " [probe: detached]";
            }

            try
            {
                EliteAPI.IAbility a = _pl.Resources.GetAbility(abilityName.Trim(), 0);
                return " [probe: resId=" + a.ID
                    + " has(resId)=" + _pl.Player.HasAbility(a.ID)
                    + " has(163)=" + _pl.Player.HasAbility(163)
                    + " has(25)=" + _pl.Player.HasAbility(25)
                    + " spell398=" + _pl.Player.HasSpell(398) + "]";
            }
            catch (Exception ex)
            {
                return " [probe ex: " + ex.Message + "]";
            }
        }

        /// <summary>Raw recast remaining for the named job ability; 0 when ready or unknown.</summary>
        public virtual int GetAbilityRecast(string abilityName)
        {
            if (_pl == null || string.IsNullOrWhiteSpace(abilityName))
            {
                return 0;
            }

            try
            {
                EliteAPI.IAbility ability = _pl.Resources.GetAbility(abilityName.Trim(), 0);
                if (ability == null || ability.ID == 0)
                {
                    return 0;
                }

                List<int> timerIds = _pl.Recast.GetAbilityIds();
                for (int i = 0; i < timerIds.Count; i++)
                {
                    if (timerIds[i] == ability.TimerID)
                    {
                        return _pl.Recast.GetAbilityRecast(i);
                    }
                }

                return 0;
            }
            catch
            {
                return 0;
            }
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
