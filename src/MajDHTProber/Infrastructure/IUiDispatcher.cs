using System;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>
    /// Marshals work onto the UI thread and schedules periodic UI refreshes, so view models can be driven by node events
    /// raised on background threads and remain testable without a running UI framework.
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>Checks whether the caller runs on the UI thread.</summary>
        /// <returns>Whether UI state may be touched directly.</returns>
        bool CheckAccess();

        /// <summary>Queues an action for the UI thread.</summary>
        /// <param name="action">The action to run on the UI thread.</param>
        void Post(Action action);

        /// <summary>Starts a repeating UI-thread timer.</summary>
        /// <param name="interval">The tick interval.</param>
        /// <param name="tick">The action invoked on the UI thread at every tick.</param>
        /// <returns>A handle that stops the timer when disposed.</returns>
        IDisposable StartTimer(TimeSpan interval, Action tick);
    }
}
