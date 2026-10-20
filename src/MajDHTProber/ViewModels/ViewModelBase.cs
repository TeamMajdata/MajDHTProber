using CommunityToolkit.Mvvm.ComponentModel;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>The base class of every view model, providing property change notification.</summary>
    public abstract class ViewModelBase : ObservableObject
    {
    }

    /// <summary>The visual tone of a status element, mapped to colors by the theme.</summary>
    public enum StatusTone
    {
        /// <summary>No particular meaning.</summary>
        Neutral,
        /// <summary>Informational or in progress.</summary>
        Info,
        /// <summary>Healthy or successful.</summary>
        Success,
        /// <summary>Degraded or needs attention.</summary>
        Warning,
        /// <summary>Failed or broken.</summary>
        Danger,
    }

    /// <summary>A view model with a <see cref="StatusTone"/> exposed as boolean flags for style classes.</summary>
    public abstract partial class TonedViewModel : ViewModelBase
    {
        /// <summary>Gets or sets the visual tone.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNeutral), nameof(IsInfo), nameof(IsSuccess), nameof(IsWarning), nameof(IsDanger))]
        public partial StatusTone Tone { get; set; }

        /// <summary>Gets whether the tone is neutral.</summary>
        public bool IsNeutral => Tone == StatusTone.Neutral;

        /// <summary>Gets whether the tone is informational.</summary>
        public bool IsInfo => Tone == StatusTone.Info;

        /// <summary>Gets whether the tone is successful.</summary>
        public bool IsSuccess => Tone == StatusTone.Success;

        /// <summary>Gets whether the tone is a warning.</summary>
        public bool IsWarning => Tone == StatusTone.Warning;

        /// <summary>Gets whether the tone is a failure.</summary>
        public bool IsDanger => Tone == StatusTone.Danger;
    }
}
