using System;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>
    /// Runs posted work immediately on the calling thread and never ticks timers; used by tests and design-time previews
    /// where no UI thread exists.
    /// </summary>
    public sealed class InlineUiDispatcher : IUiDispatcher
    {
        /// <summary>Gets the shared instance.</summary>
        public static InlineUiDispatcher Instance { get; } = new InlineUiDispatcher();

        /// <summary>Always reports access, because work runs inline.</summary>
        /// <returns>Always true.</returns>
        public bool CheckAccess()
        {
            return true;
        }

        /// <summary>Runs the action immediately.</summary>
        /// <param name="action">The action to run.</param>
        public void Post(Action action)
        {
            action();
        }

        /// <summary>Returns an inert timer; ticks are driven explicitly by tests instead.</summary>
        /// <param name="interval">The ignored interval.</param>
        /// <param name="tick">The ignored tick action.</param>
        /// <returns>A handle whose disposal does nothing.</returns>
        public IDisposable StartTimer(TimeSpan interval, Action tick)
        {
            return new InertTimer();
        }

        /// <summary>A timer handle that does nothing.</summary>
        private sealed class InertTimer : IDisposable
        {
            /// <summary>Does nothing.</summary>
            public void Dispose()
            {
            }
        }
    }
}
