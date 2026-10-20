using System;
using System.Net;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>The outcome of probing one endpoint with a signed Hello exchange followed by one encrypted Ping request.</summary>
    public sealed record EndpointProbeResult
    {
        /// <summary>Gets the probed endpoint.</summary>
        public required IPEndPoint Endpoint { get; init; }

        /// <summary>Gets the transport both requests were sent over.</summary>
        public required DHTTransport Transport { get; init; }

        /// <summary>Gets when the probe started.</summary>
        public required DateTimeOffset StartedAt { get; init; }

        /// <summary>Gets the total probe duration, including binding and releasing the probe's sockets.</summary>
        public required TimeSpan Elapsed { get; init; }

        /// <summary>Gets whether a node answered the signed Hello request.</summary>
        public bool HelloSucceeded { get; init; }

        /// <summary>Gets the round trip of the Hello request, measured from sending it until its answer arrived.</summary>
        public TimeSpan? HelloRoundTrip { get; init; }

        /// <summary>Gets the endpoint the answering node observed for the probe, which reveals the NAT mapping of this host.</summary>
        public IPEndPoint? ObservedEndpoint { get; init; }

        /// <summary>Gets whether the encrypted Ping request to the same endpoint was answered with a success status.</summary>
        public bool PingSucceeded { get; init; }

        /// <summary>Gets the round trip of the encrypted Ping request.</summary>
        public TimeSpan? PingRoundTrip { get; init; }

        /// <summary>Gets the signed contact returned by the answering node, or null when nothing answered.</summary>
        public DHTPeer? Peer { get; init; }

        /// <summary>Gets the total transmissions of both requests, counting UDP retransmissions.</summary>
        public int Transmissions { get; init; }

        /// <summary>Gets the transmissions that received no answer before their attempt timeout.</summary>
        public int LostTransmissions { get; init; }

        /// <summary>Gets a user-facing description of the stage that failed, or null when both requests succeeded.</summary>
        public string? Error { get; init; }

        /// <summary>Gets the identifier of the answering node, or null when nothing answered.</summary>
        public string? NodeId => Peer?.NodeId;

        /// <summary>Gets whether the answering node declared itself private.</summary>
        public bool IsPrivate => Peer != null && Peer.Reachability == DHTReachability.Private;

        /// <summary>Gets whether the probe confirmed the node, which requires an answered Hello.</summary>
        public bool Succeeded => HelloSucceeded;
    }
}
