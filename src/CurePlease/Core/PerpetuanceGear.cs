using System.Collections.Generic;

namespace CurePlease.Core
{
    /// <summary>
    /// Perpetuance duration gear detection (docs/buff-upkeep-design.md #4). Hands gear
    /// raises the 2x stock multiplier per BG-wiki. The inventory scan covers portable
    /// containers only — bracers on a storage slip are invisible, so the Scholar page
    /// dropdown stays user-overridable and a manual choice stops the auto pre-set.
    /// </summary>
    public static class PerpetuanceGear
    {
        /// <summary>One multiplier tier the Scholar page offers.</summary>
        public sealed class Tier
        {
            public string Label { get; }
            public double Multiplier { get; }

            public Tier(string label, double multiplier)
            {
                Label = label;
                Multiplier = multiplier;
            }
        }

        /// <summary>Dropdown tiers, stock first.</summary>
        public static readonly IReadOnlyList<Tier> Tiers = new[]
        {
            new Tier("2.00x (stock)", 2.0),
            new Tier("2.25x (Savant's Bracers +1)", 2.25),
            new Tier("2.50x (Savant's +2 / Arbatel NQ)", 2.50),
            new Tier("2.55x (Arbatel Bracers +1)", 2.55),
            new Tier("2.60x (Arbatel Bracers +2)", 2.60),
            new Tier("2.65x (Arbatel Bracers +3)", 2.65),
        };

        /// <summary>Detection order: best multiplier first.</summary>
        private static readonly (string ItemName, double Multiplier)[] DetectOrder =
        {
            ("Arbatel Bracers +3", 2.65),
            ("Arbatel Bracers +2", 2.60),
            ("Arbatel Bracers +1", 2.55),
            ("Arbatel Bracers", 2.50),
            ("Savant's Bracers +2", 2.50),
            ("Savant's Bracers +1", 2.25),
        };

        /// <summary>Index of the tier with this multiplier, or 0 (stock) when unknown.</summary>
        public static int TierIndexOf(double multiplier)
        {
            for (int i = 0; i < Tiers.Count; i++)
            {
                if (Tiers[i].Multiplier == multiplier)
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>
        /// Best multiplier among the bracers the player owns, or null when none are
        /// found (or items are unreadable — detached).
        /// </summary>
        public static Tier Detect(GameStateService state)
        {
            foreach ((string itemName, double multiplier) in DetectOrder)
            {
                if (state.OwnsItem(itemName))
                {
                    return new Tier(itemName, multiplier);
                }
            }

            return null;
        }
    }
}
