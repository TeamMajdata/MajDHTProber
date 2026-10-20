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
    /// <summary>Registers with relays and attempts relay-coordinated TCP or UDP hole punching.</summary>
    public sealed partial class NatToolViewModel : OperationViewModel
    {
        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public NatToolViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Gets the peers offering relay assistance.</summary>
        public ObservableCollection<PeerChoiceViewModel> Relays { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets every known peer as punching target.</summary>
        public ObservableCollection<PeerChoiceViewModel> Targets { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets the transport options.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "TCP 同时打开", "UDP 打洞" };

        /// <summary>Gets the result banner.</summary>
        public BannerViewModel Result { get; } = new BannerViewModel();

        /// <summary>Gets or sets the selected relay.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedRelay { get; set; }

        /// <summary>Gets or sets the selected punching target.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedTarget { get; set; }

        /// <summary>Gets or sets the transport option index.</summary>
        [ObservableProperty]
        public partial int TransportIndex { get; set; } = 1;

        /// <summary>Gets or sets the NAT traversal status of the active node.</summary>
        [ObservableProperty]
        public partial string TraversalText { get; set; } = string.Empty;

        /// <summary>Reloads the relay and target choices from the active session.</summary>
        [RelayCommand]
        public void RefreshChoices()
        {
            var relay = SelectedRelay?.Contact?.NodeId;
            var target = SelectedTarget?.Contact?.NodeId;
            Relays.Clear();
            foreach (var choice in PeerChoiceViewModel.FromSession(Session, true))
            {
                Relays.Add(choice);
            }
            Targets.Clear();
            foreach (var choice in PeerChoiceViewModel.FromSession(Session, false))
            {
                Targets.Add(choice);
            }
            SelectedRelay = Find(Relays, relay) ?? (Relays.Count == 0 ? null : Relays[0]);
            SelectedTarget = Find(Targets, target) ?? (Targets.Count == 0 ? null : Targets[0]);
            var settings = Session.Settings;
            TraversalText = settings.EnableNatTraversal
                ? "本节点已启用 NAT 穿透；打洞需要双方都在同一中继注册，并需要本地对应协议的监听端口。"
                : "本节点未启用 NAT 穿透：无法注册中继，也不会自动打洞（可在设置中开启）。";
        }

        /// <summary>Registers the active node with the selected relay.</summary>
        /// <param name="cancellationToken">Cancels the registration.</param>
        /// <returns>Completion after the registration.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task RegisterAsync(CancellationToken cancellationToken)
        {
            var relay = SelectedRelay?.Contact;
            if (relay == null)
            {
                Result.Show("没有可用的中继节点；中继需声明“中继”能力。", StatusTone.Warning);
                return;
            }
            if (await RunAsync("注册中继", () => Session.RegisterRelayAsync(relay, cancellationToken)))
            {
                Result.Show("已在 " + Display.ShortId(relay.NodeId, 8, 4) + " 注册，本节点会持续刷新映射并轮询引介。", StatusTone.Success);
            }
        }

        /// <summary>Attempts a direct connection to the selected target through the selected relay.</summary>
        /// <param name="cancellationToken">Cancels the attempt.</param>
        /// <returns>Completion after the attempt.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task PunchAsync(CancellationToken cancellationToken)
        {
            var relay = SelectedRelay?.Contact;
            var target = SelectedTarget?.Contact;
            if (relay == null || target == null)
            {
                Result.Show("请选择中继与目标节点。", StatusTone.Warning);
                return;
            }
            if (relay.NodeId == target.NodeId)
            {
                Result.Show("中继与目标不能是同一个节点。", StatusTone.Warning);
                return;
            }
            var transport = TransportIndex == 0 ? DHTTransport.Tcp : DHTTransport.Udp;
            var connected = false;
            if (await RunAsync("打洞", async () => connected = await Session.TryPunchAsync(target, relay, transport, cancellationToken)))
            {
                Result.Show(connected
                        ? "已建立经认证的 " + Display.Transport(transport) + " 直连路由，后续请求将优先使用它。"
                        : "打洞超时：目标可能未在该中继注册，或 NAT / 防火墙阻止了直连。",
                    connected ? StatusTone.Success : StatusTone.Warning);
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
                if (choice.Contact?.NodeId == nodeId)
                {
                    return choice;
                }
            }
            return null;
        }
    }
}
