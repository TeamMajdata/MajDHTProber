using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;
using MajDHTProber.ViewModels;
using MajDHTProber.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MajDHTProber.Tests.HeadlessApp))]

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Configures the headless Avalonia application used by UI tests, rendering with Skia so frames can be captured.</summary>
    public static class HeadlessApp
    {
        /// <summary>Builds the headless application.</summary>
        /// <returns>The application builder.</returns>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }
    }

    /// <summary>Renders every page of the main window with live data and checks for binding errors.</summary>
    public sealed class UiRenderingTests
    {
        /// <summary>The environment variable naming a directory that receives page screenshots.</summary>
        public const string ScreenshotVariable = "MAJDHTPROBER_SCREENSHOTS";

        /// <summary>Every page and tool tab renders in both themes without binding errors, with peers, records and logs present.</summary>
        /// <returns>Completion after rendering.</returns>
        [AvaloniaFact]
        public async Task RendersEveryPageWithoutBindingErrors()
        {
            var bindingErrors = new BindingErrorSink();
            var previousSink = Logger.Sink;
            Logger.Sink = bindingErrors;
            using var directory = new TempDirectory();
            var sink = new LogSink();
            var store = new SettingsStore(new AppPaths(directory.Path).SettingsFile, sink.CreateLogger("设置"));
            store.Save(TestNodes.LoopbackSettings());
            using var vault = new IdentityVault(null, sink.CreateLogger("身份"));
            var manager = new NodeSessionManager(store, vault, sink);
            await using var remote = await TestNodes.StartAsync(DHTCapabilities.Download | DHTCapabilities.Provide | DHTCapabilities.Relay);
            var viewModel = new MainWindowViewModel(manager, new AvaloniaUiDispatcher(), new NullUserInteraction());
            var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
            try
            {
                window.Show();
                await manager.Primary.StartAsync();
                await manager.AddLocalNodeAsync(new LocalNodeRequest());
                await manager.Primary.AddPeerAsync(TestNodes.TcpEndpoint(remote), new AddPeerOptions(), default);
                using (var text = new MemoryStream(Encoding.UTF8.GetBytes("界面渲染测试内容")))
                {
                    await manager.Primary.PublishAsync("prober/ui.txt", text);
                }
                Dispatcher.UIThread.RunJobs();
                viewModel.Peers.SelectedPeer = viewModel.Peers.VisiblePeers[0];

                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current!.RequestedThemeVariant = theme;
                    for (var page = 0; page < viewModel.Pages.Count; page++)
                    {
                        viewModel.SelectedPage = viewModel.Pages[page];
                        if (viewModel.SelectedPage.Page is ToolsViewModel tools)
                        {
                            for (var tab = 0; tab < 6; tab++)
                            {
                                tools.SelectedTabIndex = tab;
                                Capture(window, theme + "-tools-" + tab);
                            }
                        }
                        else
                        {
                            Capture(window, theme + "-page-" + page);
                        }
                    }
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;

                Assert.Equal(2, viewModel.Sessions.Count);
                Assert.NotEmpty(viewModel.Peers.VisiblePeers);
                Assert.NotEmpty(viewModel.Content.Records);
                Assert.True(bindingErrors.Errors.Count == 0, string.Join(Environment.NewLine, new SortedSet<string>(bindingErrors.Errors)));
            }
            finally
            {
                Logger.Sink = previousSink;
                window.Close();
                viewModel.Dispose();
                await manager.DisposeAsync();
            }
        }

        /// <summary>Lets the UI settle, captures a frame and optionally saves it.</summary>
        /// <param name="window">The window.</param>
        /// <param name="name">The screenshot name.</param>
        private static void Capture(MainWindow window, string name)
        {
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var target = Environment.GetEnvironmentVariable(ScreenshotVariable);
            if (!string.IsNullOrEmpty(target))
            {
                Directory.CreateDirectory(target);
                frame!.Save(Path.Combine(target, name + ".png"), PngBitmapEncoderOptions.Default);
            }
        }

        /// <summary>Collects binding errors reported by Avalonia.</summary>
        private sealed class BindingErrorSink : ILogSink
        {
            /// <summary>Gets the collected binding errors.</summary>
            internal List<string> Errors { get; } = new List<string>();

            /// <summary>Accepts binding events at warning level and above.</summary>
            /// <param name="level">The level.</param>
            /// <param name="area">The area.</param>
            /// <returns>Whether the event is collected.</returns>
            public bool IsEnabled(LogEventLevel level, string area)
            {
                return level >= LogEventLevel.Warning && area == LogArea.Binding;
            }

            /// <summary>Collects an event.</summary>
            /// <param name="level">The level.</param>
            /// <param name="area">The area.</param>
            /// <param name="source">The source object.</param>
            /// <param name="messageTemplate">The message template.</param>
            public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            {
                Log(level, area, source, messageTemplate, Array.Empty<object?>());
            }

            /// <summary>Collects an event with values.</summary>
            /// <param name="level">The level.</param>
            /// <param name="area">The area.</param>
            /// <param name="source">The source object.</param>
            /// <param name="messageTemplate">The message template.</param>
            /// <param name="propertyValues">The template values.</param>
            public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            {
                if (IsEnabled(level, area))
                {
                    lock (Errors)
                    {
                        Errors.Add(messageTemplate + " | " + string.Join(", ", propertyValues) + " | " + source);
                    }
                }
            }
        }
    }
}
