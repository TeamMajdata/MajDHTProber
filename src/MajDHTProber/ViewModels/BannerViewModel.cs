using CommunityToolkit.Mvvm.ComponentModel;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>An inline result message shown next to the form that produced it.</summary>
    public sealed partial class BannerViewModel : TonedViewModel
    {
        /// <summary>Gets or sets the message text.</summary>
        [ObservableProperty]
        public partial string Text { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the banner is visible.</summary>
        [ObservableProperty]
        public partial bool IsVisible { get; set; }

        /// <summary>Shows a message.</summary>
        /// <param name="text">The text.</param>
        /// <param name="tone">The tone.</param>
        public void Show(string text, StatusTone tone)
        {
            Text = text;
            Tone = tone;
            IsVisible = true;
        }

        /// <summary>Hides the banner.</summary>
        public void Hide()
        {
            IsVisible = false;
        }
    }
}
