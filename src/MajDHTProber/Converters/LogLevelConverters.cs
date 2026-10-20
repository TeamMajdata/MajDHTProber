using Avalonia.Data.Converters;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Converters
{
    /// <summary>Maps log severities to the boolean style classes used to color log lines.</summary>
    public static class LogLevelConverters
    {
        /// <summary>Gets a converter that is true for trace and debug events.</summary>
        public static FuncValueConverter<LogLevel, bool> IsDebug { get; } = new FuncValueConverter<LogLevel, bool>(level => level <= LogLevel.Debug);

        /// <summary>Gets a converter that is true for warnings.</summary>
        public static FuncValueConverter<LogLevel, bool> IsWarning { get; } = new FuncValueConverter<LogLevel, bool>(level => level == LogLevel.Warning);

        /// <summary>Gets a converter that is true for errors and critical events.</summary>
        public static FuncValueConverter<LogLevel, bool> IsError { get; } = new FuncValueConverter<LogLevel, bool>(level => level >= LogLevel.Error);
    }
}
