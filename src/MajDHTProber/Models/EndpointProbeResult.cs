using System;
using System.Collections.Generic;
using System.Net;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>The outcome of probing one endpoint with a signed Hello exchange followed by one encrypted lookup request.</summary>
    public sealed record EndpointProbeResult
    {
        /// <summary>Gets the probed endpoint.</summary>
        public required IPEndPoint Endpoint { get; init; }

        /// <summary>Gets the transport that was tried first.</summary>
        public required DHTTransport RequestedTransport { get; init; }

        /// <summary>Gets when the probe started.</summary>
        public required DateTimeOffset StartedAt { get; init; }

        /// <summary>Gets the total probe duration, including node setup and teardown of the probe client.</summary>
        public required TimeSpan Elapsed { get; init; }

        /// <summary>Gets whether a node answered the signed Hello request.</summary>
        public bool HelloSucceeded { get; init; }

        /// <summary>Gets the time from the start of the probe until the Hello answer was authenticated.</summary>
        public TimeSpan? HelloRoundTrip { get; init; }

        /// <summary>Gets whether the follow-up encrypted lookup request to the node's advertised endpoints succeeded.</summary>
        public bool RpcSucceeded { get; init; }

        /// <summary>Gets the duration of the follow-up encrypted lookup request.</summary>
        public TimeSpan? RpcRoundTrip { get; init; }

        /// <summary>Gets whether at least one transmission over <see cref="RequestedTransport"/> was answered.</summary>
        public bool RequestedTransportAnswered { get; init; }

        /// <summary>Gets the signed contact returned by the node, or null when it was not retained (for example a private node).</summary>
        public DHTPeer? Peer { get; init; }

        /// <summary>Gets the identifier of the answering node, or null when nothing answered.</summary>
        public string? NodeId { get; init; }

        /// <summary>Gets whether the answering node declared itself private.</summary>
        public bool IsPrivate { get; init; }

        /// <summary>Gets the per-route transmission statistics the probe client collected for the answering node.</summary>
        public IReadOnlyList<DHTRouteStatistics> Routes { get; init; } = Array.Empty<DHTRouteStatistics>();

        /// <summary>Gets a user-facing failure description, or null on success.</summary>
        public string? Error { get; init; }

        /// <summary>Gets whether the probe confirmed the node, which requires an answered Hello.</summary>
        public bool Succeeded => HelloSucceeded;
    }
}
