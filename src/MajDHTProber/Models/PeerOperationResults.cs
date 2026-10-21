using System;
using System.Net;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>Options for adding a node by endpoint.</summary>
    public sealed record AddPeerOptions
    {
        /// <summary>
        /// Gets the transport tried first when contacting the endpoint, or null for the hosted node's preferred transport;
        /// the other transport is tried at the same port when the first one does not answer.
        /// </summary>
        public DHTTransport? Transport { get; init; }
    }

    /// <summary>The outcome of adding a node by endpoint through the hosted node's own signed Hello exchange.</summary>
    public sealed record AddPeerResult
    {
        /// <summary>Gets the contacted endpoint.</summary>
        public required IPEndPoint Endpoint { get; init; }

        /// <summary>Gets how long the Hello exchange, Traversal node and Forwarder registration and neighbor discovery took.</summary>
        public required TimeSpan Elapsed { get; init; }

        /// <summary>Gets the signed contact of the node that answered, or null when nothing was added.</summary>
        public DHTPeer? Peer { get; init; }

        /// <summary>Gets whether the hosted node's routing table accepted the node as a lookup hop.</summary>
        public bool AdmittedToRoutingTable { get; init; }

        /// <summary>Gets the size of the routing table after the neighbor discovery that followed.</summary>
        public int RoutingTableSize { get; init; }

        /// <summary>Gets a user-facing explanation of anything noteworthy, or the reason nothing was added.</summary>
        public string? Note { get; init; }

        /// <summary>Gets whether a node answered and was recorded in the peer list.</summary>
        public bool Added => Peer != null;
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
        public DHTCapabilities Capabilities { get; init; } =
            DHTCapabilities.Download | DHTCapabilities.Provide | DHTCapabilities.Traversal | DHTCapabilities.Forwarder;
    }
}
