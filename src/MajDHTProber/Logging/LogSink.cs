using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Logging
{
    /// <summary>
    /// Collects diagnostic events from the prober and from every hosted node in a bounded, thread-safe buffer and notifies
    /// listeners about new events.
    /// </summary>
    /// <remarks>
    /// Events can arrive from socket and maintenance threads of the hosted nodes, so listeners must marshal to the UI thread
    /// themselves. Listener failures are isolated so that diagnostics can never break node operations.
    /// </remarks>
    public sealed class LogSink
    {
        /// <summary>The default number of retained events.</summary>
        public const int DefaultCapacity = 5000;

        /// <summary>Protects the buffer, the counters and the sequence number.</summary>
        private readonly Lock _gate = new Lock();
        /// <summary>The retained events, oldest first.</summary>
        private readonly Queue<LogEntry> _entries;
        /// <summary>The maximum number of retained events.</summary>
        private readonly int _capacity;
        /// <summary>The time source used to stamp events.</summary>
        private readonly Func<DateTimeOffset> _clock;
        /// <summary>The last issued sequence number.</summary>
        private long _sequence;
        /// <summary>The number of warnings written since the last clear.</summary>
        private int _warningCount;
        /// <summary>The number of errors written since the last clear.</summary>
        private int _errorCount;
        /// <summary>The lowest severity that is captured.</summary>
        private volatile int _minimumLevel = (int)LogLevel.Information;

        /// <summary>Creates a sink.</summary>
        /// <param name="capacity">The maximum number of retained events; older events are discarded first.</param>
        /// <param name="clock">An optional time source, used by tests; defaults to the current UTC time.</param>
        /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive.</exception>
        public LogSink(int capacity = DefaultCapacity, Func<DateTimeOffset>? clock = null)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _capacity = capacity;
            _entries = new Queue<LogEntry>(Math.Min(capacity, 1024));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Raised after an event was captured; may be raised on any thread.</summary>
        public event EventHandler<LogEntry>? EntryAdded;

        /// <summary>Raised after the buffer was cleared; may be raised on any thread.</summary>
        public event EventHandler? Cleared;

        /// <summary>Gets or sets the lowest severity that is captured.</summary>
        public LogLevel MinimumLevel
        {
            get => (LogLevel)_minimumLevel;
            set => _minimumLevel = (int)value;
        }

        /// <summary>Gets the number of warnings captured since the last clear.</summary>
        public int WarningCount
        {
            get
            {
                lock (_gate)
                {
                    return _warningCount;
                }
            }
        }

        /// <summary>Gets the number of errors captured since the last clear.</summary>
        public int ErrorCount
        {
            get
            {
                lock (_gate)
                {
                    return _errorCount;
                }
            }
        }

        /// <summary>Creates a logger that writes into this sink.</summary>
        /// <param name="category">The category shown next to each event, such as a node label.</param>
        /// <returns>A thread-safe logger.</returns>
        public ILogger CreateLogger(string category)
        {
            return new SinkLogger(this, category);
        }

        /// <summary>Checks whether events of a severity are currently captured.</summary>
        /// <param name="level">The severity.</param>
        /// <returns>Whether an event of this severity would be captured.</returns>
        public bool IsEnabled(LogLevel level)
        {
            return level != LogLevel.None && (int)level >= _minimumLevel;
        }

        /// <summary>Captures one event when its severity is enabled.</summary>
        /// <param name="level">The event severity.</param>
        /// <param name="category">The logger category.</param>
        /// <param name="message">The formatted message.</param>
        /// <param name="exception">An optional failure whose type and message are summarized.</param>
        /// <returns>The captured entry, or null when the severity is disabled.</returns>
        public LogEntry? Write(LogLevel level, string category, string message, Exception? exception = null)
        {
            if (!IsEnabled(level))
            {
                return null;
            }
            var failure = exception == null ? null : exception.GetType().Name + ": " + exception.Message;
            LogEntry entry;
            lock (_gate)
            {
                entry = new LogEntry(++_sequence, _clock(), level, category, message, failure);
                _entries.Enqueue(entry);
                while (_entries.Count > _capacity)
                {
                    _entries.Dequeue();
                }
                if (level == LogLevel.Warning)
                {
                    _warningCount++;
                }
                else if (level >= LogLevel.Error)
                {
                    _errorCount++;
                }
            }
            Notify(entry);
            return entry;
        }

        /// <summary>Returns the retained events, oldest first.</summary>
        /// <returns>An owned snapshot.</returns>
        public IReadOnlyList<LogEntry> Snapshot()
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }

        /// <summary>Discards every retained event and resets the counters.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _warningCount = 0;
                _errorCount = 0;
            }
            try
            {
                Cleared?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A failing listener must not break the caller.
            }
        }

        /// <summary>Notifies listeners about a captured event while isolating listener failures.</summary>
        /// <param name="entry">The captured event.</param>
        private void Notify(LogEntry entry)
        {
            try
            {
                EntryAdded?.Invoke(this, entry);
            }
            catch (Exception)
            {
                // Diagnostics must never break node threads.
            }
        }

        /// <summary>Adapts the standard logging abstraction to the sink.</summary>
        private sealed class SinkLogger : ILogger
        {
            /// <summary>The owning sink.</summary>
            private readonly LogSink _sink;
            /// <summary>The category attached to every event.</summary>
            private readonly string _category;

            /// <summary>Creates a logger for a category.</summary>
            /// <param name="sink">The owning sink.</param>
            /// <param name="category">The category attached to every event.</param>
            internal SinkLogger(LogSink sink, string category)
            {
                _sink = sink;
                _category = category;
            }

            /// <summary>Scopes are not recorded by the sink.</summary>
            /// <typeparam name="TState">The scope state type.</typeparam>
            /// <param name="state">The ignored scope state.</param>
            /// <returns>Always null.</returns>
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            /// <summary>Checks whether a severity is captured by the sink.</summary>
            /// <param name="logLevel">The severity.</param>
            /// <returns>Whether the sink captures the severity.</returns>
            public bool IsEnabled(LogLevel logLevel)
            {
                return _sink.IsEnabled(logLevel);
            }

            /// <summary>Formats and captures one event.</summary>
            /// <typeparam name="TState">The state type.</typeparam>
            /// <param name="logLevel">The severity.</param>
            /// <param name="eventId">The ignored event identifier.</param>
            /// <param name="state">The structured state.</param>
            /// <param name="exception">An optional failure.</param>
            /// <param name="formatter">Formats the state and failure into the message text.</param>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!_sink.IsEnabled(logLevel))
                {
                    return;
                }
                _sink.Write(logLevel, _category, formatter(state, exception), exception);
            }
        }
    }
}
