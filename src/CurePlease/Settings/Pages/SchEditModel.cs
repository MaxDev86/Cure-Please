using System.Collections.Generic;
using System.ComponentModel;
using CurePlease.Core;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the Scholar page. The arts are mutually exclusive in-game
    /// (each overwrites the other), and an addendum requires its matching art, so
    /// the setters cascade:
    ///  - enabling an art clears the other art and its addendum;
    ///  - enabling an addendum forces its art on (and the other art/addendum off);
    ///  - disabling an art clears its addendum.
    /// Scalars (Sublimation etc.) pass straight through to the settings clone.
    /// </summary>
    public sealed class SchEditModel : INotifyPropertyChanged
    {
        public SchSettings Settings { get; }

        public SchEditModel(SchSettings settings)
        {
            Settings = settings;
        }

        public bool Enabled
        {
            get => Settings.Enabled;
            set { Settings.Enabled = value; Notify(nameof(Enabled)); }
        }

        public bool LightArts
        {
            get => Settings.LightArts;
            set
            {
                if (value)
                {
                    Settings.DarkArts = false;
                    Settings.AddendumBlack = false;
                    Notify(nameof(DarkArts));
                    Notify(nameof(AddendumBlack));
                }
                else if (Settings.AddendumWhite)
                {
                    Settings.AddendumWhite = false;
                    Notify(nameof(AddendumWhite));
                }

                Settings.LightArts = value;
                Notify(nameof(LightArts));
            }
        }

        public bool DarkArts
        {
            get => Settings.DarkArts;
            set
            {
                if (value)
                {
                    Settings.LightArts = false;
                    Settings.AddendumWhite = false;
                    Notify(nameof(LightArts));
                    Notify(nameof(AddendumWhite));
                }
                else if (Settings.AddendumBlack)
                {
                    Settings.AddendumBlack = false;
                    Notify(nameof(AddendumBlack));
                }

                Settings.DarkArts = value;
                Notify(nameof(DarkArts));
            }
        }

        public bool AddendumWhite
        {
            get => Settings.AddendumWhite;
            set
            {
                if (value)
                {
                    // Requires Light Arts; implies the other art/addendum are off.
                    Settings.DarkArts = false;
                    Settings.AddendumBlack = false;
                    Settings.LightArts = true;
                    Notify(nameof(DarkArts));
                    Notify(nameof(AddendumBlack));
                    Notify(nameof(LightArts));
                }

                Settings.AddendumWhite = value;
                Notify(nameof(AddendumWhite));
            }
        }

        public bool AddendumBlack
        {
            get => Settings.AddendumBlack;
            set
            {
                if (value)
                {
                    Settings.LightArts = false;
                    Settings.AddendumWhite = false;
                    Settings.DarkArts = true;
                    Notify(nameof(LightArts));
                    Notify(nameof(AddendumWhite));
                    Notify(nameof(DarkArts));
                }

                Settings.AddendumBlack = value;
                Notify(nameof(AddendumBlack));
            }
        }

        public bool Sublimation
        {
            get => Settings.Sublimation;
            set { Settings.Sublimation = value; Notify(nameof(Sublimation)); }
        }

        public uint SublimationMpDeficit
        {
            get => Settings.SublimationMpDeficit;
            set { Settings.SublimationMpDeficit = value; Notify(nameof(SublimationMpDeficit)); }
        }

        /// <summary>Perpetuance multiplier tiers for the dropdown.</summary>
        public IReadOnlyList<PerpetuanceGear.Tier> PerpetuanceTiers => PerpetuanceGear.Tiers;

        /// <summary>
        /// Selected tier index. Setting it marks the multiplier as a manual choice —
        /// the attach-time gear scan no longer overwrites it (storage-slip case).
        /// </summary>
        public int PerpetuanceTierIndex
        {
            get => PerpetuanceGear.TierIndexOf(Settings.PerpetuanceMultiplier);
            set
            {
                if (value >= 0 && value < PerpetuanceGear.Tiers.Count)
                {
                    Settings.PerpetuanceMultiplier = PerpetuanceGear.Tiers[value].Multiplier;
                    Settings.PerpetuanceMultiplierManual = true;
                    Notify(nameof(PerpetuanceTierIndex));
                }
            }
        }

        private void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
