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
            TransportText = Display.Transport(result.Transport);
            HelloText = Display.Duration(result.HelloRoundTrip);
            PingText = result.Succeeded ? (result.PingSucceeded ? Display.Duration(result.PingRoundTrip) : "失败") : Display.None;
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
            else if (!result.PingSucceeded)
            {
                Tone = StatusTone.Warning;
                OutcomeText = result.Error ?? "Hello 成功，加密 Ping 失败";
            }
            else
            {
                Tone = StatusTone.Success;
                OutcomeText = (result.IsPrivate ? "正常（私有节点）" : "正常")
                    + (result.ObservedEndpoint == null ? string.Empty : "，对方观察到本机 " + Display.IPEndPoint(result.ObservedEndpoint));
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

        /// <summary>Gets the probed transport.</summary>
        public string TransportText { get; }

        /// <summary>Gets the Hello round trip.</summary>
        public string HelloText { get; }

        /// <summary>Gets the encrypted Ping round trip or its failure.</summary>
        public string PingText { get; }

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
