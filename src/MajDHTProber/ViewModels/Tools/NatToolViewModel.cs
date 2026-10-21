using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;

#nullable enable

namespace MajDHTProber.ViewModels.Tools
{
    /// <summary>
    /// Registers with Traversal nodes, opens forwarding sessions with Forwarders and attempts TCP or UDP hole punching
    /// coordinated by a Traversal node.
    /// </summary>
    public sealed partial class NatToolViewModel : OperationViewModel
    {
        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public NatToolViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Gets the peers that coordinate traversal.</summary>
        public ObservableCollection<PeerChoiceViewModel> TraversalNodes { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets the peers that forward traffic.</summary>
        public ObservableCollection<PeerChoiceViewModel> Forwarders { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets every known peer as punching target.</summary>
        public ObservableCollection<PeerChoiceViewModel> Targets { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets the transport options.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "TCP 同时打开", "UDP 打洞" };

        /// <summary>Gets the result banner.</summary>
        public BannerViewModel Result { get; } = new BannerViewModel();

        /// <summary>Gets or sets the selected Traversal node.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedTraversal { get; set; }

        /// <summary>Gets or sets the selected Forwarder.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedForwarder { get; set; }

        /// <summary>Gets or sets the selected punching target.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedTarget { get; set; }

        /// <summary>Gets or sets the transport option index.</summary>
        [ObservableProperty]
        public partial int TransportIndex { get; set; } = 1;

        /// <summary>Gets or sets the NAT traversal status of the active node.</summary>
        [ObservableProperty]
        public partial string TraversalText { get; set; } = string.Empty;

        /// <summary>Reloads the Traversal node, Forwarder and target choices from the active session.</summary>
        [RelayCommand]
        public void RefreshChoices()
        {
            var traversal = SelectedTraversal?.Peer?.NodeId;
            var forwarder = SelectedForwarder?.Peer?.NodeId;
            var target = SelectedTarget?.Peer?.NodeId;
            Reload(TraversalNodes, PeerChoiceViewModel.FromSession(Session, DHTCapabilities.Traversal));
            Reload(Forwarders, PeerChoiceViewModel.FromSession(Session, DHTCapabilities.Forwarder));
            Reload(Targets, PeerChoiceViewModel.FromSession(Session));
            SelectedTraversal = Find(TraversalNodes, traversal) ?? (TraversalNodes.Count == 0 ? null : TraversalNodes[0]);
            SelectedForwarder = Find(Forwarders, forwarder) ?? (Forwarders.Count == 0 ? null : Forwarders[0]);
            SelectedTarget = Find(Targets, target) ?? (Targets.Count == 0 ? null : Targets[0]);
            var settings = Session.Settings;
            TraversalText = settings.EnableNatTraversal
                ? "本节点已启用 NAT 穿透；打洞需要双方都在同一 Traversal 节点登记，并需要本地对应协议的监听端口。"
                : "本节点未启用 NAT 穿透：无法登记 Traversal 节点或建立转发会话，也不会自动打洞（可在设置中开启）。";
        }

        /// <summary>Registers the active node with the selected Traversal node.</summary>
        /// <param name="cancellationToken">Cancels the registration.</param>
        /// <returns>Completion after the registration.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task RegisterTraversalAsync(CancellationToken cancellationToken)
        {
            var traversal = SelectedTraversal?.Peer;
            if (traversal == null)
            {
                Result.Show("没有可用的 Traversal 节点；它需声明 Traversal 能力。", StatusTone.Warning);
                return;
            }
            if (await RunAsync("登记 Traversal 节点", () => Session.RegisterTraversalAsync(traversal, cancellationToken)))
            {
                Result.Show("已在 " + Display.ShortId(traversal.NodeId, 8, 4) + " 登记，本节点会持续刷新登记并轮询引介。", StatusTone.Success);
            }
        }

        /// <summary>Opens a forwarding session between the active node and the selected Forwarder.</summary>
        /// <param name="cancellationToken">Cancels the session.</param>
        /// <returns>Completion after the session opened.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task RegisterForwarderAsync(CancellationToken cancellationToken)
        {
            var forwarder = SelectedForwarder?.Peer;
            if (forwarder == null)
            {
                Result.Show("没有可用的 Forwarder；它需声明 Forwarder 能力。", StatusTone.Warning);
                return;
            }
            if (await RunAsync("建立转发会话", () => Session.RegisterForwarderAsync(forwarder, cancellationToken)))
            {
                Result.Show("已与 " + Display.ShortId(forwarder.NodeId, 8, 4) + " 建立转发会话，其他节点可经它把流量投递给本节点。", StatusTone.Success);
            }
        }

        /// <summary>Attempts a direct connection to the selected target through the selected Traversal node.</summary>
        /// <param name="cancellationToken">Cancels the attempt.</param>
        /// <returns>Completion after the attempt.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task PunchAsync(CancellationToken cancellationToken)
        {
            var traversal = SelectedTraversal?.Peer;
            var target = SelectedTarget?.Peer;
            if (traversal == null || target == null)
            {
                Result.Show("请选择 Traversal 节点与目标节点。", StatusTone.Warning);
                return;
            }
            if (traversal.NodeId == target.NodeId)
            {
                Result.Show("Traversal 节点与目标不能是同一个节点。", StatusTone.Warning);
                return;
            }
            var transport = TransportIndex == 0 ? DHTTransport.Tcp : DHTTransport.Udp;
            var connected = false;
            if (await RunAsync("打洞", async () => connected = await Session.TryPunchAsync(target, traversal, transport, cancellationToken)))
            {
                Result.Show(connected
                        ? "已建立经认证的 " + Display.Transport(transport) + " 直连路由，后续请求将优先使用它。"
                        : "打洞超时：目标可能未在该 Traversal 节点登记，或 NAT / 防火墙阻止了直连。",
                    connected ? StatusTone.Success : StatusTone.Warning);
            }
        }

        /// <summary>Replaces the content of a choice list.</summary>
        /// <param name="choices">The list.</param>
        /// <param name="replacement">The new choices.</param>
        private static void Reload(ObservableCollection<PeerChoiceViewModel> choices, IEnumerable<PeerChoiceViewModel> replacement)
        {
            choices.Clear();
            foreach (var choice in replacement)
            {
                choices.Add(choice);
            }
        }

        /// <summary>Finds a choice by node identifier.</summary>
        /// <param name="choices">The choices.</param>
        /// <param name="nodeId">The node identifier, or null.</param>
        /// <returns>The choice, or null.</returns>
        private static PeerChoiceViewModel? Find(IEnumerable<PeerChoiceViewModel> choices, string? nodeId)
        {
            if (nodeId == null)
            {
                return null;
            }
            foreach (var choice in choices)
            {
                if (choice.Peer?.NodeId == nodeId)
                {
                    return choice;
                }
            }
            return null;
        }
    }
}
