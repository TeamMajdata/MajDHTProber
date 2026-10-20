using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTProber.Logging;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One severity choice of the log page.</summary>
    public sealed class LogLevelOption
    {
        /// <summary>Creates a choice.</summary>
        /// <param name="label">The display text.</param>
        /// <param name="level">The severity.</param>
        public LogLevelOption(string label, LogLevel level)
        {
            Label = label;
            Level = level;
        }

        /// <summary>Gets the display text.</summary>
        public string Label { get; }

        /// <summary>Gets the severity.</summary>
        public LogLevel Level { get; }
    }

    /// <summary>The log page: a live, filterable view of every captured diagnostic event.</summary>
    public sealed partial class LogsViewModel : PageViewModel, IDisposable
    {
        /// <summary>The maximum number of displayed events.</summary>
        public const int MaxVisible = 3000;
        /// <summary>The flush interval of the live feed.</summary>
        private static readonly TimeSpan s_flushInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>The captured events waiting for the next flush.</summary>
        private readonly ConcurrentQueue<LogEntry> _pending = new ConcurrentQueue<LogEntry>();
        /// <summary>The known categories.</summary>
        private readonly SortedSet<string> _categories = new SortedSet<string>(StringComparer.Ordinal);
        /// <summary>The flush timer.</summary>
        private readonly IDisposable _timer;
        /// <summary>The log sink.</summary>
        private readonly LogSink _sink;
        /// <summary>Whether the constructor finished, so filter changes may rebuild the view.</summary>
        private readonly bool _initialized;

        /// <summary>Creates the page and subscribes to the sink.</summary>
        /// <param name="context">The shared services.</param>
        public LogsViewModel(ProberContext context)
            : base(context)
        {
            _sink = context.Manager.Sink;
            LevelFilters = new[]
            {
                new LogLevelOption("全部级别", LogLevel.Trace),
                new LogLevelOption("调试及以上", LogLevel.Debug),
                new LogLevelOption("信息及以上", LogLevel.Information),
                new LogLevelOption("警告及以上", LogLevel.Warning),
                new LogLevelOption("仅错误", LogLevel.Error),
            };
            CaptureLevels = new[]
            {
                new LogLevelOption("调试 (Debug)", LogLevel.Debug),
                new LogLevelOption("信息 (Info)", LogLevel.Information),
                new LogLevelOption("警告 (Warning)", LogLevel.Warning),
                new LogLevelOption("错误 (Error)", LogLevel.Error),
            };
            Categories.Add(AllCategories);
            SelectedCategory = AllCategories;
            SelectedLevelFilter = LevelFilters[0];
            SelectedCaptureLevel = Find(CaptureLevels, _sink.MinimumLevel) ?? CaptureLevels[1];
            foreach (var entry in _sink.Snapshot())
            {
                _pending.Enqueue(entry);
            }
            _sink.EntryAdded += OnEntryAdded;
            _sink.Cleared += OnCleared;
            _timer = context.Dispatcher.StartTimer(s_flushInterval, Flush);
            _initialized = true;
            Flush();
        }

        /// <summary>The category choice that matches every category.</summary>
        public static string AllCategories => "全部来源";

        /// <summary>Gets the displayed events, oldest first.</summary>
        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        /// <summary>Gets the category choices.</summary>
        public ObservableCollection<string> Categories { get; } = new ObservableCollection<string>();

        /// <summary>Gets the display severity choices.</summary>
        public IReadOnlyList<LogLevelOption> LevelFilters { get; }

        /// <summary>Gets the capture severity choices.</summary>
        public IReadOnlyList<LogLevelOption> CaptureLevels { get; }

        /// <summary>Gets or sets the displayed severity.</summary>
        [ObservableProperty]
        public partial LogLevelOption SelectedLevelFilter { get; set; }

        /// <summary>Gets or sets the captured severity.</summary>
        [ObservableProperty]
        public partial LogLevelOption SelectedCaptureLevel { get; set; }

        /// <summary>Gets or sets the displayed category.</summary>
        [ObservableProperty]
        public partial string SelectedCategory { get; set; }

        /// <summary>Gets or sets the search text.</summary>
        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the live feed is paused.</summary>
        [ObservableProperty]
        public partial bool IsPaused { get; set; }

        /// <summary>Gets or sets whether the view follows the newest event.</summary>
        [ObservableProperty]
        public partial bool AutoScroll { get; set; } = true;

        /// <summary>Gets or sets the selected event.</summary>
        [ObservableProperty]
        public partial LogEntry? SelectedEntry { get; set; }

        /// <summary>Gets or sets the event counter text.</summary>
        [ObservableProperty]
        public partial string CountText { get; set; } = string.Empty;

        /// <summary>Raised on the UI thread after events were appended while <see cref="AutoScroll"/> is on.</summary>
        public event EventHandler? ScrollToEndRequested;

        /// <summary>Stops the live feed.</summary>
        public void Dispose()
        {
            _sink.EntryAdded -= OnEntryAdded;
            _sink.Cleared -= OnCleared;
            _timer.Dispose();
        }

        /// <summary>Checks whether an event passes the current filters.</summary>
        /// <param name="entry">The event.</param>
        /// <returns>Whether the event is displayed.</returns>
        public bool Matches(LogEntry entry)
        {
            if (entry.Level < SelectedLevelFilter.Level)
            {
                return false;
            }
            if (SelectedCategory != AllCategories && entry.Category != SelectedCategory)
            {
                return false;
            }
            var search = SearchText.Trim();
            return search.Length == 0 || entry.FullText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Moves pending events into the view.</summary>
        public void Flush()
        {
            if (IsPaused)
            {
                return;
            }
            var appended = false;
            while (_pending.TryDequeue(out var entry))
            {
                if (_categories.Add(entry.Category))
                {
                    InsertCategory(entry.Category);
                }
                if (Matches(entry))
                {
                    Entries.Add(entry);
                    appended = true;
                }
            }
            while (Entries.Count > MaxVisible)
            {
                Entries.RemoveAt(0);
            }
            UpdateCount();
            if (appended && AutoScroll)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Applies the display severity.</summary>
        /// <param name="value">The new severity.</param>
        partial void OnSelectedLevelFilterChanged(LogLevelOption value)
        {
            Rebuild();
        }

        /// <summary>Applies the capture severity to the sink.</summary>
        /// <param name="value">The new severity.</param>
        partial void OnSelectedCaptureLevelChanged(LogLevelOption value)
        {
            _sink.MinimumLevel = value.Level;
        }

        /// <summary>Applies the category filter.</summary>
        /// <param name="value">The new category.</param>
        partial void OnSelectedCategoryChanged(string value)
        {
            Rebuild();
        }

        /// <summary>Applies the search filter.</summary>
        /// <param name="value">The new search text.</param>
        partial void OnSearchTextChanged(string value)
        {
            Rebuild();
        }

        /// <summary>Flushes the events held back while paused.</summary>
        /// <param name="value">Whether the feed is paused.</param>
        partial void OnIsPausedChanged(bool value)
        {
            if (!value)
            {
                Flush();
            }
        }

        /// <summary>Clears every captured event.</summary>
        [RelayCommand]
        private void Clear()
        {
            _sink.Clear();
        }

        /// <summary>Copies the selected event.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopySelectedAsync()
        {
            return CopyAsync(SelectedEntry?.ToExportLine(), "日志条目");
        }

        /// <summary>Exports the displayed events to a text file.</summary>
        /// <returns>Completion after exporting.</returns>
        [RelayCommand]
        private async Task ExportAsync()
        {
            var path = await Context.Interaction.PickSaveFileAsync("导出日志", "majdht-prober-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            if (path == null)
            {
                return;
            }
            await RunAsync("导出日志", async () =>
            {
                var builder = new StringBuilder();
                foreach (var entry in Entries)
                {
                    builder.AppendLine(entry.ToExportLine());
                }
                await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false));
                Notify("已导出 " + Entries.Count + " 条日志到 " + Path.GetFileName(path) + "。", StatusTone.Success);
            });
        }

        /// <summary>Rebuilds the displayed events from the sink.</summary>
        private void Rebuild()
        {
            if (!_initialized)
            {
                return;
            }
            Entries.Clear();
            var snapshot = _sink.Snapshot();
            var matched = new List<LogEntry>();
            for (var index = snapshot.Count - 1; index >= 0; index--)
            {
                var entry = snapshot[index];
                if (_categories.Add(entry.Category))
                {
                    InsertCategory(entry.Category);
                }
                if (matched.Count < MaxVisible && Matches(entry))
                {
                    matched.Add(entry);
                }
            }
            for (var index = matched.Count - 1; index >= 0; index--)
            {
                Entries.Add(matched[index]);
            }
            while (_pending.TryDequeue(out _))
            {
                // Pending events are already part of the sink snapshot.
            }
            UpdateCount();
            if (AutoScroll)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Inserts a category choice in sorted order after the "all" choice.</summary>
        /// <param name="category">The category.</param>
        private void InsertCategory(string category)
        {
            var index = 1;
            while (index < Categories.Count && string.CompareOrdinal(Categories[index], category) < 0)
            {
                index++;
            }
            Categories.Insert(index, category);
        }

        /// <summary>Updates the counter text.</summary>
        private void UpdateCount()
        {
            CountText = "显示 " + Entries.Count + " 条 · 警告 " + _sink.WarningCount + " · 错误 " + _sink.ErrorCount;
        }

        /// <summary>Queues a captured event from any thread.</summary>
        /// <param name="sender">The sink.</param>
        /// <param name="entry">The event.</param>
        private void OnEntryAdded(object? sender, LogEntry entry)
        {
            _pending.Enqueue(entry);
        }

        /// <summary>Clears the view after the sink was cleared.</summary>
        /// <param name="sender">The sink.</param>
        /// <param name="args">The ignored event data.</param>
        private void OnCleared(object? sender, EventArgs args)
        {
            Context.Dispatcher.Post(() =>
            {
                while (_pending.TryDequeue(out _))
                {
                    // Discard events captured before the clear.
                }
                Entries.Clear();
                UpdateCount();
            });
        }

        /// <summary>Finds the choice of a severity.</summary>
        /// <param name="options">The choices.</param>
        /// <param name="level">The severity.</param>
        /// <returns>The choice, or null.</returns>
        private static LogLevelOption? Find(IReadOnlyList<LogLevelOption> options, LogLevel level)
        {
            foreach (var option in options)
            {
                if (option.Level == level)
                {
                    return option;
                }
            }
            return null;
        }
    }
}
