using System;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>Options for adding a node by endpoint.</summary>
    public sealed record AddPeerOptions
    {
        /// <summary>Gets the transport tried first when contacting the endpoint.</summary>
        public DHTTransport Transport { get; init; } = DHTTransport.Tcp;

        /// <summary>Gets whether a neighbor lookup through the new node follows, as when joining through it.</summary>
        public bool Join { get; init; } = true;

        /// <summary>Gets whether the hosted node registers with the new node when it offers relay assistance.</summary>
        public bool RegisterRelay { get; init; }
    }

    /// <summary>The outcome of adding a node by endpoint.</summary>
    public sealed record AddPeerResult
    {
        /// <summary>Gets the probe that contacted the endpoint.</summary>
        public required EndpointProbeResult Probe { get; init; }

        /// <summary>Gets whether the node was recorded in the peer list.</summary>
        public bool Added { get; init; }

        /// <summary>Gets whether the hosted node's routing table accepted the node as a lookup hop.</summary>
        public bool AdmittedToRoutingTable { get; init; }

        /// <summary>Gets the number of neighbors found by the follow-up lookup, when one ran.</summary>
        public int? NeighborsFound { get; init; }

        /// <summary>Gets whether relay registration succeeded, when it was attempted.</summary>
        public bool? RelayRegistered { get; init; }

        /// <summary>Gets a user-facing explanation of anything noteworthy.</summary>
        public string? Note { get; init; }
    }

    /// <summary>The outcome of checking whether a hosted node can reach a peer through its own routes.</summary>
    public sealed record RouteCheckResult
    {
        /// <summary>Gets whether the peer answered a lookup sent through the hosted node's routes.</summary>
        public required bool Reachable { get; init; }

        /// <summary>Gets the duration of the lookup.</summary>
        public required TimeSpan Elapsed { get; init; }

        /// <summary>Gets the number of responsive nodes the lookup found.</summary>
        public int NodesFound { get; init; }

        /// <summary>Gets the hosted node's transmission statistics for the peer after the lookup.</summary>
        public DHTPeerStatistics? Statistics { get; init; }
    }

    /// <summary>Requests a loopback node hosted by the prober.</summary>
    public sealed record LocalNodeRequest
    {
        /// <summary>Gets the capabilities of the new node.</summary>
        public DHTCapabilities Capabilities { get; init; } = DHTCapabilities.Download | DHTCapabilities.Provide | DHTCapabilities.Relay;
    }
}
