using System.ComponentModel;

namespace CurePlease.Settings.Pages
{
    /// <summary>
    /// Edit model for the White Mage page. The Afflatus stances overwrite each
    /// other in-game, so enabling one clears the other. Everything else passes
    /// straight through to the settings clone.
    /// </summary>
    public sealed class WhmEditModel : INotifyPropertyChanged
    {
        public WhmSettings Settings { get; }

        public WhmEditModel(WhmSettings settings)
        {
            Settings = settings;
        }

        public bool Enabled
        {
            get => Settings.Enabled;
            set { Settings.Enabled = value; Notify(nameof(Enabled)); }
        }

        public bool UseAfflatusSolace
        {
            get => Settings.UseAfflatusSolace;
            set
            {
                if (value && Settings.UseAfflatusMisery)
                {
                    Settings.UseAfflatusMisery = false;
                    Notify(nameof(UseAfflatusMisery));
                }

                Settings.UseAfflatusSolace = value;
                Notify(nameof(UseAfflatusSolace));
            }
        }

        public bool UseAfflatusMisery
        {
            get => Settings.UseAfflatusMisery;
            set
            {
                if (value && Settings.UseAfflatusSolace)
                {
                    Settings.UseAfflatusSolace = false;
                    Notify(nameof(UseAfflatusSolace));
                }

                Settings.UseAfflatusMisery = value;
                Notify(nameof(UseAfflatusMisery));
            }
        }

        public bool UseDevotion
        {
            get => Settings.UseDevotion;
            set { Settings.UseDevotion = value; Notify(nameof(UseDevotion)); }
        }

        public string DevotionTarget
        {
            get => Settings.DevotionTarget;
            set { Settings.DevotionTarget = value; Notify(nameof(DevotionTarget)); }
        }

        public uint DevotionMpBelow
        {
            get => Settings.DevotionMpBelow;
            set { Settings.DevotionMpBelow = value; Notify(nameof(DevotionMpBelow)); }
        }

        public bool DevotionOnlyWhenEngaged
        {
            get => Settings.DevotionOnlyWhenEngaged;
            set { Settings.DevotionOnlyWhenEngaged = value; Notify(nameof(DevotionOnlyWhenEngaged)); }
        }

        private void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
