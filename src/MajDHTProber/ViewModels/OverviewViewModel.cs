using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One endpoint of a node: a listener endpoint of the active node, or an endpoint observed for a remote node.</summary>
    public sealed class EndpointRowViewModel
    {
        /// <summary>The endpoint shown by the row.</summary>
        private readonly DHTEndpoint _endpoint;

        /// <summary>Creates a row.</summary>
        /// <param name="endpoint">The endpoint.</param>
        public EndpointRowViewModel(DHTEndpoint endpoint)
        {
            _endpoint = endpoint;
            AddressText = endpoint.Address.ToString();
            TcpText = endpoint.TcpPort == 0 ? Display.None : endpoint.TcpPort.ToString(CultureInfo.InvariantCulture);
            UdpText = endpoint.UdpPort == 0 ? Display.None : endpoint.UdpPort.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Gets the address.</summary>
        public string AddressText { get; }

        /// <summary>Gets the TCP port.</summary>
        public string TcpText { get; }

        /// <summary>Gets the UDP port.</summary>
        public string UdpText { get; }

        /// <summary>Checks whether the row shows an endpoint.</summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <returns>Whether the address and both ports are equal.</returns>
        public bool Matches(DHTEndpoint endpoint)
        {
            return _endpoint.Equals(endpoint);
        }
    }

    /// <summary>The overview page: the active node's identity, reachability, health metrics and the hosted local nodes.</summary>
    public sealed partial class OverviewViewModel : PageViewModel, ISessionActions
    {
        /// <summary>Creates the page.</summary>
        /// <param name="context">The shared services.</param>
        /// <param name="sessions">The hosted sessions shown in the local node list.</param>
        public OverviewViewModel(ProberContext context, ObservableCollection<SessionItemViewModel> sessions)
            : base(context)
        {
            Sessions = sessions;
            Active = new SessionItemViewModel(context.ActiveSession);
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Gets every hosted session.</summary>
        public ObservableCollection<SessionItemViewModel> Sessions { get; }

        /// <summary>Gets the listener endpoints of the active node, with wildcard listeners represented by their loopback address.</summary>
        public ObservableCollection<EndpointRowViewModel> Endpoints { get; } = new ObservableCollection<EndpointRowViewModel>();

        /// <summary>Gets or sets the status of the active session.</summary>
        [ObservableProperty]
        public partial SessionItemViewModel Active { get; set; }

        /// <summary>Gets or sets the full node identifier.</summary>
        [ObservableProperty]
        public partial string NodeIdText { get; set; } = Display.None;

        /// <summary>Gets or sets the advertised capabilities.</summary>
        [ObservableProperty]
        public partial string CapabilitiesText { get; set; } = Display.None;

        /// <summary>Gets or sets the reachability.</summary>
        [ObservableProperty]
        public partial string ReachabilityText { get; set; } = Display.None;

        /// <summary>Gets or sets the external addresses confirmed by independent observers.</summary>
        [ObservableProperty]
        public partial string ExternalAddressText { get; set; } = Display.None;

        /// <summary>Gets or sets the NAT mapping behavior.</summary>
        [ObservableProperty]
        public partial string MappingText { get; set; } = Display.None;

        /// <summary>Gets or sets the Traversal nodes and Forwarders hinted in the local contact.</summary>
        [ObservableProperty]
        public partial string HintsText { get; set; } = Display.None;

        /// <summary>Gets or sets the identity work of the node identity.</summary>
        [ObservableProperty]
        public partial string IdentityWorkText { get; set; } = Display.None;

        /// <summary>Gets or sets the contact sequence and lease.</summary>
        [ObservableProperty]
        public partial string ContactLeaseText { get; set; } = Display.None;

        /// <summary>Gets or sets the configured listeners.</summary>
        [ObservableProperty]
        public partial string ListenText { get; set; } = Display.None;

        /// <summary>Gets or sets the configured bootstrap endpoints.</summary>
        [ObservableProperty]
        public partial string BootstrapText { get; set; } = Display.None;

        /// <summary>Gets or sets whether the running node declared itself private, so other nodes reach it only through Forwarders.</summary>
        [ObservableProperty]
        public partial bool ShowPrivateWarning { get; set; }

        /// <summary>Gets or sets the number of known peers.</summary>
        [ObservableProperty]
        public partial int PeerTotal { get; set; }

        /// <summary>Gets or sets the number of connected peers.</summary>
        [ObservableProperty]
        public partial int ConnectedCount { get; set; }

        /// <summary>Gets or sets the number of reachable peers.</summary>
        [ObservableProperty]
        public partial int ReachableCount { get; set; }

        /// <summary>Gets or sets the number of disconnected or expired peers.</summary>
        [ObservableProperty]
        public partial int DisconnectedCount { get; set; }

        /// <summary>Gets or sets the routing table size.</summary>
        [ObservableProperty]
        public partial int RoutingTableSize { get; set; }

        /// <summary>Gets or sets the number of remembered records.</summary>
        [ObservableProperty]
        public partial int RecordCount { get; set; }

        /// <summary>Gets or sets the transmission summary.</summary>
        [ObservableProperty]
        public partial string TrafficText { get; set; } = Display.None;

        /// <summary>Gets or sets the aggregate packet loss.</summary>
        [ObservableProperty]
        public partial string LossText { get; set; } = "0%";

        /// <summary>Gets or sets the number of captured warnings and errors.</summary>
        [ObservableProperty]
        public partial string ProblemsText { get; set; } = "0 / 0";

        /// <summary>Gets or sets whether a new local node downloads content.</summary>
        [ObservableProperty]
        public partial bool NewNodeDownload { get; set; } = true;

        /// <summary>Gets or sets whether a new local node provides content.</summary>
        [ObservableProperty]
        public partial bool NewNodeProvide { get; set; } = true;

        /// <summary>Gets or sets whether a new local node coordinates traversal.</summary>
        [ObservableProperty]
        public partial bool NewNodeTraversal { get; set; } = true;

        /// <summary>Gets or sets whether a new local node forwards traffic.</summary>
        [ObservableProperty]
        public partial bool NewNodeForwarder { get; set; } = true;

        /// <summary>Re-reads everything after the active session changed.</summary>
        public override void OnActiveSessionChanged()
        {
            Active = new SessionItemViewModel(Session);
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Refreshes the page.</summary>
        /// <param name="now">The current time.</param>
        public override void OnTick(DateTimeOffset now)
        {
            Refresh(now);
        }

        /// <summary>Re-reads the active session.</summary>
        /// <param name="now">The current time.</param>
        public void Refresh(DateTimeOffset now)
        {
            var session = Session;
            Active.Refresh(now);
            var settings = session.Settings;
            NodeIdText = session.NodeId ?? "尚未生成（首次启动时生成）";
            ListenText = settings.ListenAddresses + "  ·  TCP " + (settings.TcpEnabled ? PortText(settings.TcpPort) : "关闭")
                + "  ·  UDP " + (settings.UdpEnabled ? PortText(settings.UdpPort) : "关闭") + "  ·  优先 " + Display.Transport(settings.PreferredTransport);
            BootstrapText = settings.BootstrapEndpoints.Count == 0 ? "未配置" : string.Join("，", settings.BootstrapEndpoints);
            var local = session.TryGetLocalPeer();
            if (local != null)
            {
                var external = session.GetExternalAddresses();
                CapabilitiesText = Display.Capabilities(local.Capabilities);
                ReachabilityText = Display.Reachability(local.Reachability);
                ExternalAddressText = external.Count == 0 ? "未确认（需要至少两个不同网段的节点报告）" : string.Join("，", external);
                MappingText = local.HasEndpointDependentMapping ? "端点相关映射（难以打洞）" : "未观察到端点相关映射";
                HintsText = local.Hints.Count == 0 ? "无" : string.Join("；", local.Hints.Select(Display.Hint));
                IdentityWorkText = local.IdentityWork + " 位（网络要求 " + settings.IdentityWorkBits + " 位）";
                ContactLeaseText = "序号 " + local.Sequence.ToString(CultureInfo.InvariantCulture) + " · " + Display.Future(local.ExpiresAt, now) + "到期";
                SyncEndpoints(local);
                ShowPrivateWarning = local.Reachability == DHTReachability.Private;
            }
            else
            {
                CapabilitiesText = Display.Capabilities(settings.Capabilities) + "（未运行）";
                ReachabilityText = Display.None;
                ExternalAddressText = Display.None;
                MappingText = Display.None;
                HintsText = Display.None;
                IdentityWorkText = "网络要求 " + settings.IdentityWorkBits + " 位";
                ContactLeaseText = Display.None;
                Endpoints.Clear();
                ShowPrivateWarning = false;
            }
            var connected = 0;
            var reachable = 0;
            var disconnected = 0;
            var peers = session.Peers.Snapshot();
            foreach (var peer in peers)
            {
                switch (peer.GetState(now))
                {
                    case PeerConnectionState.Connected:
                        connected++;
                        break;
                    case PeerConnectionState.Reachable:
                        reachable++;
                        break;
                    case PeerConnectionState.Disconnected:
                    case PeerConnectionState.Expired:
                        disconnected++;
                        break;
                }
            }
            PeerTotal = peers.Count;
            ConnectedCount = connected;
            ReachableCount = reachable;
            DisconnectedCount = disconnected;
            RoutingTableSize = session.RoutingTable?.Snapshot().Count ?? 0;
            RecordCount = session.Records.Count;
            long sent = 0;
            long lost = 0;
            foreach (var statistics in session.GetStatistics())
            {
                sent += statistics.SentPackets;
                lost += statistics.LostPackets;
            }
            TrafficText = "发送 " + sent.ToString(CultureInfo.InvariantCulture) + " · 丢失 " + lost.ToString(CultureInfo.InvariantCulture);
            LossText = Display.Percent(sent == 0 ? 0 : (double)lost / sent);
            ProblemsText = Context.Manager.Sink.WarningCount + " / " + Context.Manager.Sink.ErrorCount;
            foreach (var item in Sessions)
            {
                item.Refresh(now);
            }
        }

        /// <summary>Starts the active node.</summary>
        /// <returns>Completion after the start.</returns>
        [RelayCommand]
        private async Task StartAsync()
        {
            if (await RunAsync("启动节点", () => Session.StartAsync()))
            {
                Notify(Session.Label + "已启动。", StatusTone.Success);
            }
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Stops the active node.</summary>
        /// <returns>Completion after the stop.</returns>
        [RelayCommand]
        private async Task StopAsync()
        {
            if (await RunAsync("停止节点", () => Session.StopAsync()))
            {
                Notify(Session.Label + "已停止。", StatusTone.Neutral);
            }
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Restarts the active node with its current settings.</summary>
        /// <returns>Completion after the restart.</returns>
        [RelayCommand]
        private async Task RestartAsync()
        {
            if (await RunAsync("重启节点", async () =>
            {
                await Session.StopAsync();
                await Session.StartAsync();
            }))
            {
                Notify(Session.Label + "已重启。", StatusTone.Success);
            }
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Contacts the configured bootstrap endpoints.</summary>
        /// <returns>Completion after bootstrapping.</returns>
        [RelayCommand]
        private async Task BootstrapAsync()
        {
            var joined = 0;
            if (await RunAsync("引导", async () => joined = await Session.BootstrapAsync()))
            {
                Notify("引导完成：" + joined + " 个引导端点响应。", StatusTone.Success);
            }
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Refreshes the neighborhood with a lookup of the node's own identifier.</summary>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand]
        private async Task RefreshNeighborsAsync()
        {
            var found = 0;
            if (await RunAsync("刷新邻居", async () => found = (await Session.RefreshNeighborsAsync()).Count))
            {
                Notify("邻居查找完成：找到 " + found + " 个可响应节点。", StatusTone.Success);
            }
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Copies the node identifier.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyNodeIdAsync()
        {
            return CopyAsync(Session.NodeId, "节点 ID");
        }

        /// <summary>Copies the endpoints at which other nodes can contact the active node.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyEndpointAsync()
        {
            var endpoints = Session.GetContactEndpoints();
            if (endpoints.Count == 0)
            {
                Notify("当前节点没有可供他人连接的端点。", StatusTone.Warning);
                return Task.CompletedTask;
            }
            return CopyAsync(string.Join(Environment.NewLine, endpoints.Select(Display.IPEndPoint)), "节点端点");
        }

        /// <summary>Creates and starts a loopback node linked with the running nodes.</summary>
        /// <returns>Completion after the node started.</returns>
        [RelayCommand]
        private async Task AddLocalNodeAsync()
        {
            var capabilities = (DHTCapabilities)0;
            if (NewNodeDownload)
            {
                capabilities |= DHTCapabilities.Download;
            }
            if (NewNodeProvide)
            {
                capabilities |= DHTCapabilities.Provide;
            }
            if (NewNodeTraversal)
            {
                capabilities |= DHTCapabilities.Traversal;
            }
            if (NewNodeForwarder)
            {
                capabilities |= DHTCapabilities.Forwarder;
            }
            string? label = null;
            if (await RunAsync("创建本地节点", async () =>
            {
                var session = await Context.Manager.AddLocalNodeAsync(new LocalNodeRequest { Capabilities = capabilities });
                label = session.Label;
            }))
            {
                Notify(label + "已创建并接入本地网络。", StatusTone.Success);
            }
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Starts a hosted session.</summary>
        /// <param name="item">The session.</param>
        /// <returns>Completion after the start.</returns>
        async Task ISessionActions.StartAsync(SessionItemViewModel item)
        {
            await RunAsync("启动" + item.Label, () => item.Session.StartAsync());
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Stops a hosted session.</summary>
        /// <param name="item">The session.</param>
        /// <returns>Completion after the stop.</returns>
        async Task ISessionActions.StopAsync(SessionItemViewModel item)
        {
            await RunAsync("停止" + item.Label, () => item.Session.StopAsync());
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Stops and removes a loopback node; the primary node cannot be removed.</summary>
        /// <param name="item">The session.</param>
        /// <returns>Completion after removal.</returns>
        async Task ISessionActions.RemoveAsync(SessionItemViewModel item)
        {
            if (!item.IsPrimary)
            {
                await RunAsync("移除" + item.Label, () => Context.Manager.RemoveLocalNodeAsync(item.Session));
            }
        }

        /// <summary>Makes a session the one operations act on.</summary>
        /// <param name="item">The session.</param>
        void ISessionActions.Activate(SessionItemViewModel item)
        {
            Context.Manager.ActiveSession = item.Session;
            Notify("当前操作节点：" + item.Label + "。", StatusTone.Info);
        }

        /// <summary>Formats a configured port.</summary>
        /// <param name="port">The port, zero for assigned.</param>
        /// <returns>The port text.</returns>
        private static string PortText(int port)
        {
            return port == 0 ? "自动分配" : port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Replaces the endpoint rows when the listener endpoints changed.</summary>
        /// <param name="local">The local contact at its listener endpoints.</param>
        private void SyncEndpoints(DHTPeer local)
        {
            var changed = Endpoints.Count != local.Endpoints.Count;
            for (var index = 0; !changed && index < local.Endpoints.Count; index++)
            {
                changed = !Endpoints[index].Matches(local.Endpoints[index]);
            }
            if (!changed)
            {
                return;
            }
            Endpoints.Clear();
            foreach (var endpoint in local.Endpoints)
            {
                Endpoints.Add(new EndpointRowViewModel(endpoint));
            }
        }
    }
}
