using System.Collections.Generic;
using CurePlease.Settings;
using FFXI.Memory;

namespace CurePlease.Core
{
    /// <summary>
    /// Data-driven replacement for the legacy CureTiers ladder: picks the best
    /// available spell from a tier table given MP, recast, and known spells, with
    /// explicit undercure/overcure fallback policy. MP floors come from the spell's
    /// real MPCost in the resource database, not hardcoded magic numbers.
    /// </summary>
    public static class SpellBook
    {
        /// <summary>
        /// Selects a spell from a tier list (highest tier first) for the given HP loss:
        /// the highest enabled tier whose threshold is met wins. If that tier cannot
        /// be cast right now (unknown, on recast, insufficient MP), falls back down
        /// the list (undercure) or up the list (overcure) per policy.
        /// Returns null when no threshold is met or nothing is castable.
        /// </summary>
        public static EliteAPI.ISpell ResolveTier(
            GameStateService state,
            IReadOnlyList<string> tierNames,
            IReadOnlyList<CureTierSettings> tiers,
            uint hpLoss,
            uint availableMp,
            bool undercure,
            bool overcure)
        {
            int count = tierNames.Count < tiers.Count ? tierNames.Count : tiers.Count;

            // Highest tier whose threshold the loss satisfies (legacy behavior:
            // below every threshold means no cure at all).
            int matched = -1;
            for (int i = 0; i < count; i++)
            {
                if (tiers[i].Enabled && hpLoss >= tiers[i].MinimumHpLoss)
                {
                    matched = i;
                    break;
                }
            }

            if (matched < 0)
            {
                return null;
            }

            EliteAPI.ISpell direct = IsCastable(state, tierNames[matched], availableMp);
            if (direct != null)
            {
                return direct;
            }

            if (undercure)
            {
                for (int i = matched + 1; i < count; i++)
                {
                    EliteAPI.ISpell spell = IsCastable(state, tierNames[i], availableMp);
                    if (spell != null)
                    {
                        return spell;
                    }
                }
            }

            if (overcure)
            {
                for (int i = matched - 1; i >= 0; i--)
                {
                    EliteAPI.ISpell spell = IsCastable(state, tierNames[i], availableMp);
                    if (spell != null)
                    {
                        return spell;
                    }
                }
            }

            return null;
        }

        /// <summary>Resolves a spell and validates known + recast ready + enough MP. Null otherwise.</summary>
        public static EliteAPI.ISpell IsCastable(GameStateService state, string spellName, uint availableMp)
        {
            EliteAPI.ISpell spell = state.FindSpell(spellName);
            if (spell == null)
            {
                return null;
            }

            if (!state.KnowsSpell(spell.ID))
            {
                return null;
            }

            if (availableMp < spell.MPCost)
            {
                return null;
            }

            if (state.GetSpellRecast(spell.Index) != 0)
            {
                return null;
            }

            return spell;
        }

        public static readonly string[] CureTierNames = { "Cure VI", "Cure V", "Cure IV", "Cure III", "Cure II", "Cure" };
        public static readonly string[] CuragaTierNames = { "Curaga V", "Curaga IV", "Curaga III", "Curaga II", "Curaga" };
    }
}
