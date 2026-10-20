using System.Globalization;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One endpoint probe result row.</summary>
    public sealed class ProbeRowViewModel : TonedViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="result">The probe result.</param>
        /// <param name="expectedNodeId">The node expected to answer, or null when any node is acceptable.</param>
        /// <param name="sequence">The row number shown to the user, or zero to omit it.</param>
        public ProbeRowViewModel(EndpointProbeResult result, string? expectedNodeId, int sequence = 0)
        {
            Result = result;
            SequenceText = sequence == 0 ? string.Empty : "#" + sequence.ToString(CultureInfo.InvariantCulture);
            TimeText = Display.Timestamp(result.StartedAt).Substring(11);
            EndpointText = Display.IPEndPoint(result.Endpoint);
            TransportText = Display.Transport(result.RequestedTransport);
            HelloText = Display.Duration(result.HelloRoundTrip);
            RpcText = result.Succeeded ? (result.RpcSucceeded ? Display.Duration(result.RpcRoundTrip) : result.IsPrivate ? "私有节点" : "失败") : Display.None;
            NodeText = Display.ShortId(result.NodeId, 8, 4);
            var mismatch = result.Succeeded && expectedNodeId != null && result.NodeId != expectedNodeId;
            if (!result.Succeeded)
            {
                Tone = StatusTone.Danger;
                OutcomeText = result.Error ?? "无响应";
            }
            else if (mismatch)
            {
                Tone = StatusTone.Warning;
                OutcomeText = "由其他节点应答";
            }
            else if (!result.RequestedTransportAnswered)
            {
                Tone = StatusTone.Warning;
                OutcomeText = Display.Transport(result.RequestedTransport) + " 无应答，已回退到 "
                    + Display.Transport(result.RequestedTransport == DHTTransport.Tcp ? DHTTransport.Udp : DHTTransport.Tcp);
            }
            else if (!result.RpcSucceeded)
            {
                Tone = StatusTone.Warning;
                OutcomeText = result.IsPrivate ? "Hello 成功（私有节点，未测加密请求）" : "Hello 成功，加密请求失败（通告端点可能不可达）";
            }
            else
            {
                Tone = StatusTone.Success;
                OutcomeText = "正常";
            }
        }

        /// <summary>Gets the probe result.</summary>
        public EndpointProbeResult Result { get; }

        /// <summary>Gets the row number text.</summary>
        public string SequenceText { get; }

        /// <summary>Gets the local start time.</summary>
        public string TimeText { get; }

        /// <summary>Gets the probed endpoint.</summary>
        public string EndpointText { get; }

        /// <summary>Gets the transport tried first.</summary>
        public string TransportText { get; }

        /// <summary>Gets the Hello round trip.</summary>
        public string HelloText { get; }

        /// <summary>Gets the encrypted request round trip or its failure.</summary>
        public string RpcText { get; }

        /// <summary>Gets the shortened identifier of the answering node.</summary>
        public string NodeText { get; }

        /// <summary>Gets the outcome summary.</summary>
        public string OutcomeText { get; }
    }

    /// <summary>One per-route transmission statistics row.</summary>
    public sealed class RouteStatRowViewModel : TonedViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="route">The route statistics.</param>
        /// <param name="now">The current time.</param>
        public RouteStatRowViewModel(DHTRouteStatistics route, System.DateTimeOffset now)
        {
            EndpointText = Display.IPEndPoint(route.Endpoint);
            TransportText = Display.Transport(route.Transport);
            SentText = route.SentPackets.ToString(CultureInfo.InvariantCulture);
            LostText = route.LostPackets.ToString(CultureInfo.InvariantCulture);
            LossText = Display.Percent(route.PacketLossRate);
            UpdatedText = Display.Relative(route.LastUpdated, now);
            Tone = route.PacketLossRate == 0 ? StatusTone.Success : route.PacketLossRate < 0.2 ? StatusTone.Warning : StatusTone.Danger;
        }

        /// <summary>Gets the route endpoint.</summary>
        public string EndpointText { get; }

        /// <summary>Gets the route transport.</summary>
        public string TransportText { get; }

        /// <summary>Gets the number of sent transmissions.</summary>
        public string SentText { get; }

        /// <summary>Gets the number of unanswered transmissions.</summary>
        public string LostText { get; }

        /// <summary>Gets the loss rate.</summary>
        public string LossText { get; }

        /// <summary>Gets when the route was last used.</summary>
        public string UpdatedText { get; }
    }
}
