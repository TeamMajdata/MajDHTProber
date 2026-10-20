using System;
using Avalonia;

#nullable enable

namespace MajDHTProber
{
    /// <summary>The desktop entry point.</summary>
    internal static class Program
    {
        /// <summary>Starts the application with the classic desktop lifetime.</summary>
        /// <param name="args">The command-line arguments.</param>
        [STAThread]
        public static void Main(string[] args)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>Configures Avalonia; also used by the visual designer.</summary>
        /// <returns>The configured application builder.</returns>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }
    }
}
