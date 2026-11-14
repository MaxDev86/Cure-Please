using System;
using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core.Modules
{
    /// <summary>
    /// Debuff removal for targets whose buffs are readable from memory: the local
    /// player and the monitored character. (Full party coverage needs a buff
    /// provider — addon or future memory reads — which is a P2 item; see
    /// docs/legacy-assessment.md section 3.)
    ///
    /// Unlike legacy, severity is scored and competes with cures directly, so a
    /// Doom outranks a medium cure instead of only being checked before small ones.
    /// </summary>
    public sealed class DebuffModule : IDecisionModule
    {
        private sealed class Removal
        {
            public StatusEffect Debuff;
            public string Spell;
            public double Severity;

            /// <summary>Removal only works while Afflatus Misery is up (Esuna cases).</summary>
            public bool NeedsMisery;
        }

        private static readonly Removal[] Removals =
        {
            new Removal { Debuff = StatusEffect.Doom,           Spell = "Cursna",   Severity = 1.00 },
            new Removal { Debuff = StatusEffect.Petrification,  Spell = "Stona",    Severity = 0.95 },
            new Removal { Debuff = StatusEffect.Paralysis,      Spell = "Paralyna", Severity = 0.90 },
            new Removal { Debuff = StatusEffect.Amnesia,        Spell = "Esuna",    Severity = 0.85, NeedsMisery = true },
            new Removal { Debuff = StatusEffect.Silence,        Spell = "Silena",   Severity = 0.80 },
            new Removal { Debuff = StatusEffect.Curse,          Spell = "Cursna",   Severity = 0.75 },
            new Removal { Debuff = StatusEffect.Bane,           Spell = "Cursna",   Severity = 0.75 },
            new Removal { Debuff = StatusEffect.Disease,        Spell = "Viruna",   Severity = 0.70 },
            new Removal { Debuff = StatusEffect.Plague,         Spell = "Viruna",   Severity = 0.70 },
            new Removal { Debuff = StatusEffect.Blindness,      Spell = "Blindna",  Severity = 0.60 },
            new Removal { Debuff = StatusEffect.Poison,         Spell = "Poisona",  Severity = 0.50 },
            new Removal { Debuff = StatusEffect.Slow,           Spell = "Erase",    Severity = 0.40 },
            new Removal { Debuff = StatusEffect.Bind,           Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Weight,         Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Bio,            Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Dia,            Spell = "Erase",    Severity = 0.35 },
            new Removal { Debuff = StatusEffect.Burn,           Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.Flash,          Spell = "Erase",    Severity = 0.30 },
            new Removal { Debuff = StatusEffect.STR_Down,       Spell = "Erase",    Severity = 0.25 },
        };

        private readonly GameStateService _state;

        public DebuffModule(GameStateService state)
        {
            _state = state;
        }

        public string Name => "Debuffs";

        public void AddCandidates(GameSnapshot snapshot, CurePleaseSettings settings, List<ActionCandidate> candidates)
        {
            if (!settings.Debuffs.Enabled)
            {
                return;
            }

            PlayerState pl = snapshot.Player;
            bool miseryUp = pl.HasBuff(StatusEffect.Afflatus_Misery);

            ScanTarget(snapshot, settings, candidates, pl.Name, pl.Buffs, miseryUp, isMonitored: false);

            if (!string.IsNullOrEmpty(snapshot.MonitoredName) && snapshot.MonitoredBuffs.Length > 0)
            {
                ScanTarget(snapshot, settings, candidates, snapshot.MonitoredName, snapshot.MonitoredBuffs, miseryUp, isMonitored: true);
            }

            // Addon-provided party buffs (own party only). Skip names already scanned
            // from memory to avoid duplicate candidates.
            foreach (KeyValuePair<string, short[]> pair in snapshot.PartyBuffs)
            {
                if (pair.Key.Equals(pl.Name, StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals(snapshot.MonitoredName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ScanTarget(snapshot, settings, candidates, pair.Key, pair.Value, miseryUp, isMonitored: false);
            }
        }

        private void ScanTarget(
            GameSnapshot snapshot,
            CurePleaseSettings settings,
            List<ActionCandidate> candidates,
            string targetName,
            short[] buffs,
            bool miseryUp,
            bool isMonitored)
        {
            foreach (Removal removal in Removals)
            {
                if (!settings.Debuffs.EnabledRemovals.Contains(removal.Spell == "Esuna" ? "Amnesia" : removal.Debuff.ToString()))
                {
                    continue;
                }

                if (removal.NeedsMisery && !miseryUp)
                {
                    continue;
                }

                if (!HasBuff(buffs, removal.Debuff))
                {
                    continue;
                }

                // Correct spell gate (legacy bug: Poisona branch checked JobChecker("Erase")).
                EliteAPI.ISpell spell = SpellBook.IsCastable(_state, removal.Spell, snapshot.Player.MP);
                if (spell == null)
                {
                    continue;
                }

                double priority = isMonitored ? settings.Priorities.MonitoredWeight : 1.0;
                double score = settings.Priorities.DebuffWeight * removal.Severity * priority;

                candidates.Add(new ActionCandidate
                {
                    Category = ActionCategory.DebuffRemoval,
                    Score = score,
                    ActionName = spell.Name[0],
                    TargetName = targetName,
                    Description = string.Format("{0} -> {1} ({2})", spell.Name[0], targetName, removal.Debuff),
                });
            }
        }

        private static bool HasBuff(short[] buffs, StatusEffect effect)
        {
            foreach (short buff in buffs)
            {
                if (buff == (short)effect)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
