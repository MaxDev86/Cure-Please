using System;
using CurePlease.Settings;

namespace CurePlease.Core
{
    /// <summary>
    /// Auto-follow, ticked by the engine once per cycle. Replaces the legacy
    /// Follow_BGW BackgroundWorker (blocking while-loop with sleeps inside a
    /// worker, re-run on completion): every decision here is a state transition
    /// evaluated from the current tick's snapshot — nothing blocks, nothing loops.
    ///
    /// Two modes (General.FollowUseNative):
    /// - Memory (default): writes the client's auto-follow state struct directly
    ///   (FFXI.Memory AutoFollowTools, live-verified). The game recomputes the
    ///   destination from the follow target while the flag is set, so we only
    ///   re-assert info/coords; the distance band and stop conditions are ours.
    /// - Native: targets the entity and sends /follow across several ticks (the
    ///   client needs a beat between retarget and the command). The same distance
    ///   band applies; stopping clears the client follow flag and resets the command
    ///   sequence so follow can resume when the target moves away.
    ///
    /// From Windower's SmartFollow addon: the "intentional pause" concept — if
    /// the follow flag drops while we believe we are driving, the user grabbed
    /// the keyboard; follow yields until the target moves well away again.
    /// </summary>
    public sealed class FollowService
    {
        private readonly GameStateService _state;

        /// <summary>Diagnostic log line (engaged, yielded, warnings).</summary>
        public event Action<string> Log;

        /// <summary>Beyond this the target is effectively unreachable (legacy hard stop: 48).</summary>
        private const float MaxRange = 48f;

        /// <summary>Legacy sent the "too far" tell at 40+ yalms.</summary>
        private const float WarnRange = 40f;

        /// <summary>User override clears when the target is FollowDistance + this far away.</summary>
        private const float ResumeHysteresis = 3f;

        /// <summary>Ticks without moving &gt; <see cref="StuckEpsilon"/> before the stuck warning (~5 s at 500 ms).</summary>
        private const int StuckTickLimit = 10;

        private const float StuckEpsilon = 0.1f;

        /// <summary>Full entity-array scans are rate-limited; the cached index is re-validated every tick.</summary>
        private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

        private int _targetIndex = -1;
        private DateTime _nextRescanUtc = DateTime.MinValue;
        private bool _driving;
        private int _drivingIndex = -1;
        private bool _userOverride;
        private bool _warnedFar;
        private bool _warnedStuck;
        private float _lastX;
        private float _lastY;
        private float _lastZ;
        private int _stillTicks;
        private int _nativeStage; // 0 idle, 1 target cleared, 2 target set, 3 /follow sent
        private string _engagedName = string.Empty;

        public FollowService(GameStateService state)
        {
            _state = state;
        }

        /// <summary>True while this service is driving movement (memory mode) or has native /follow engaged.</summary>
        public bool IsFollowing => _driving || _nativeStage == 3;

        /// <summary>
        /// One follow decision. <paramref name="mayMove"/> is the engine's combined
        /// gate (not paused, not dead, not fully incapacitated, same zone);
        /// <paramref name="castingBusy"/> is CastingService.IsCasting — moving
        /// interrupts casts, so follow stands down while a cast is in flight.
        /// </summary>
        public void Tick(GameSnapshot snapshot, CurePleaseSettings settings, bool mayMove, bool castingBusy)
        {
            string name = (settings.General.FollowTargetName ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                Reset();
                return;
            }

            if (!mayMove || castingBusy
                || name.Equals(snapshot.Player.Name, StringComparison.OrdinalIgnoreCase))
            {
                StopMoving();
                return;
            }

            if (!name.Equals(_engagedName, StringComparison.OrdinalIgnoreCase))
            {
                // Target changed: forget everything about the previous one.
                Reset();
                _engagedName = name;
            }

            EntityInfo target = ResolveTarget(name);
            if (target == null)
            {
                StopMoving();
                return;
            }

            float distance = target.Distance;
            float followDistance = Math.Max(1, settings.General.FollowDistance);

            if (distance > MaxRange)
            {
                StopMoving();
                WarnFar(name, settings, distance);
                return;
            }

            _warnedFar = false;

            if (distance < followDistance)
            {
                // Arrived. Stand still until the target moves off again.
                StopMoving();
                _userOverride = false;
                _warnedStuck = false;
                return;
            }

            if (_userOverride)
            {
                if (distance < followDistance + ResumeHysteresis)
                {
                    return; // the user is driving — yield
                }

                _userOverride = false;
                RaiseLog("Follow: resuming (" + name + " moved away)");
            }

            if (settings.General.FollowUseNative)
            {
                DriveNative(snapshot, target);
            }
            else
            {
                DriveMemory(target);
            }

            TrackStuck(snapshot, name, settings);
        }

        /// <summary>Engine is about to cast/use an ability: halt movement so the cast is not interrupted.</summary>
        public void SuspendForAction()
        {
            StopMoving();
        }

        /// <summary>Drop all follow state (zone change, engine stop, target cleared).</summary>
        public void Reset()
        {
            StopMoving();
            _targetIndex = -1;
            _drivingIndex = -1;
            _nextRescanUtc = DateTime.MinValue;
            _userOverride = false;
            _warnedFar = false;
            _warnedStuck = false;
            _nativeStage = 0;
            _engagedName = string.Empty;
        }

        private void DriveMemory(EntityInfo target)
        {
            if (_driving && !_state.IsAutoFollowActive())
            {
                // The flag dropped without us clearing it: the user moved (the flag
                // flips off within a second of follow breaking — maps.md). Yield.
                _driving = false;
                _userOverride = true;
                RaiseLog("Follow: movement detected — yielding (resume when the target moves off)");
                return;
            }

            if (!_driving || _drivingIndex != target.Index)
            {
                if (!_state.SetAutoFollowInfo(target.Index, target.ServerId))
                {
                    RaiseLog("Follow: could not write the follow target (memory write failed)");
                    return;
                }

                _drivingIndex = target.Index;
            }

            // Absolute destination (see docs/maps.md: the struct tracks the followee
            // live while the flag is set; re-asserting each tick just keeps pace).
            _state.SetAutoFollowCoords(target.X, target.Y, target.Z);
            if (!_driving)
            {
                _state.SetAutoFollow(true);
                _driving = true;
                RaiseLog("Follow: following " + target.Name);
            }
        }

        private void DriveNative(GameSnapshot snapshot, EntityInfo target)
        {
            switch (_nativeStage)
            {
                case 0:
                    if (snapshot.TargetIndex == (uint)target.Index)
                    {
                        _nativeStage = 2;
                    }
                    else
                    {
                        _state.SetTargetByIndex(0); // clear first: retarget needs a beat
                        _nativeStage = 1;
                    }

                    break;
                case 1:
                    _state.SetTargetByIndex(target.Index);
                    _nativeStage = 2;
                    break;
                case 2:
                    _state.SendCommand("/follow");
                    _nativeStage = 3;
                    RaiseLog("Follow: /follow " + target.Name + " (native mode — ignores distance once engaged)");
                    break;
                case 3:
                    if (snapshot.TargetIndex != (uint)target.Index)
                    {
                        _nativeStage = 0; // target lost — re-engage from scratch
                    }

                    break;
            }
        }

        private void StopMoving()
        {
            if (_driving || _nativeStage == 3)
            {
                _state.SetAutoFollow(false);
                _driving = false;
                if (_nativeStage == 3)
                {
                    _nativeStage = 0; // Re-run target + /follow when movement may resume.
                }
            }

            _stillTicks = 0;
        }

        private void TrackStuck(GameSnapshot snapshot, string name, CurePleaseSettings settings)
        {
            if (!IsFollowing)
            {
                return;
            }

            float dx = snapshot.Player.X - _lastX;
            float dy = snapshot.Player.Y - _lastY;
            float dz = snapshot.Player.Z - _lastZ;
            _lastX = snapshot.Player.X;
            _lastY = snapshot.Player.Y;
            _lastZ = snapshot.Player.Z;

            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > StuckEpsilon)
            {
                _stillTicks = 0;
                _warnedStuck = false;
                return;
            }

            if (++_stillTicks < StuckTickLimit || _warnedStuck)
            {
                return;
            }

            _warnedStuck = true;
            RaiseLog("Follow: not moving — possibly stuck");
            if (settings.General.FollowWarning)
            {
                _state.SendCommand("/tell " + name + " I appear to be stuck.");
            }
        }

        private void WarnFar(string name, CurePleaseSettings settings, float distance)
        {
            if (_warnedFar || distance < WarnRange)
            {
                return;
            }

            _warnedFar = true;
            RaiseLog("Follow: " + name + " is " + distance.ToString("0") + " yalms away — too far to follow");
            if (settings.General.FollowWarning)
            {
                _state.SendCommand("/tell " + name + " You're too far to follow.");
            }
        }

        private EntityInfo ResolveTarget(string name)
        {
            if (_targetIndex > 0)
            {
                EntityInfo cached = _state.FindEntityByIndex(_targetIndex);
                if (cached != null && cached.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return cached;
                }

                _targetIndex = -1;
            }

            if (DateTime.UtcNow < _nextRescanUtc)
            {
                return null;
            }

            _nextRescanUtc = DateTime.UtcNow + RescanInterval;
            EntityInfo found = _state.FindEntityByName(name);
            _targetIndex = found != null ? found.Index : -1;
            return found;
        }

        private void RaiseLog(string message)
        {
            Log?.Invoke(message);
        }
    }
}
