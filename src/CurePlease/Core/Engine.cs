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

        /// <summary>Optional addon data provider; when set, party buffs are merged into each snapshot.</summary>
        public AddonListener Addon { get; set; }

        private CancellationTokenSource _cts;
        private Task _loop;

        public Engine(GameStateService state, CastingService casting, SettingsService settings)
        {
            _state = state;
            _casting = casting;
            _settings = settings;
        }

        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        /// <summary>Diagnostic log line for the UI (loop started, action chosen, errors).</summary>
        public event Action<string> Log;

        /// <summary>Raised after every executed action with its outcome.</summary>
        public event Action<CastResult> ActionExecuted;

        /// <summary>Raised once per tick with the snapshot (for the party UI), even when idle.</summary>
        public event Action<GameSnapshot> SnapshotReady;

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

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
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
                SnapshotReady?.Invoke(null);
                return;
            }

            SnapshotReady?.Invoke(snapshot);

            CurePleaseSettings settings = _settings.Current;

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

            if (settings.General.PauseActions)
            {
                return;
            }

            // Dead PL does nothing (KO, zoning handled by snapshot).
            if (snapshot.Player.HP == 0)
            {
                return;
            }

            // Full incapacitation: no actions of any kind are possible.
            if (HasAnyBuff(snapshot.Player, StatusEffect.Sleep, StatusEffect.Petrification, StatusEffect.Stun,
                    StatusEffect.Charm1, StatusEffect.Charm2, StatusEffect.Terror))
            {
                return;
            }

            List<ActionCandidate> candidates = new List<ActionCandidate>();
            foreach (IDecisionModule module in _modules)
            {
                try
                {
                    module.AddCandidates(snapshot, settings, candidates);
                }
                catch (Exception ex)
                {
                    RaiseLog(module.Name + " module error: " + ex.Message);
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

            if (candidates.Count == 0)
            {
                return;
            }

            ActionCandidate best = candidates.OrderByDescending(c => c.Score).First();

            CastResult result = best.IsJobAbility
                ? await _casting.JobAbilityAsync(best.ActionName, best.TargetName, ct).ConfigureAwait(false)
                : await _casting.CastAsync(best.ActionName, best.TargetName, ct).ConfigureAwait(false);

            RaiseLog(string.Format("[{0:0.0}] {1} — {2}{3}", best.Score, best.Description, result.Outcome,
                result.Detail.Length > 0 ? " (" + result.Detail + ")" : string.Empty));

            foreach (IDecisionModule module in _modules)
            {
                try
                {
                    module.OnActionExecuted(best, result);
                }
                catch (Exception ex)
                {
                    RaiseLog(module.Name + " feedback error: " + ex.Message);
                }
            }

            ActionExecuted?.Invoke(result);
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
