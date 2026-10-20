using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;

#nullable enable

namespace MajDHTProber.ViewModels.Tools
{
    /// <summary>One peer row of the transmission statistics.</summary>
    public sealed class PeerStatRowViewModel : TonedViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="statistics">The peer statistics.</param>
        /// <param name="label">An optional peer label.</param>
        /// <param name="now">The current time.</param>
        public PeerStatRowViewModel(DHTPeerStatistics statistics, string? label, DateTimeOffset now)
        {
            Statistics = statistics;
            NodeId = statistics.NodeId;
            NodeText = (string.IsNullOrEmpty(label) ? string.Empty : label + " · ") + Display.ShortId(statistics.NodeId, 8, 4);
            SentText = statistics.SentPackets.ToString(CultureInfo.InvariantCulture);
            LostText = statistics.LostPackets.ToString(CultureInfo.InvariantCulture);
            LossText = Display.Percent(statistics.PacketLossRate);
            RoutesText = statistics.Routes.Count.ToString(CultureInfo.InvariantCulture);
            UpdatedText = Display.Relative(statistics.LastUpdated, now);
            Tone = statistics.PacketLossRate == 0 ? StatusTone.Success : statistics.PacketLossRate < 0.2 ? StatusTone.Warning : StatusTone.Danger;
        }

        /// <summary>Gets the statistics.</summary>
        public DHTPeerStatistics Statistics { get; }

        /// <summary>Gets the node identifier.</summary>
        public string NodeId { get; }

        /// <summary>Gets the node text.</summary>
        public string NodeText { get; }

        /// <summary>Gets the number of sent transmissions.</summary>
        public string SentText { get; }

        /// <summary>Gets the number of unanswered transmissions.</summary>
        public string LostText { get; }

        /// <summary>Gets the loss rate.</summary>
        public string LossText { get; }

        /// <summary>Gets the number of routes.</summary>
        public string RoutesText { get; }

        /// <summary>Gets when the statistics were last updated.</summary>
        public string UpdatedText { get; }
    }

    /// <summary>Shows the active node's per-peer and per-route packet loss statistics.</summary>
    public sealed partial class StatisticsToolViewModel : OperationViewModel
    {
        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public StatisticsToolViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Gets the peer rows, ordered by node identifier so rows keep their positions across refreshes.</summary>
        public ObservableCollection<PeerStatRowViewModel> Rows { get; } = new ObservableCollection<PeerStatRowViewModel>();

        /// <summary>Gets the routes of the selected peer.</summary>
        public ObservableCollection<RouteStatRowViewModel> Routes { get; } = new ObservableCollection<RouteStatRowViewModel>();

        /// <summary>Gets or sets the selected peer row.</summary>
        [ObservableProperty]
        public partial PeerStatRowViewModel? SelectedRow { get; set; }

        /// <summary>Gets or sets the totals.</summary>
        [ObservableProperty]
        public partial string TotalsText { get; set; } = Display.None;

        /// <summary>Reloads the statistics.</summary>
        /// <param name="now">The current time.</param>
        public void Refresh(DateTimeOffset now)
        {
            var keep = SelectedRow?.NodeId;
            var statistics = new List<DHTPeerStatistics>(Session.GetStatistics());
            statistics.Sort((left, right) => string.CompareOrdinal(left.NodeId, right.NodeId));
            long sent = 0;
            long lost = 0;
            PeerStatRowViewModel? selected = null;
            for (var index = 0; index < statistics.Count; index++)
            {
                var entry = statistics[index];
                var row = new PeerStatRowViewModel(entry, Session.Peers.Find(entry.NodeId)?.Label, now);
                // Replacing rows in place keeps the scroll position of the bound list.
                if (index < Rows.Count)
                {
                    Rows[index] = row;
                }
                else
                {
                    Rows.Add(row);
                }
                sent += entry.SentPackets;
                lost += entry.LostPackets;
                if (entry.NodeId == keep)
                {
                    selected = row;
                }
            }
            while (Rows.Count > statistics.Count)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }
            SelectedRow = selected;
            TotalsText = Rows.Count + " 个节点 · 发送 " + sent + " · 丢失 " + lost + " · 总丢包率 " + Display.Percent(sent == 0 ? 0 : (double)lost / sent);
        }

        /// <summary>Shows the routes of the selected peer.</summary>
        /// <param name="value">The selected row.</param>
        partial void OnSelectedRowChanged(PeerStatRowViewModel? value)
        {
            Routes.Clear();
            if (value == null)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            foreach (var route in value.Statistics.Routes)
            {
                Routes.Add(new RouteStatRowViewModel(route, now));
            }
        }
    }
}
