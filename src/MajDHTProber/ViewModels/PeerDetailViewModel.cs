using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>The detail panel of the selected peer, with the debugging actions that target it.</summary>
    public sealed partial class PeerDetailViewModel : OperationViewModel
    {
        /// <summary>Returns the peers offering relay assistance.</summary>
        private readonly Func<IReadOnlyList<PeerItemViewModel>> _relayCandidates;
        /// <summary>Called after the peer was forgotten.</summary>
        private readonly Action<string> _forgotten;
        /// <summary>Whether the identity work evaluation has started.</summary>
        private bool _workRequested;

        /// <summary>Creates the panel.</summary>
        /// <param name="context">The shared services.</param>
        /// <param name="item">The selected peer row.</param>
        /// <param name="relayCandidates">Returns the peers offering relay assistance.</param>
        /// <param name="forgotten">Called with the node identifier after the peer was forgotten.</param>
        public PeerDetailViewModel(ProberContext context, PeerItemViewModel item, Func<IReadOnlyList<PeerItemViewModel>> relayCandidates,
            Action<string> forgotten)
            : base(context)
        {
            Item = item;
            _relayCandidates = relayCandidates;
            _forgotten = forgotten;
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Gets the selected peer row.</summary>
        public PeerItemViewModel Item { get; }

        /// <summary>Gets the advertised endpoints.</summary>
        public ObservableCollection<EndpointRowViewModel> Endpoints { get; } = new ObservableCollection<EndpointRowViewModel>();

        /// <summary>Gets the embedded relay hints.</summary>
        public ObservableCollection<string> RelayHints { get; } = new ObservableCollection<string>();

        /// <summary>Gets the per-route statistics.</summary>
        public ObservableCollection<RouteStatRowViewModel> Routes { get; } = new ObservableCollection<RouteStatRowViewModel>();

        /// <summary>Gets the latest endpoint probe results.</summary>
        public ObservableCollection<ProbeRowViewModel> ProbeResults { get; } = new ObservableCollection<ProbeRowViewModel>();

        /// <summary>Gets the peers that can act as relay for hole punching.</summary>
        public ObservableCollection<PeerItemViewModel> RelayCandidates { get; } = new ObservableCollection<PeerItemViewModel>();

        /// <summary>Gets the transport names offered for punching.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "TCP", "UDP" };

        /// <summary>Gets or sets the selected relay for punching.</summary>
        [ObservableProperty]
        public partial PeerItemViewModel? SelectedRelay { get; set; }

        /// <summary>Gets or sets the selected punching transport: 0 for TCP, 1 for UDP.</summary>
        [ObservableProperty]
        public partial int PunchTransportIndex { get; set; }

        /// <summary>Gets or sets the explanation of the connection state.</summary>
        [ObservableProperty]
        public partial string StateDescription { get; set; } = string.Empty;

        /// <summary>Gets or sets the routing table membership text.</summary>
        [ObservableProperty]
        public partial string RoutingText { get; set; } = string.Empty;

        /// <summary>Gets or sets the external address text.</summary>
        [ObservableProperty]
        public partial string ExternalAddressText { get; set; } = string.Empty;

        /// <summary>Gets or sets the NAT mapping text.</summary>
        [ObservableProperty]
        public partial string MappingText { get; set; } = string.Empty;

        /// <summary>Gets or sets the identity work text.</summary>
        [ObservableProperty]
        public partial string IdentityWorkText { get; set; } = "计算中…";

        /// <summary>Gets or sets the contact sequence and lease text.</summary>
        [ObservableProperty]
        public partial string LeaseText { get; set; } = string.Empty;

        /// <summary>Gets or sets the first-seen text.</summary>
        [ObservableProperty]
        public partial string FirstSeenText { get; set; } = string.Empty;

        /// <summary>Gets or sets the last interaction text.</summary>
        [ObservableProperty]
        public partial string LastInteractionText { get; set; } = string.Empty;

        /// <summary>Gets or sets the last failure text.</summary>
        [ObservableProperty]
        public partial string LastFailureText { get; set; } = string.Empty;

        /// <summary>Gets or sets the verified address text.</summary>
        [ObservableProperty]
        public partial string VerifiedAddressText { get; set; } = string.Empty;

        /// <summary>Gets or sets the last probe summary.</summary>
        [ObservableProperty]
        public partial string LastProbeText { get; set; } = string.Empty;

        /// <summary>Gets or sets the statistics summary.</summary>
        [ObservableProperty]
        public partial string StatisticsText { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the peer advertises no endpoint.</summary>
        [ObservableProperty]
        public partial bool HasNoEndpoints { get; set; }

        /// <summary>Gets the result of the latest action.</summary>
        public BannerViewModel ActionResult { get; } = new BannerViewModel();

        /// <summary>Re-reads the snapshot and statistics.</summary>
        /// <param name="now">The current time.</param>
        public void Refresh(DateTimeOffset now)
        {
            var snapshot = Item.Snapshot;
            var contact = snapshot.Contact;
            StateDescription = Display.StateDescription(Item.State);
            RoutingText = snapshot.InRoutingTable ? "在本节点路由表中" : "不在本节点路由表中";
            ExternalAddressText = contact.ExternalAddress?.ToString() ?? "未确认";
            MappingText = contact.HasEndpointDependentMapping ? "端点相关映射（不打洞）" : "未报告";
            LeaseText = "序号 " + contact.Sequence.ToString(CultureInfo.InvariantCulture) + " · " + Display.Future(contact.ExpiresAt, now) + "到期";
            FirstSeenText = Display.Relative(snapshot.FirstSeen, now);
            LastInteractionText = Display.Relative(snapshot.LastInteractionAt, now);
            LastFailureText = Display.Relative(snapshot.RemovedAt, now);
            VerifiedAddressText = snapshot.VerifiedAddress?.ToString() ?? Display.None;
            LastProbeText = snapshot.LastProbe == null
                ? "尚未探测"
                : (snapshot.LastProbe.Succeeded ? "成功" : "失败") + " · " + Display.Relative(snapshot.LastProbe.At, now) + " · "
                  + Display.Duration(snapshot.LastProbe.RoundTrip) + " · " + snapshot.LastProbe.Summary;
            HasNoEndpoints = contact.Endpoints.Count == 0;
            if (Endpoints.Count != contact.Endpoints.Count)
            {
                Endpoints.Clear();
                foreach (var endpoint in contact.Endpoints)
                {
                    Endpoints.Add(new EndpointRowViewModel(endpoint));
                }
            }
            if (RelayHints.Count != contact.RelayHints.Count)
            {
                RelayHints.Clear();
                foreach (var hint in contact.RelayHints)
                {
                    RelayHints.Add(hint.NodeId);
                }
            }
            var statistics = Session.GetStatistics(Item.NodeId);
            StatisticsText = statistics == null
                ? "本节点尚未向其发送请求"
                : "发送 " + statistics.SentPackets + " · 丢失 " + statistics.LostPackets + " · 丢包率 " + Display.Percent(statistics.PacketLossRate);
            Routes.Clear();
            if (statistics != null)
            {
                foreach (var route in statistics.Routes)
                {
                    Routes.Add(new RouteStatRowViewModel(route, now));
                }
            }
            SyncRelayCandidates();
            if (!_workRequested)
            {
                _workRequested = true;
                _ = EvaluateIdentityWorkAsync(contact);
            }
        }

        /// <summary>Probes every advertised endpoint of the peer over both transports.</summary>
        /// <param name="cancellationToken">Cancels the probes.</param>
        /// <returns>Completion after the probes.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task ProbeAsync(CancellationToken cancellationToken)
        {
            await RunAsync("探测", async () =>
            {
                var results = await Session.ProbePeerAsync(Item.Snapshot, cancellationToken);
                ProbeResults.Clear();
                var success = 0;
                foreach (var result in results)
                {
                    ProbeResults.Add(new ProbeRowViewModel(result, Item.NodeId));
                    success += result.Succeeded && result.NodeId == Item.NodeId ? 1 : 0;
                }
                if (results.Count == 0)
                {
                    ActionResult.Show("该节点未通告任何端点（私有节点只能经中继访问），可改用“路由探测”。", StatusTone.Warning);
                }
                else
                {
                    ActionResult.Show("探测完成：" + success + " / " + results.Count + " 个端点由该节点应答。",
                        success == results.Count ? StatusTone.Success : success == 0 ? StatusTone.Danger : StatusTone.Warning);
                }
            });
        }

        /// <summary>Checks whether the active node reaches the peer through its own routes.</summary>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task CheckRouteAsync(CancellationToken cancellationToken)
        {
            await RunAsync("路由探测", async () =>
            {
                var result = await Session.CheckRouteAsync(Item.Contact, cancellationToken);
                ActionResult.Show(result.Reachable
                        ? "该节点响应了经本节点路由发出的查找请求，耗时 " + Display.Duration(result.Elapsed) + "。"
                        : "查找未得到该节点的响应（共找到 " + result.NodesFound + " 个节点），耗时 " + Display.Duration(result.Elapsed) + "。",
                    result.Reachable ? StatusTone.Success : StatusTone.Danger);
            });
            Refresh(DateTimeOffset.UtcNow);
        }

        /// <summary>Registers the active node with this peer as relay.</summary>
        /// <param name="cancellationToken">Cancels the registration.</param>
        /// <returns>Completion after the registration.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task RegisterRelayAsync(CancellationToken cancellationToken)
        {
            if (await RunAsync("注册中继", () => Session.RegisterRelayAsync(Item.Contact, cancellationToken)))
            {
                ActionResult.Show("已在该中继注册本节点的 TCP/UDP 映射。", StatusTone.Success);
            }
        }

        /// <summary>Attempts a direct connection to the peer through the selected relay.</summary>
        /// <param name="cancellationToken">Cancels the attempt.</param>
        /// <returns>Completion after the attempt.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task PunchAsync(CancellationToken cancellationToken)
        {
            var relay = SelectedRelay;
            if (relay == null)
            {
                ActionResult.Show("请先选择一个具备中继能力的节点。", StatusTone.Warning);
                return;
            }
            var transport = PunchTransportIndex == 1 ? DHTTransport.Udp : DHTTransport.Tcp;
            var connected = false;
            if (await RunAsync("打洞", async () => connected = await Session.TryPunchAsync(Item.Contact, relay.Contact, transport, cancellationToken)))
            {
                ActionResult.Show(connected
                        ? "已通过 " + relay.Title + " 建立 " + Display.Transport(transport) + " 直连。"
                        : "未能建立直连（双方都需先在该中继注册；对称 NAT 或防火墙也会阻止打洞）。",
                    connected ? StatusTone.Success : StatusTone.Warning);
            }
        }

        /// <summary>Copies the node identifier.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyNodeIdAsync()
        {
            return CopyAsync(Item.NodeId, "节点 ID");
        }

        /// <summary>Copies the first advertised endpoint in a form accepted by the add form.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyEndpointAsync()
        {
            var endpoints = Item.Contact.Endpoints;
            if (endpoints.Count == 0)
            {
                return Task.CompletedTask;
            }
            var first = endpoints[0];
            var port = first.TcpPort != 0 ? first.TcpPort : first.UdpPort;
            return CopyAsync(Display.IPEndPoint(new System.Net.IPEndPoint(first.Address, port)), "端点");
        }

        /// <summary>Removes the peer from the list.</summary>
        [RelayCommand]
        private void Forget()
        {
            Session.ForgetPeer(Item.NodeId, false);
            _forgotten(Item.NodeId);
        }

        /// <summary>Removes the peer from the list and the routing table.</summary>
        [RelayCommand]
        private void Evict()
        {
            Session.ForgetPeer(Item.NodeId, true);
            _forgotten(Item.NodeId);
            Notify("已从列表和路由表移除该节点；它再次与本节点交互时会重新出现。", StatusTone.Neutral);
        }

        /// <summary>Evaluates the peer's identity work off the UI thread.</summary>
        /// <param name="contact">The contact.</param>
        /// <returns>Completion after the evaluation.</returns>
        private async Task EvaluateIdentityWorkAsync(DHTPeer contact)
        {
            try
            {
                var work = await Task.Run(() => contact.IdentityWork);
                var required = Session.Settings.IdentityWorkBits;
                IdentityWorkText = work + " 位" + (work >= required ? "（满足 " + required + " 位要求）" : "（低于 " + required + " 位要求，或评估预算已用尽）");
            }
            catch (Exception exception)
            {
                IdentityWorkText = "无法评估：" + exception.GetType().Name;
            }
        }

        /// <summary>Updates the relay candidates without disturbing the selection.</summary>
        private void SyncRelayCandidates()
        {
            var desired = new List<PeerItemViewModel>();
            foreach (var candidate in _relayCandidates())
            {
                if (candidate.NodeId != Item.NodeId)
                {
                    desired.Add(candidate);
                }
            }
            CollectionSync.Sync(RelayCandidates, desired);
            if (SelectedRelay == null && RelayCandidates.Count != 0)
            {
                SelectedRelay = RelayCandidates[0];
            }
        }
    }
}
