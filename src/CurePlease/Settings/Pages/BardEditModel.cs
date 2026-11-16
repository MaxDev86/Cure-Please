using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the Bard settings page: the page edits ObservableCollection
    /// mirrors of the settings lists (so add/remove/reorder just work), and Commit
    /// copies them back into the settings clone before it is published.
    /// </summary>
    public sealed class BardEditModel
    {
        /// <summary>The settings clone being edited (scalar properties bind directly).</summary>
        public BardSettings Settings { get; }

        public ObservableCollection<string> PartySongs { get; }
        public ObservableCollection<string> DummySongs { get; }
        public ObservableCollection<string> OwnedInstruments { get; }
        public ObservableCollection<string> EnemyDebuffSongs { get; }
        public ObservableCollection<PianissimoTarget> PianissimoTargets { get; }

        public BardEditModel(BardSettings settings)
        {
            Settings = settings;
            PartySongs = new ObservableCollection<string>(settings.PartySongs);
            DummySongs = new ObservableCollection<string>(settings.DummySongs);
            OwnedInstruments = new ObservableCollection<string>(settings.OwnedInstruments);
            EnemyDebuffSongs = new ObservableCollection<string>(settings.EnemyDebuffSongs);
            PianissimoTargets = new ObservableCollection<PianissimoTarget>(
                settings.PianissimoTargets.Select(p => new PianissimoTarget { Song = p.Song, Target = p.Target }));
        }

        public void Commit()
        {
            Settings.PartySongs = Clean(PartySongs);
            Settings.DummySongs = Clean(DummySongs);
            Settings.OwnedInstruments = Clean(OwnedInstruments);
            Settings.EnemyDebuffSongs = Clean(EnemyDebuffSongs);
            Settings.PianissimoTargets = PianissimoTargets
                .Where(p => !string.IsNullOrWhiteSpace(p.Song) && !string.IsNullOrWhiteSpace(p.Target))
                .Select(p => new PianissimoTarget { Song = p.Song.Trim(), Target = p.Target.Trim() })
                .ToList();
        }

        private static List<string> Clean(IEnumerable<string> items)
        {
            return items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        }

        /// <summary>Song names for the pickers (the legacy Form2 combo list, corrected).</summary>
        public static readonly IReadOnlyList<string> KnownSongs = new[]
        {
            "Knight's Minne", "Knight's Minne II", "Knight's Minne III", "Knight's Minne IV", "Knight's Minne V",
            "Valor Minuet", "Valor Minuet II", "Valor Minuet III", "Valor Minuet IV", "Valor Minuet V",
            "Army's Paeon", "Army's Paeon II", "Army's Paeon III", "Army's Paeon IV", "Army's Paeon V", "Army's Paeon VI",
            "Sword Madrigal", "Blade Madrigal",
            "Hunter's Prelude", "Archer's Prelude",
            "Sinewy Etude", "Dextrous Etude", "Vivacious Etude", "Quick Etude", "Learned Etude", "Spirited Etude",
            "Enchanting Etude", "Herculean Etude", "Uncanny Etude", "Vital Etude", "Swift Etude", "Sage Etude",
            "Logical Etude", "Bewitching Etude",
            "Sheepfoe Mambo", "Dragonfoe Mambo",
            "Mage's Ballad", "Mage's Ballad II", "Mage's Ballad III",
            "Advancing March", "Victory March", "Honor March",
            "Fire Carol", "Fire Carol II", "Ice Carol", "Ice Carol II", "Wind Carol", "Wind Carol II",
            "Earth Carol", "Earth Carol II", "Lightning Carol", "Lightning Carol II",
            "Water Carol", "Water Carol II", "Light Carol", "Light Carol II", "Dark Carol", "Dark Carol II",
            "Goddess's Hymnus", "Sentinel's Scherzo", "Aria of Passion",
            "Foe Requiem", "Foe Requiem II", "Foe Requiem III", "Foe Requiem IV", "Foe Requiem V",
            "Foe Requiem VI", "Foe Requiem VII",
            "Carnage Elegy", "Battlefield Elegy",
            "Fire Threnody", "Ice Threnody", "Wind Threnody", "Earth Threnody",
            "Lightning Threnody", "Water Threnody", "Light Threnody", "Dark Threnody",
            "Foe Lullaby", "Foe Lullaby II", "Horde Lullaby", "Horde Lullaby II",
            "Maiden's Virelai", "Magic Finale",
        };

        /// <summary>
        /// Enemy-target songs the engine can actually track (timer-based families per
        /// BardModule.FamilyOf): requiems, elegies, threnodies, lullabies. Instant
        /// effects (Magic Finale) and charms (Virelai) have no trackable family.
        /// </summary>
        public static readonly IReadOnlyList<string> KnownEnemySongs = KnownSongs
            .Where(s =>
            {
                FFXI.Memory.StatusEffect family = Core.Modules.BardModule.FamilyOf(s);
                return family == FFXI.Memory.StatusEffect.Requiem
                    || family == FFXI.Memory.StatusEffect.Elegy
                    || family == FFXI.Memory.StatusEffect.Nocturne
                    || family == FFXI.Memory.StatusEffect.Threnody
                    || family == FFXI.Memory.StatusEffect.Lullaby;
            })
            .ToList();

        public static readonly IReadOnlyList<string> DummyPlacements = new[] { "Middle", "First" };
    }
}
