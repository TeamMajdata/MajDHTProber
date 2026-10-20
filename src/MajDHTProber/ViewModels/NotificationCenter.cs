using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTProber.Infrastructure;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>Shows short transient notifications (toasts) about the outcome of user actions.</summary>
    public sealed partial class NotificationCenter : TonedViewModel, IDisposable
    {
        /// <summary>How long a notification stays visible.</summary>
        public static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(4.5);

        /// <summary>Schedules the automatic dismissal.</summary>
        private readonly IUiDispatcher _dispatcher;
        /// <summary>The pending dismissal timer.</summary>
        private IDisposable? _dismissal;

        /// <summary>Creates the notification center.</summary>
        /// <param name="dispatcher">Marshals notifications to the UI thread and schedules dismissal.</param>
        public NotificationCenter(IUiDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>Gets or sets the notification text.</summary>
        [ObservableProperty]
        public partial string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets whether a notification is visible.</summary>
        [ObservableProperty]
        public partial bool IsVisible { get; set; }

        /// <summary>Shows a notification from any thread, replacing the current one.</summary>
        /// <param name="message">The text.</param>
        /// <param name="tone">The tone.</param>
        public void Show(string message, StatusTone tone)
        {
            _dispatcher.Post(() =>
            {
                Message = message;
                Tone = tone;
                IsVisible = true;
                _dismissal?.Dispose();
                _dismissal = _dispatcher.StartTimer(DisplayDuration, Dismiss);
            });
        }

        /// <summary>Hides the current notification.</summary>
        [RelayCommand]
        public void Dismiss()
        {
            _dismissal?.Dispose();
            _dismissal = null;
            IsVisible = false;
        }

        /// <summary>Stops the pending dismissal timer.</summary>
        public void Dispose()
        {
            _dismissal?.Dispose();
            _dismissal = null;
        }
    }
}
