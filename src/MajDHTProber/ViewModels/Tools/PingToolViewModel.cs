using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels.Tools
{
    /// <summary>
    /// Repeatedly probes one endpoint like a ping utility: each round performs a signed Hello and one encrypted request from
    /// a short-lived probe client, then summarizes loss and round-trip times.
    /// </summary>
    public sealed partial class PingToolViewModel : OperationViewModel
    {
        /// <summary>The successful Hello round trips of the current series.</summary>
        private readonly List<TimeSpan> _roundTrips = new List<TimeSpan>();
        /// <summary>The distinct nodes that answered in the current series.</summary>
        private readonly HashSet<string> _nodes = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The last endpoint that answered.</summary>
        private System.Net.IPEndPoint? _lastAnswered;

        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public PingToolViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Gets the transport options.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "TCP", "UDP", "TCP / UDP 交替" };

        /// <summary>Gets the probe rows, newest last.</summary>
        public ObservableCollection<ProbeRowViewModel> Rows { get; } = new ObservableCollection<ProbeRowViewModel>();

        /// <summary>Gets the result banner.</summary>
        public BannerViewModel Result { get; } = new BannerViewModel();

        /// <summary>Gets or sets the endpoint text.</summary>
        [ObservableProperty]
        public partial string EndpointText { get; set; } = string.Empty;

        /// <summary>Gets or sets the transport option index.</summary>
        [ObservableProperty]
        public partial int TransportIndex { get; set; }

        /// <summary>Gets or sets the number of rounds.</summary>
        [ObservableProperty]
        public partial decimal? Count { get; set; } = 4;

        /// <summary>Gets or sets the pause between rounds in milliseconds.</summary>
        [ObservableProperty]
        public partial decimal? IntervalMilliseconds { get; set; } = 500;

        /// <summary>Gets or sets the timeout of each transport attempt in seconds.</summary>
        [ObservableProperty]
        public partial decimal? TimeoutSeconds { get; set; } = 3;

        /// <summary>Gets or sets the number of sent rounds.</summary>
        [ObservableProperty]
        public partial int Sent { get; set; }

        /// <summary>Gets or sets the number of answered rounds.</summary>
        [ObservableProperty]
        public partial int Answered { get; set; }

        /// <summary>Gets or sets the loss text.</summary>
        [ObservableProperty]
        public partial string LossText { get; set; } = Display.None;

        /// <summary>Gets or sets the minimum, average and maximum round trip.</summary>
        [ObservableProperty]
        public partial string RoundTripText { get; set; } = Display.None;

        /// <summary>Gets or sets the answering nodes.</summary>
        [ObservableProperty]
        public partial string NodesText { get; set; } = Display.None;

        /// <summary>Fills the endpoint with one of the active node's own contact endpoints.</summary>
        [RelayCommand]
        private void UseActiveEndpoint()
        {
            var endpoints = Session.GetContactEndpoints(TransportIndex == 1 ? DHTTransport.Udp : DHTTransport.Tcp);
            if (endpoints.Count == 0)
            {
                Notify("当前节点未运行或没有可连接的端点。", StatusTone.Warning);
                return;
            }
            EndpointText = Display.IPEndPoint(endpoints[0]);
        }

        /// <summary>Runs the probe series.</summary>
        /// <param name="cancellationToken">Stops the series.</param>
        /// <returns>Completion after the series.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task StartAsync(CancellationToken cancellationToken)
        {
            Result.Hide();
            Rows.Clear();
            _roundTrips.Clear();
            _nodes.Clear();
            _lastAnswered = null;
            Sent = 0;
            Answered = 0;
            UpdateSummary();
            var rounds = (int)Math.Clamp(Count ?? 4, 1, 100);
            var interval = TimeSpan.FromMilliseconds((double)Math.Clamp(IntervalMilliseconds ?? 500, 0, 60000));
            var settings = Session.Settings;
            var options = ProbeOptions.From(settings) with { RequestTimeout = TimeSpan.FromSeconds((double)Math.Clamp(TimeoutSeconds ?? 3, 0.1m, 60)) };
            await RunAsync("端点探测", async () =>
            {
                var endpoint = await EndpointParser.ResolveAsync(EndpointText, cancellationToken);
                for (var round = 1; round <= rounds; round++)
                {
                    var transport = TransportIndex switch
                    {
                        1 => DHTTransport.Udp,
                        2 => round % 2 == 1 ? DHTTransport.Tcp : DHTTransport.Udp,
                        _ => DHTTransport.Tcp,
                    };
                    var result = await Session.ProbeEndpointAsync(endpoint, transport, options, cancellationToken);
                    Record(result, round);
                    if (round < rounds)
                    {
                        await Task.Delay(interval, cancellationToken);
                    }
                }
            });
            if (Sent != 0)
            {
                Result.Show("完成 " + Sent + " 次探测，" + Answered + " 次应答，丢失 " + LossText + "。",
                    Answered == Sent ? StatusTone.Success : Answered == 0 ? StatusTone.Danger : StatusTone.Warning);
            }
        }

        /// <summary>Adds the endpoint that answered to the active node's peers.</summary>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>Completion after the attempt.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task AddToPeersAsync(CancellationToken cancellationToken)
        {
            var endpoint = _lastAnswered;
            if (endpoint == null)
            {
                Notify("请先成功探测一个端点。", StatusTone.Warning);
                return;
            }
            AddPeerResult? result = null;
            if (await RunAsync("添加节点", async () => result = await Session.AddPeerAsync(endpoint, new AddPeerOptions(), cancellationToken)) && result != null)
            {
                Notify(result.Added ? "已添加到节点列表。" : result.Note ?? "未能添加。", result.Added ? StatusTone.Success : StatusTone.Warning);
            }
        }

        /// <summary>Clears the results.</summary>
        [RelayCommand]
        private void Clear()
        {
            Rows.Clear();
            Result.Hide();
            _roundTrips.Clear();
            _nodes.Clear();
            Sent = 0;
            Answered = 0;
            UpdateSummary();
        }

        /// <summary>Records one round.</summary>
        /// <param name="result">The probe result.</param>
        /// <param name="round">The round number.</param>
        private void Record(EndpointProbeResult result, int round)
        {
            Rows.Add(new ProbeRowViewModel(result, null, round));
            Sent++;
            if (result.Succeeded)
            {
                Answered++;
                _lastAnswered = result.Endpoint;
                if (result.HelloRoundTrip.HasValue)
                {
                    _roundTrips.Add(result.HelloRoundTrip.Value);
                }
                if (result.NodeId != null)
                {
                    _nodes.Add(result.NodeId);
                }
            }
            UpdateSummary();
        }

        /// <summary>Recomputes the summary.</summary>
        private void UpdateSummary()
        {
            LossText = Sent == 0 ? Display.None : Display.Percent((double)(Sent - Answered) / Sent);
            if (_roundTrips.Count == 0)
            {
                RoundTripText = Display.None;
            }
            else
            {
                var minimum = TimeSpan.MaxValue;
                var maximum = TimeSpan.Zero;
                var total = TimeSpan.Zero;
                foreach (var roundTrip in _roundTrips)
                {
                    minimum = roundTrip < minimum ? roundTrip : minimum;
                    maximum = roundTrip > maximum ? roundTrip : maximum;
                    total += roundTrip;
                }
                RoundTripText = Display.Duration(minimum) + " / " + Display.Duration(total / _roundTrips.Count) + " / " + Display.Duration(maximum);
            }
            NodesText = _nodes.Count == 0 ? Display.None : string.Join("，", MapShort(_nodes));
        }

        /// <summary>Shortens node identifiers.</summary>
        /// <param name="nodes">The identifiers.</param>
        /// <returns>The shortened identifiers.</returns>
        private static IEnumerable<string> MapShort(IEnumerable<string> nodes)
        {
            foreach (var node in nodes)
            {
                yield return Display.ShortId(node, 8, 4);
            }
        }
    }
}
