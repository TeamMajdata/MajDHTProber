using System;
using Avalonia.Threading;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Marshals work onto the Avalonia UI thread.</summary>
    public sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        /// <summary>Checks whether the caller runs on the Avalonia UI thread.</summary>
        /// <returns>Whether UI state may be touched directly.</returns>
        public bool CheckAccess()
        {
            return Dispatcher.UIThread.CheckAccess();
        }

        /// <summary>Runs the action inline on the UI thread, or queues it from any other thread.</summary>
        /// <param name="action">The action to run on the UI thread.</param>
        public void Post(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }

        /// <summary>Starts a repeating dispatcher timer.</summary>
        /// <param name="interval">The tick interval.</param>
        /// <param name="tick">The action invoked on the UI thread at every tick.</param>
        /// <returns>A handle that stops the timer when disposed.</returns>
        public IDisposable StartTimer(TimeSpan interval, Action tick)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (_, _) => tick();
            timer.Start();
            return new TimerHandle(timer);
        }

        /// <summary>Stops a dispatcher timer when disposed.</summary>
        private sealed class TimerHandle : IDisposable
        {
            /// <summary>The running timer.</summary>
            private readonly DispatcherTimer _timer;

            /// <summary>Wraps a running timer.</summary>
            /// <param name="timer">The running timer.</param>
            internal TimerHandle(DispatcherTimer timer)
            {
                _timer = timer;
            }

            /// <summary>Stops the timer.</summary>
            public void Dispose()
            {
                _timer.Stop();
            }
        }
    }
}
