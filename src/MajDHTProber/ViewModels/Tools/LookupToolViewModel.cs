using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels.Tools
{
    /// <summary>One node returned by an iterative lookup.</summary>
    public sealed class LookupRowViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="rank">The one-based rank by distance.</param>
        /// <param name="peer">The returned node.</param>
        /// <param name="routingId">The lookup target.</param>
        /// <param name="localNodeId">The active node identifier.</param>
        public LookupRowViewModel(int rank, DHTPeer peer, string routingId, string? localNodeId)
        {
            RankText = rank.ToString(CultureInfo.InvariantCulture);
            NodeText = Display.ShortId(peer.NodeId) + (peer.NodeId == localNodeId ? "（本节点）" : string.Empty);
            PrefixText = RoutingMath.CommonPrefixBits(peer.NodeId, routingId).ToString(CultureInfo.InvariantCulture) + " 位";
            AddressText = Display.PrimaryAddress(peer);
            CapabilitiesText = Display.Capabilities(peer.Capabilities);
            ReachabilityText = Display.Reachability(peer.Reachability);
            NodeId = peer.NodeId;
        }

        /// <summary>Gets the node identifier.</summary>
        public string NodeId { get; }

        /// <summary>Gets the rank.</summary>
        public string RankText { get; }

        /// <summary>Gets the shortened identifier.</summary>
        public string NodeText { get; }

        /// <summary>Gets the shared prefix length with the target.</summary>
        public string PrefixText { get; }

        /// <summary>Gets the primary address.</summary>
        public string AddressText { get; }

        /// <summary>Gets the capabilities.</summary>
        public string CapabilitiesText { get; }

        /// <summary>Gets the reachability.</summary>
        public string ReachabilityText { get; }
    }

    /// <summary>Runs iterative XOR-distance lookups for node identifiers, keys or random targets.</summary>
    public sealed partial class LookupToolViewModel : OperationViewModel
    {
        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public LookupToolViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Gets the returned nodes, nearest first.</summary>
        public ObservableCollection<LookupRowViewModel> Rows { get; } = new ObservableCollection<LookupRowViewModel>();

        /// <summary>Gets the result banner.</summary>
        public BannerViewModel Result { get; } = new BannerViewModel();

        /// <summary>Gets or sets the target: a 64-digit identifier or a canonical key.</summary>
        [ObservableProperty]
        public partial string TargetText { get; set; } = string.Empty;

        /// <summary>Gets or sets the resolved routing identifier of the last lookup.</summary>
        [ObservableProperty]
        public partial string RoutingIdText { get; set; } = string.Empty;

        /// <summary>Fills the target with a random identifier.</summary>
        [RelayCommand]
        private void Randomize()
        {
            TargetText = RoutingMath.RandomId();
        }

        /// <summary>Fills the target with the active node's own identifier.</summary>
        [RelayCommand]
        private void UseSelf()
        {
            TargetText = Session.NodeId ?? string.Empty;
        }

        /// <summary>Runs the lookup.</summary>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task LookupAsync(CancellationToken cancellationToken)
        {
            Result.Hide();
            var target = TargetText.Trim();
            string routingId;
            if (RoutingMath.IsRoutingId(target.ToLowerInvariant()))
            {
                routingId = target.ToLowerInvariant();
            }
            else if (RecordInput.TryParseKey(target, out var key))
            {
                routingId = key.RoutingId;
            }
            else
            {
                Result.Show("目标应为 64 位十六进制标识符（节点 ID / 路由 ID）或“发布者ID:名称”格式的键。", StatusTone.Warning);
                return;
            }
            RoutingIdText = routingId;
            IReadOnlyList<DHTPeer>? peers = null;
            var stopwatch = Stopwatch.StartNew();
            if (!await RunAsync("路由查找", async () => peers = await Session.FindNodesAsync(routingId, cancellationToken)) || peers == null)
            {
                return;
            }
            stopwatch.Stop();
            Rows.Clear();
            var localNodeId = Session.NodeId;
            for (var index = 0; index < peers.Count; index++)
            {
                Rows.Add(new LookupRowViewModel(index + 1, peers[index], routingId, localNodeId));
            }
            Result.Show("查找完成：返回 " + peers.Count + " 个最近的可响应节点，耗时 " + Display.Duration(stopwatch.Elapsed) + "。",
                peers.Count == 0 ? StatusTone.Warning : StatusTone.Success);
        }
    }
}
