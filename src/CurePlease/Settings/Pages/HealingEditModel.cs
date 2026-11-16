using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the Healing page (Cures + Curaga). Scalar properties bind
    /// straight into the settings clone; tier rows wrap the clone's tier entries
    /// (tier names are positional, not stored), so nothing needs committing.
    /// </summary>
    public sealed class HealingEditModel
    {
        /// <summary>The settings clone being edited (Cures/Curaga sections).</summary>
        public CurePleaseSettings Settings { get; }

        public ObservableCollection<CureTierRow> CureTiers { get; }
        public ObservableCollection<CureTierRow> CuragaTiers { get; }

        public HealingEditModel(CurePleaseSettings settings)
        {
            Settings = settings;
            CureTiers = WrapTiers(settings.Cures.Tiers, new[] { "Cure VI", "Cure V", "Cure IV", "Cure III", "Cure II", "Cure" });
            CuragaTiers = WrapTiers(settings.Curaga.Tiers, new[] { "Curaga V", "Curaga IV", "Curaga III", "Curaga II", "Curaga" });
        }

        private static ObservableCollection<CureTierRow> WrapTiers(List<CureTierSettings> tiers, string[] names)
        {
            var rows = new ObservableCollection<CureTierRow>();
            for (int i = 0; i < tiers.Count; i++)
            {
                rows.Add(new CureTierRow(i < names.Length ? names[i] : "Tier " + (i + 1), tiers[i]));
            }

            return rows;
        }
    }

    /// <summary>Editable view of one cure/curaga tier; writes through to the wrapped settings entry.</summary>
    public sealed class CureTierRow
    {
        private readonly CureTierSettings _tier;

        public string TierName { get; }

        public bool Enabled
        {
            get => _tier.Enabled;
            set => _tier.Enabled = value;
        }

        public uint MinimumHpLoss
        {
            get => _tier.MinimumHpLoss;
            set => _tier.MinimumHpLoss = value;
        }

        public CureTierRow(string tierName, CureTierSettings tier)
        {
            TierName = tierName;
            _tier = tier;
        }
    }
}
