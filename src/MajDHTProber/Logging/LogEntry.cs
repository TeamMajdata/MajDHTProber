using System;
using System.Globalization;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Logging
{
    /// <summary>One immutable diagnostic event captured by a <see cref="LogSink"/>.</summary>
    public sealed class LogEntry
    {
        /// <summary>Creates a captured event.</summary>
        /// <param name="sequence">The strictly increasing capture number within the sink.</param>
        /// <param name="timestamp">The time at which the event was written.</param>
        /// <param name="level">The event severity.</param>
        /// <param name="category">The logger category, such as a node label or a service name.</param>
        /// <param name="message">The formatted message text.</param>
        /// <param name="failure">The failure summary (exception type and message), or null when the event has no exception.</param>
        public LogEntry(long sequence, DateTimeOffset timestamp, LogLevel level, string category, string message, string? failure)
        {
            Sequence = sequence;
            Timestamp = timestamp;
            Level = level;
            Category = category;
            Message = message;
            Failure = failure;
        }

        /// <summary>Gets the strictly increasing capture number within the sink.</summary>
        public long Sequence { get; }

        /// <summary>Gets the time at which the event was written.</summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>Gets the event severity.</summary>
        public LogLevel Level { get; }

        /// <summary>Gets the logger category.</summary>
        public string Category { get; }

        /// <summary>Gets the formatted message text.</summary>
        public string Message { get; }

        /// <summary>Gets the failure summary, or null when the event has no exception.</summary>
        public string? Failure { get; }

        /// <summary>Gets the local wall-clock time formatted for the log view.</summary>
        public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        /// <summary>Gets a short fixed-width severity label.</summary>
        public string LevelText => Level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "---",
        };

        /// <summary>Gets the message followed by the failure summary when one exists.</summary>
        public string FullText => Failure == null ? Message : Message + " | " + Failure;

        /// <summary>Formats the entry as one plain-text export line.</summary>
        /// <returns>The timestamp, severity, category and full text separated by tabs.</returns>
        public string ToExportLine()
        {
            return string.Join('\t', Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), LevelText, Category, FullText);
        }
    }
}
