using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>Performs the lifecycle actions offered on session rows.</summary>
    public interface ISessionActions
    {
        /// <summary>Starts a session.</summary>
        /// <param name="item">The session row.</param>
        /// <returns>Completion after the start.</returns>
        Task StartAsync(SessionItemViewModel item);

        /// <summary>Stops a session.</summary>
        /// <param name="item">The session row.</param>
        /// <returns>Completion after the stop.</returns>
        Task StopAsync(SessionItemViewModel item);

        /// <summary>Stops and removes a loopback session.</summary>
        /// <param name="item">The session row.</param>
        /// <returns>Completion after removal.</returns>
        Task RemoveAsync(SessionItemViewModel item);

        /// <summary>Makes a session the one operations act on.</summary>
        /// <param name="item">The session row.</param>
        void Activate(SessionItemViewModel item);
    }

    /// <summary>Presents one hosted node session in the session selector and the local node list.</summary>
    public sealed partial class SessionItemViewModel : TonedViewModel
    {
        /// <summary>Wraps a session.</summary>
        /// <param name="session">The session.</param>
        /// <param name="actions">Performs the row actions, or null for a read-only status row.</param>
        public SessionItemViewModel(NodeSession session, ISessionActions? actions = null)
        {
            Session = session;
            StartCommand = new AsyncRelayCommand(() => actions?.StartAsync(this) ?? Task.CompletedTask);
            StopCommand = new AsyncRelayCommand(() => actions?.StopAsync(this) ?? Task.CompletedTask);
            RemoveCommand = new AsyncRelayCommand(() => actions?.RemoveAsync(this) ?? Task.CompletedTask);
            ActivateCommand = new RelayCommand(() => actions?.Activate(this));
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Gets the wrapped session.</summary>
        public NodeSession Session { get; }

        /// <summary>Gets the session name.</summary>
        public string Label => Session.Label;

        /// <summary>Gets whether this is the primary session.</summary>
        public bool IsPrimary => Session.Role == NodeSessionRole.Primary;

        /// <summary>Gets the command that starts the session.</summary>
        public IAsyncRelayCommand StartCommand { get; }

        /// <summary>Gets the command that stops the session.</summary>
        public IAsyncRelayCommand StopCommand { get; }

        /// <summary>Gets the command that removes a loopback session.</summary>
        public IAsyncRelayCommand RemoveCommand { get; }

        /// <summary>Gets the command that makes the session the active one.</summary>
        public IRelayCommand ActivateCommand { get; }

        /// <summary>Gets or sets the lifecycle state text.</summary>
        [ObservableProperty]
        public partial string StateText { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the node is running.</summary>
        [ObservableProperty]
        public partial bool IsRunning { get; set; }

        /// <summary>Gets or sets whether the node is stopped or faulted and can be started.</summary>
        [ObservableProperty]
        public partial bool CanStart { get; set; }

        /// <summary>Gets or sets the shortened node identifier.</summary>
        [ObservableProperty]
        public partial string ShortNodeId { get; set; } = Display.None;

        /// <summary>Gets or sets the endpoint text of the running node.</summary>
        [ObservableProperty]
        public partial string EndpointText { get; set; } = Display.None;

        /// <summary>Gets or sets the number of known peers.</summary>
        [ObservableProperty]
        public partial int PeerCount { get; set; }

        /// <summary>Gets or sets the uptime or failure text.</summary>
        [ObservableProperty]
        public partial string DetailText { get; set; } = string.Empty;

        /// <summary>Re-reads the session state.</summary>
        /// <param name="now">The current time.</param>
        public void Refresh(DateTimeOffset now)
        {
            var state = Session.State;
            StateText = Display.SessionState(state);
            IsRunning = state == NodeSessionState.Running;
            CanStart = state == NodeSessionState.Stopped || state == NodeSessionState.Faulted;
            Tone = state switch
            {
                NodeSessionState.Running => StatusTone.Success,
                NodeSessionState.Starting or NodeSessionState.Stopping => StatusTone.Info,
                NodeSessionState.Faulted => StatusTone.Danger,
                _ => StatusTone.Neutral,
            };
            ShortNodeId = Display.ShortId(Session.NodeId, 8, 4);
            var endpoints = Session.GetContactEndpoints();
            EndpointText = endpoints.Count == 0 ? Display.None : Display.IPEndPoint(endpoints[0]);
            PeerCount = Session.Peers.Count;
            var started = Session.StartedAt;
            DetailText = state == NodeSessionState.Faulted
                ? Session.LastError ?? "启动失败"
                : started.HasValue ? "已运行 " + Display.Duration(now - started.Value) : StateText;
        }
    }
}
