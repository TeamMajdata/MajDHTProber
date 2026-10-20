using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MajDHTProber.Infrastructure;
using MajDHTProber.Logging;
using MajDHTProber.Services;
using MajDHTProber.ViewModels;
using MajDHTProber.Views;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber
{
    /// <summary>The Avalonia application: composes the services and opens the main window.</summary>
    public partial class App : Application
    {
        /// <summary>How long shutdown waits for the hosted nodes to drain.</summary>
        private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromSeconds(8);

        /// <summary>The hosted node sessions, created once the framework is ready.</summary>
        private NodeSessionManager? _manager;
        /// <summary>The identity vault.</summary>
        private IdentityVault? _vault;
        /// <summary>The main view model.</summary>
        private MainWindowViewModel? _viewModel;

        /// <summary>Loads the application XAML.</summary>
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>Composes the services and creates the main window once the framework is ready.</summary>
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var paths = AppPaths.CreateDefault();
                var sink = new LogSink();
                var logger = sink.CreateLogger("探测器");
                _vault = new IdentityVault(paths.IdentityDirectory, sink.CreateLogger("身份"));
                _manager = new NodeSessionManager(new SettingsStore(paths.SettingsFile, sink.CreateLogger("设置")), _vault, sink,
                    new RoutingSnapshotStore(paths.RoutingDirectory, sink.CreateLogger("路由表")));
                var window = new MainWindow();
                var interaction = new AvaloniaUserInteraction(() => window);
                interaction.ApplyTheme(_manager.Settings.Theme);
                _viewModel = new MainWindowViewModel(_manager, new AvaloniaUiDispatcher(), interaction);
                window.DataContext = _viewModel;
                desktop.MainWindow = window;
                desktop.Exit += OnExit;
                logger.LogInformation("MajDHT Prober 已启动，数据目录：{Directory}。", paths.Root);
                _ = _manager.AutoStartAsync();
            }
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Stops every hosted node before the process exits.</summary>
        /// <param name="sender">The lifetime.</param>
        /// <param name="args">The exit event data.</param>
        private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
        {
            _viewModel?.Dispose();
            if (_manager != null)
            {
                // Node shutdown uses ConfigureAwait(false) throughout, so waiting here cannot deadlock the UI thread.
                Task.Run(async () => await _manager.DisposeAsync()).Wait(s_shutdownTimeout);
            }
            _vault?.Dispose();
        }
    }
}
