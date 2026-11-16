using System;

namespace CurePlease.Core
{
    /// <summary>
    /// SCH stratagem charge math. All stratagems share one recast timer (id 231)
    /// that counts down to the NEXT charge; the count of currently available
    /// charges is derived from it. Table per the canonical Windower implementation
    /// (Lorand's StratagemCounter) and BG-wiki:
    ///  - max charges = floor((schLevel - 10) / 20) + 1 (1 at 10, 5 at 90), using
    ///    the main OR sub job SCH level;
    ///  - recharge per charge = {1: 240s, 2: 120s, 3: 80s, 4: 60s, 5: 48s},
    ///    dropping to 33s at 5 charges once 550+ SCH job points are spent
    ///    (main job only);
    ///  - available = max - ceil(recast / perChargeSeconds).
    /// A name-based recast lookup (GetAbilityRecast("Accession")) resolves to this
    /// same shared timer, so it reads "on recast" whenever charges are merely not
    /// FULL — stratagem gating must go through the charge count instead.
    /// </summary>
    public static class Stratagems
    {
        /// <summary>Shared recast timer id for all stratagem charges.</summary>
        public const int TimerId = 231;

        /// <summary>Recharge seconds per charge, indexed by max charges (1..5).</summary>
        private static readonly int[] ChargeSeconds = { 0, 240, 120, 80, 60, 48 };

        /// <summary>Job points spent on SCH main for the reduced 33s recharge at 5 charges.</summary>
        public const int ReducedRechargeJobPoints = 550;

        /// <summary>Reduced per-charge recharge at 5 charges with 550+ SCH job points.</summary>
        private const int ReducedRechargeSeconds = 33;

        /// <summary>Max charges for a SCH level (main or sub job level). 0 below 10.</summary>
        public static int MaxChargesForLevel(int schLevel)
        {
            if (schLevel < 10)
            {
                return 0;
            }

            return Math.Min(5, (schLevel - 10) / 20 + 1);
        }

        /// <summary>
        /// Per-charge recharge seconds for the given max charge count. The 550 JP
        /// bonus (33s at 5 charges) only applies on SCH main.
        /// </summary>
        public static int RechargeSeconds(int maxCharges, bool reducedRecharge)
        {
            if (maxCharges <= 0 || maxCharges >= ChargeSeconds.Length)
            {
                return 0;
            }

            return reducedRecharge && maxCharges == 5 ? ReducedRechargeSeconds : ChargeSeconds[maxCharges];
        }

        /// <summary>Effective SCH level for this player (main job first, otherwise sub job).</summary>
        public static int ScholarLevel(PlayerState player)
        {
            return player.MainJob == 20 ? player.MainJobLevel : player.SubJobLevel;
        }

        /// <summary>Charges currently available on this player's SCH main or sub job.</summary>
        public static int AvailableCharges(GameStateService state, PlayerState player)
        {
            int maxCharges = MaxChargesForLevel(ScholarLevel(player));
            bool reduced = player.MainJob == 20
                && state.GetSpentJobPoints(20) >= ReducedRechargeJobPoints;
            return AvailableCharges(
                state.GetAbilityRecastByTimerId(TimerId), maxCharges, reduced);
        }

        /// <summary>Charges currently available, given the shared timer's remaining seconds.</summary>
        public static int AvailableCharges(int combinedRecastSeconds, int maxCharges, bool reducedRecharge = false)
        {
            if (maxCharges <= 0)
            {
                return 0;
            }

            if (combinedRecastSeconds <= 0)
            {
                return maxCharges;
            }

            int used = (int)Math.Ceiling((double)combinedRecastSeconds / RechargeSeconds(maxCharges, reducedRecharge));
            return Math.Max(0, maxCharges - used);
        }
    }
}
