using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>The main window: navigation, session selection, global status and periodic refresh of the visible page.</summary>
    public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        /// <summary>The refresh interval of the visible page.</summary>
        private static readonly TimeSpan s_tickInterval = TimeSpan.FromSeconds(1);

        /// <summary>The refresh timer.</summary>
        private readonly IDisposable _timer;
        /// <summary>Whether the selection is being synchronized from the manager.</summary>
        private bool _syncingActive;

        /// <summary>Creates the main view model and every page.</summary>
        /// <param name="manager">The hosted node sessions.</param>
        /// <param name="dispatcher">Marshals work onto the UI thread.</param>
        /// <param name="interaction">File pickers, clipboard and theme.</param>
        public MainWindowViewModel(NodeSessionManager manager, IUiDispatcher dispatcher, IUserInteraction interaction)
        {
            Manager = manager;
            Notifications = new NotificationCenter(dispatcher);
            Context = new ProberContext(manager, dispatcher, interaction, Notifications);
            Overview = new OverviewViewModel(Context, Sessions);
            foreach (var session in manager.Sessions)
            {
                Sessions.Add(CreateItem(session));
            }
            Peers = new PeersViewModel(Context);
            Content = new ContentViewModel(Context);
            Tools = new ToolsViewModel(Context);
            Logs = new LogsViewModel(Context);
            Settings = new SettingsViewModel(Context);
            Pages = new[]
            {
                new NavigationItem("概览", "当前节点的身份、可达性、健康指标与本地测试网络", "Icon.Overview", Overview),
                new NavigationItem("节点", "网络内的节点列表、手动添加节点与连接详情", "Icon.Nodes", Peers),
                new NavigationItem("内容", "发布、拉取与校验文件，解析键并查找提供者", "Icon.Content", Content),
                new NavigationItem("调试工具", "端点探测、路由查找、块读取、中继打洞、通信测试与传输统计", "Icon.Tools", Tools),
                new NavigationItem("日志", "协议库与探测器的实时诊断日志", "Icon.Logs", Logs),
                new NavigationItem("设置", "主节点网络参数、身份与外观", "Icon.Settings", Settings),
            };
            SelectedPage = Pages[0];
            ActiveSession = Find(manager.ActiveSession);
            manager.SessionsChanged += OnSessionsChanged;
            manager.ActiveSessionChanged += OnManagerActiveSessionChanged;
            manager.Vault.GenerationStarted += OnGenerationStarted;
            manager.Vault.GenerationFinished += OnGenerationFinished;
            _timer = dispatcher.StartTimer(s_tickInterval, Tick);
            Tick();
        }

        /// <summary>Gets the hosted node sessions.</summary>
        public NodeSessionManager Manager { get; }

        /// <summary>Gets the shared services.</summary>
        public ProberContext Context { get; }

        /// <summary>Gets the transient notifications.</summary>
        public NotificationCenter Notifications { get; }

        /// <summary>Gets the hosted sessions.</summary>
        public ObservableCollection<SessionItemViewModel> Sessions { get; } = new ObservableCollection<SessionItemViewModel>();

        /// <summary>Gets the navigation entries.</summary>
        public IReadOnlyList<NavigationItem> Pages { get; }

        /// <summary>Gets the overview page.</summary>
        public OverviewViewModel Overview { get; }

        /// <summary>Gets the peer page.</summary>
        public PeersViewModel Peers { get; }

        /// <summary>Gets the content page.</summary>
        public ContentViewModel Content { get; }

        /// <summary>Gets the debugging tools page.</summary>
        public ToolsViewModel Tools { get; }

        /// <summary>Gets the log page.</summary>
        public LogsViewModel Logs { get; }

        /// <summary>Gets the settings page.</summary>
        public SettingsViewModel Settings { get; }

        /// <summary>Gets or sets the visible page.</summary>
        [ObservableProperty]
        public partial NavigationItem SelectedPage { get; set; }

        /// <summary>Gets or sets the session operations act on.</summary>
        [ObservableProperty]
        public partial SessionItemViewModel? ActiveSession { get; set; }

        /// <summary>Gets or sets whether an identity is being generated.</summary>
        [ObservableProperty]
        public partial bool IsGeneratingIdentity { get; set; }

        /// <summary>Gets or sets the identity generation text.</summary>
        [ObservableProperty]
        public partial string IdentityGenerationText { get; set; } = string.Empty;

        /// <summary>Gets or sets the warning and error summary.</summary>
        [ObservableProperty]
        public partial string ProblemsText { get; set; } = string.Empty;

        /// <summary>Gets or sets whether errors were captured.</summary>
        [ObservableProperty]
        public partial bool HasErrors { get; set; }

        /// <summary>Gets or sets the status bar text.</summary>
        [ObservableProperty]
        public partial string StatusBarText { get; set; } = string.Empty;

        /// <summary>Stops the refresh timer and every subscription.</summary>
        public void Dispose()
        {
            _timer.Dispose();
            Manager.SessionsChanged -= OnSessionsChanged;
            Manager.ActiveSessionChanged -= OnManagerActiveSessionChanged;
            Manager.Vault.GenerationStarted -= OnGenerationStarted;
            Manager.Vault.GenerationFinished -= OnGenerationFinished;
            foreach (var item in Sessions)
            {
                item.Session.StateChanged -= OnSessionStateChanged;
            }
            Logs.Dispose();
            Notifications.Dispose();
        }

        /// <summary>Refreshes the sessions and the visible page.</summary>
        public void Tick()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in Sessions)
            {
                item.Refresh(now);
            }
            SelectedPage.Page.OnTick(now);
            var sink = Manager.Sink;
            ProblemsText = sink.WarningCount + " 警告 · " + sink.ErrorCount + " 错误";
            HasErrors = sink.ErrorCount != 0;
            var active = Manager.ActiveSession;
            StatusBarText = "MajDHT v1  ·  当前操作：" + active.Label + "（" + Display.SessionState(active.State) + "）  ·  已知节点 "
                + active.Peers.Count + "  ·  托管节点 " + Sessions.Count + "  ·  身份工作量 " + Manager.Settings.IdentityWorkBits + " 位";
        }

        /// <summary>Activates the newly visible page.</summary>
        /// <param name="value">The new page.</param>
        partial void OnSelectedPageChanged(NavigationItem value)
        {
            value.Page.OnActivated();
            value.Page.OnTick(DateTimeOffset.UtcNow);
        }

        /// <summary>Makes the selected session the active one.</summary>
        /// <param name="value">The selected session.</param>
        partial void OnActiveSessionChanged(SessionItemViewModel? value)
        {
            if (value != null && !_syncingActive)
            {
                Manager.ActiveSession = value.Session;
            }
        }

        /// <summary>Switches between the light and dark theme.</summary>
        [RelayCommand]
        private void ToggleTheme()
        {
            var dark = Context.Interaction.GetEffectiveTheme() != "Dark";
            Settings.ApplyThemeChoice(dark ? 2 : 1);
        }

        /// <summary>Shows the log page.</summary>
        [RelayCommand]
        private void OpenLogs()
        {
            SelectedPage = Pages[4];
        }

        /// <summary>Wraps a session and subscribes to its state.</summary>
        /// <param name="session">The session.</param>
        /// <returns>The wrapper.</returns>
        private SessionItemViewModel CreateItem(NodeSession session)
        {
            session.StateChanged += OnSessionStateChanged;
            return new SessionItemViewModel(session, Overview);
        }

        /// <summary>Finds the wrapper of a session.</summary>
        /// <param name="session">The session.</param>
        /// <returns>The wrapper, or null.</returns>
        private SessionItemViewModel? Find(NodeSession session)
        {
            foreach (var item in Sessions)
            {
                if (ReferenceEquals(item.Session, session))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>Refreshes promptly after a session changed state.</summary>
        /// <param name="sender">The session.</param>
        /// <param name="args">The ignored event data.</param>
        private void OnSessionStateChanged(object? sender, EventArgs args)
        {
            Context.Dispatcher.Post(Tick);
        }

        /// <summary>Synchronizes the session wrappers after sessions were added or removed.</summary>
        /// <param name="sender">The manager.</param>
        /// <param name="args">The ignored event data.</param>
        private void OnSessionsChanged(object? sender, EventArgs args)
        {
            Context.Dispatcher.Post(() =>
            {
                var current = Manager.Sessions;
                for (var index = Sessions.Count - 1; index >= 0; index--)
                {
                    var item = Sessions[index];
                    var kept = false;
                    foreach (var session in current)
                    {
                        kept |= ReferenceEquals(session, item.Session);
                    }
                    if (!kept)
                    {
                        item.Session.StateChanged -= OnSessionStateChanged;
                        Sessions.RemoveAt(index);
                    }
                }
                foreach (var session in current)
                {
                    if (Find(session) == null)
                    {
                        Sessions.Add(CreateItem(session));
                    }
                }
                Tools.RoundTrip.RefreshTargets();
                Tick();
            });
        }

        /// <summary>Propagates an active session change to every page.</summary>
        /// <param name="sender">The manager.</param>
        /// <param name="args">The ignored event data.</param>
        private void OnManagerActiveSessionChanged(object? sender, EventArgs args)
        {
            Context.Dispatcher.Post(() =>
            {
                _syncingActive = true;
                try
                {
                    ActiveSession = Find(Manager.ActiveSession);
                }
                finally
                {
                    _syncingActive = false;
                }
                foreach (var page in Pages)
                {
                    page.Page.OnActiveSessionChanged();
                }
                Tick();
            });
        }

        /// <summary>Shows the identity generation indicator.</summary>
        /// <param name="sender">The vault.</param>
        /// <param name="args">The generation details.</param>
        private void OnGenerationStarted(object? sender, IdentityGenerationEventArgs args)
        {
            Context.Dispatcher.Post(() =>
            {
                IsGeneratingIdentity = true;
                IdentityGenerationText = "正在生成身份 " + args.Name + "（" + args.WorkBits + " 位工作量）…";
            });
        }

        /// <summary>Hides the identity generation indicator.</summary>
        /// <param name="sender">The vault.</param>
        /// <param name="args">The generation details.</param>
        private void OnGenerationFinished(object? sender, IdentityGenerationEventArgs args)
        {
            Context.Dispatcher.Post(() =>
            {
                IsGeneratingIdentity = false;
                Notifications.Show("身份 " + args.Name + " 已生成，耗时 " + Display.Duration(args.Elapsed) + "。", StatusTone.Success);
            });
        }
    }
}
