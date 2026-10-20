using System;
using System.Collections.Generic;
using MajDHTProber.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Covers capture, filtering, bounding and listener isolation of the log sink.</summary>
    public sealed class LogSinkTests
    {
        /// <summary>Events below the minimum severity are not captured.</summary>
        [Fact]
        public void IgnoresEventsBelowMinimumLevel()
        {
            var sink = new LogSink { MinimumLevel = LogLevel.Information };
            var logger = sink.CreateLogger("测试");

            logger.LogDebug("调试");
            logger.LogInformation("信息 {Value}", 42);

            var entries = sink.Snapshot();
            Assert.Single(entries);
            Assert.Equal("信息 42", entries[0].Message);
            Assert.Equal("测试", entries[0].Category);
            Assert.Equal("INF", entries[0].LevelText);
        }

        /// <summary>The buffer keeps only the newest events and counts warnings and errors.</summary>
        [Fact]
        public void BoundsBufferAndCountsProblems()
        {
            var sink = new LogSink(3);
            sink.Write(LogLevel.Warning, "a", "1");
            sink.Write(LogLevel.Error, "a", "2");
            sink.Write(LogLevel.Information, "a", "3");
            sink.Write(LogLevel.Information, "a", "4");

            var entries = sink.Snapshot();
            Assert.Equal(new[] { "2", "3", "4" }, new[] { entries[0].Message, entries[1].Message, entries[2].Message });
            Assert.Equal(1, sink.WarningCount);
            Assert.Equal(1, sink.ErrorCount);

            sink.Clear();
            Assert.Empty(sink.Snapshot());
            Assert.Equal(0, sink.ErrorCount);
        }

        /// <summary>Exceptions are summarized by type and message, and a throwing listener does not break writers.</summary>
        [Fact]
        public void SummarizesFailuresAndIsolatesListeners()
        {
            var sink = new LogSink();
            var received = new List<LogEntry>();
            sink.EntryAdded += (_, _) => throw new InvalidOperationException("listener");
            sink.EntryAdded += (_, entry) => received.Add(entry);

            var entry = sink.Write(LogLevel.Error, "a", "失败", new TimeoutException("超时"));

            Assert.NotNull(entry);
            Assert.Equal("TimeoutException: 超时", entry!.Failure);
            Assert.Contains("失败 | TimeoutException", entry.FullText, StringComparison.Ordinal);
            Assert.Contains("\tERR\ta\t", entry.ToExportLine(), StringComparison.Ordinal);
        }

        /// <summary>Sequence numbers increase strictly.</summary>
        [Fact]
        public void IssuesIncreasingSequenceNumbers()
        {
            var sink = new LogSink();
            var first = sink.Write(LogLevel.Information, "a", "1")!;
            var second = sink.Write(LogLevel.Information, "a", "2")!;

            Assert.True(second.Sequence > first.Sequence);
        }
    }
}
