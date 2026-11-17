using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core
{
    public enum ActionCategory
    {
        Cure,
        Curaga,
        DebuffRemoval,
        BuffUpkeep,
        Song,
        Geo,
        JobAbility,
        Item,
    }

    /// <summary>One action the engine could take this tick, with its computed priority.</summary>
    public sealed class ActionCandidate
    {
        public ActionCategory Category { get; set; }
        public double Score { get; set; }
        public string Description { get; set; } = string.Empty;

        /// <summary>Spell or ability name to send.</summary>
        public string ActionName { get; set; } = string.Empty;

        /// <summary>Chat-command target (&lt;me&gt;, a player name, etc.).</summary>
        public string TargetName { get; set; } = string.Empty;

        public bool IsJobAbility { get; set; }

        /// <summary>Module that offered this candidate (tagged by the engine during collection).</summary>
        public string OwnerModule { get; set; }

        /// <summary>For prep JAs: the "next spell" buff this JA grants (0 = not a prep).</summary>
        public StatusEffect PrepBuff { get; set; }

        /// <summary>For prep JAs: the spell the buff is intended for (empty = not fixed at prep time).</summary>
        public string PrepForSpell { get; set; }

        /// <summary>
        /// Life-or-death candidate (cure target at/below CureSettings.IntentEmergencyHpPercent):
        /// may consume another module's claimed stratagem buff — the claim owner re-preps.
        /// </summary>
        public bool IsEmergency { get; set; }
    }

    /// <summary>
    /// A feature module (cures, curaga, debuffs, buff upkeep, songs, ...).
    /// Each tick it inspects the immutable snapshot and offers scored candidates.
    /// </summary>
    public interface IDecisionModule
    {
        string Name { get; }

        void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates);

        /// <summary>
        /// Called after the engine executes an action, so modules can confirm state
        /// (e.g. record a buff's landing time only when the cast actually succeeded).
        /// Default no-op for modules that do not need feedback.
        /// </summary>
        void OnActionExecuted(ActionCandidate candidate, CastResult result)
        {
        }

        /// <summary>
        /// Called when the player zones: all buffs/songs/luopons are gone, so modules
        /// must drop their landing timers and tracking state. Default no-op.
        /// </summary>
        void OnZoneChanged()
        {
        }
    }

    /// <summary>
    /// The single decision loop. Replaces the legacy async-void 500 ms timer and its
    /// BackgroundWorker soup: one cancellable background loop, one snapshot per tick,
    /// one scored action per tick. Priority is explicit (highest score wins) and
    /// inspectable (every decision is logged with its score).
    /// </summary>
    public sealed class Engine
    {
        private readonly GameStateService _state;
        private readonly CastingService _casting;
        private readonly SettingsService _settings;
        private readonly List<IDecisionModule> _modules = new List<IDecisionModule>();
        private readonly StratagemIntents _intents = new StratagemIntents();

        /// <summary>Auto-follow; ticked once per cycle and suspended while an action executes.</summary>
        public FollowService Follow { get; private set; }

        /// <summary>Optional addon data provider; when set, party buffs are merged into each snapshot.</summary>
        public AddonListener Addon { get; set; }

        private CancellationTokenSource _cts;
        private Task _loop;

        // Zone tracking: a tick that was logged in followed by LoginStatus.Loading
        // (or a zone-id change between two valid snapshots) is a zone transition.
        private bool _wasLoggedIn;
        private int _lastZoneId = -1;
        private bool _pausedByZone;
        private CurePleaseSettings _auditedSettings;
        private string _auditContextKey;

        public Engine(GameStateService state, CastingService casting, SettingsService settings)
        {
            _state = state;
            _casting = casting;
            _settings = settings;
            _intents.ClaimAdded += RaiseLog;
            Follow = new FollowService(state);
            Follow.Log += RaiseLog;
        }

        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        /// <summary>Diagnostic log line for the UI (loop started, action chosen, errors).</summary>
        public event Action<string> Log;

        /// <summary>Raised after every executed action with its outcome.</summary>
        public event Action<CastResult> ActionExecuted;

        /// <summary>Raised once per tick with the snapshot (for the party UI), even when idle.</summary>
        public event Action<GameSnapshot> SnapshotReady;

        /// <summary>
        /// True when zoning latched a pause (General.PauseOnZone). Actions stay
        /// suspended after zone-in until <see cref="ResumeFromZone"/> is called.
        /// </summary>
        public bool PausedByZone => _pausedByZone;

        /// <summary>Raised when the zone-pause latch engages or clears (for the UI button).</summary>
        public event Action<bool> ZonePauseChanged;

        /// <summary>Clears the zone-pause latch; the engine resumes acting next tick.</summary>
        public void ResumeFromZone()
        {
            if (!_pausedByZone)
            {
                return;
            }

            _pausedByZone = false;
            RaiseLog("Resumed after zoning");
            ZonePauseChanged?.Invoke(false);
        }

        private void OnZoneTransition(CurePleaseSettings settings)
        {
            RaiseLog("Zoned — clearing buff/song tracking");
            _intents.Clear();
            Follow.Reset();
            foreach (IDecisionModule module in _modules)
            {
                try
                {
                    module.OnZoneChanged();
                }
                catch (Exception ex)
                {
                    RaiseLog(module.Name + " zone-reset error: " + ex.Message);
                    Serilog.Log.Error(ex, "Module {Module} OnZoneChanged failed", module.Name);
                }
            }

            if (settings.General.PauseOnZone && !_pausedByZone)
            {
                _pausedByZone = true;
                RaiseLog("Paused (zoned) — press Resume to continue");
                ZonePauseChanged?.Invoke(true);
            }
        }

        public void RegisterModule(IDecisionModule module)
        {
            _modules.Add(module);
        }

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            _auditedSettings = null;
            _auditContextKey = null;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            Follow.Reset();
            if (_pausedByZone)
            {
                _pausedByZone = false;
                ZonePauseChanged?.Invoke(false);
            }
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_loop != null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            RaiseLog("Engine started");

            while (!ct.IsCancellationRequested)
            {
                int tickStart = Environment.TickCount;

                try
                {
                    await Tick(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad tick must never kill the loop (legacy: unhandled = crash).
                    RaiseLog("Tick error: " + ex.Message);
                    Serilog.Log.Error(ex, "Engine tick failed");
                }

                CurePleaseSettings settings = _settings.Current;
                int elapsed = Environment.TickCount - tickStart;
                int delay = Math.Max(50, settings.General.TickIntervalMs - elapsed);

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            RaiseLog("Engine stopped");
        }

        private async Task Tick(CancellationToken ct)
        {
            GameSnapshot snapshot = _state.TakeSnapshot();

            if (snapshot == null)
            {
                // Logged-in -> Loading is the zone edge (legacy watched the same flag).
                if (_wasLoggedIn && _state.LastLoginStatus == (int)LoginStatus.Loading)
                {
                    OnZoneTransition(_settings.Current);
                }

                _wasLoggedIn = false;
                SnapshotReady?.Invoke(null);
                return;
            }

            // Belt-and-braces: a zone change without an observed Loading tick
            // (e.g. warp between two valid snapshots) still resets module state.
            if (_wasLoggedIn && _lastZoneId != -1 && snapshot.Player.ZoneId != _lastZoneId)
            {
                OnZoneTransition(_settings.Current);
            }

            _wasLoggedIn = true;
            _lastZoneId = snapshot.Player.ZoneId;

            SnapshotReady?.Invoke(snapshot);

            CurePleaseSettings settings = _settings.Current;
            AuditCapabilities(snapshot.Player, settings);

            // Merge addon-provided party buffs (with staleness + roster pruning).
            if (Addon != null && Addon.IsRunning && settings.Addon.Enabled)
            {
                snapshot.PartyBuffs = Addon.GetBuffSnapshot(TimeSpan.FromSeconds(settings.Addon.StaleSeconds));

                List<string> roster = new List<string>();
                foreach (PartyMemberState member in snapshot.PartyMembers)
                {
                    if (member.Active)
                    {
                        roster.Add(member.Name);
                    }
                }

                roster.Add(snapshot.Player.Name);
                Addon.PruneRoster(roster);
            }

            bool paused = settings.General.PauseActions || _pausedByZone;
            bool dead = snapshot.Player.HP == 0;

            // Full incapacitation: no actions of any kind are possible.
            bool fullyLocked = HasAnyBuff(snapshot.Player, StatusEffect.Sleep, StatusEffect.Petrification, StatusEffect.Stun,
                    StatusEffect.Charm1, StatusEffect.Charm2, StatusEffect.Terror);

            // Follow runs every tick — including paused/locked ones, so it can stand
            // the character down the moment a gate closes. It never blocks.
            Follow.Tick(snapshot, settings, !paused && !dead && !fullyLocked, _casting.IsCasting);

            if (paused || dead || fullyLocked)
            {
                return;
            }

            List<ActionCandidate> candidates = new List<ActionCandidate>();
            _intents.Prune(snapshot.Player.Buffs);
            foreach (IDecisionModule module in _modules)
            {
                try
                {
                    int before = candidates.Count;
                    module.AddCandidates(snapshot, settings, candidates);
                    for (int i = before; i < candidates.Count; i++)
                    {
                        candidates[i].OwnerModule = module.Name;
                    }
                }
                catch (Exception ex)
                {
                    RaiseLog(module.Name + " module error: " + ex.Message);
                    Serilog.Log.Error(ex, "Module {Module} AddCandidates failed", module.Name);
                }
            }

            // Partial incapacitation gates: silence/mute/Omerta block spells (items stay
            // usable), amnesia/impairment block job abilities.
            bool spellsLocked = HasAnyBuff(snapshot.Player, StatusEffect.Silence, StatusEffect.Mute, StatusEffect.Omerta);
            bool abilitiesLocked = HasAnyBuff(snapshot.Player, StatusEffect.Amnesia, StatusEffect.Impairment);
            if (spellsLocked || abilitiesLocked)
            {
                candidates.RemoveAll(c =>
                    (spellsLocked && !c.IsJobAbility && c.Category != ActionCategory.Item)
                    || (abilitiesLocked && c.IsJobAbility));
            }

            // Capability is the hard stop: an unlearned/off-job action must never
            // become an in-game command error. The audit explains these at context
            // changes; the per-tick filter stays silent.
            candidates.RemoveAll(c => !CapabilityAudit.IsAvailable(_state, c));

            // Stratagem intents: a claimed "next spell" buff (Accession, Divine
            // Seal, ...) is reserved for the claiming module. Suppress other
            // modules' spells that would consume it (emergencies exempt) and
            // competing prep JAs for the same buff.
            if (_intents.Count > 0)
            {
                candidates.RemoveAll(c =>
                    !c.IsEmergency
                    && ((c.IsJobAbility && c.PrepBuff != 0 && _intents.IsHeldByOther(c.PrepBuff, c.OwnerModule))
                        || (!c.IsJobAbility && c.Category != ActionCategory.Item
                            && _intents.BlocksSpell(c.ActionName, c.OwnerModule))));
            }

            if (candidates.Count == 0)
            {
                return;
            }

            ActionCandidate best = candidates.OrderByDescending(c => c.Score).First();

            // Movement interrupts casts — stand still before the command goes out.
            Follow.SuspendForAction();

            CastResult result = best.Category == ActionCategory.Item
                ? await _casting.UseItemAsync(best.ActionName, best.TargetName, ct).ConfigureAwait(false)
                : best.IsJobAbility
                    ? await _casting.JobAbilityAsync(best.ActionName, best.TargetName, ct).ConfigureAwait(false)
                    : await _casting.CastAsync(best.ActionName, best.TargetName, ct).ConfigureAwait(false);

            RaiseLog(string.Format("[{0:0.0}] {1} — {2}{3}", best.Score, best.Description, result.Outcome,
                result.Detail.Length > 0 ? " (" + result.Detail + ")" : string.Empty));

            if (result.Succeeded && best.IsJobAbility && best.PrepBuff != 0)
            {
                _intents.Add(best.PrepBuff, best.OwnerModule, best.PrepForSpell, best.Description);
            }

            foreach (IDecisionModule module in _modules)
            {
                try
                {
                    module.OnActionExecuted(best, result);
                }
                catch (Exception ex)
                {
                    RaiseLog(module.Name + " feedback error: " + ex.Message);
                    Serilog.Log.Error(ex, "Module {Module} OnActionExecuted failed", module.Name);
                }
            }

            ActionExecuted?.Invoke(result);
        }

        private void AuditCapabilities(PlayerState player, CurePleaseSettings settings)
        {
            string contextKey = (player.Name ?? string.Empty) + "|" + player.MainJob + "|" + player.SubJob
                + "|" + player.MainJobLevel + "|" + player.SubJobLevel + "|" + player.MainJobMasterLevel;
            if (contextKey == _auditContextKey && ReferenceEquals(settings, _auditedSettings))
            {
                return;
            }

            _auditContextKey = contextKey;
            _auditedSettings = settings;

            IReadOnlyList<string> issues = CapabilityAudit.FindIssues(_state, settings, player);
            if (issues.Count == 0)
            {
                return;
            }

            RaiseLog("Capability audit: " + issues.Count + " enabled action"
                + (issues.Count == 1 ? " is" : "s are") + " unavailable in this context");
            foreach (string issue in issues)
            {
                RaiseLog("  " + issue);
            }
        }

        private static bool HasAnyBuff(PlayerState player, params FFXI.Memory.StatusEffect[] effects)
        {
            foreach (FFXI.Memory.StatusEffect effect in effects)
            {
                if (player.HasBuff(effect))
                {
                    return true;
                }
            }

            return false;
        }

        private void RaiseLog(string message)
        {
            Log?.Invoke(message);
        }
    }
}
