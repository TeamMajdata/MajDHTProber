using System;
using System.Collections.Generic;
using System.Net;
using MajDHTNetwork;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>Tunes one LAN scan.</summary>
    public sealed record LanScanOptions
    {
        /// <summary>Gets the UDP port the Hello is broadcast to; nodes listening on another port are not found.</summary>
        public int Port { get; init; } = ProberSettings.DefaultPort;

        /// <summary>Gets how long replies are collected after the broadcast.</summary>
        public TimeSpan ResponseWindow { get; init; } = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// Gets explicit endpoints to send the Hello to instead of the subnet broadcast addresses, used by tests and for
        /// probing a known subnet; null broadcasts to every local IPv4 subnet.
        /// </summary>
        public IReadOnlyList<IPEndPoint>? Targets { get; init; }

        /// <summary>Gets the probe identity tuning: identity work and persistence.</summary>
        public ProbeOptions Probe { get; init; } = new ProbeOptions();
    }

    /// <summary>One node that answered a LAN scan.</summary>
    public sealed record DiscoveredPeer
    {
        /// <summary>Gets the signed contact of the node at the endpoint it answered from.</summary>
        public required DHTPeer Peer { get; init; }

        /// <summary>Gets the UDP endpoint the answer came from, which is where the node listens.</summary>
        public required IPEndPoint Endpoint { get; init; }

        /// <summary>Gets the time between the broadcast and the first answer of this node, including the cookie handshake.</summary>
        public required TimeSpan RoundTrip { get; init; }

        /// <summary>Gets the endpoint the node observed for the scanning socket.</summary>
        public required IPEndPoint ObservedEndpoint { get; init; }

        /// <summary>Gets how many answers the node sent, which exceeds one when it received the Hello on several subnets.</summary>
        public int Replies { get; init; } = 1;

        /// <summary>Gets the node identifier.</summary>
        public string NodeId => Peer.NodeId;
    }

    /// <summary>The outcome of one LAN scan.</summary>
    public sealed record LanScanResult
    {
        /// <summary>Gets the endpoints the Hello was sent to.</summary>
        public required IReadOnlyList<IPEndPoint> Targets { get; init; }

        /// <summary>Gets the nodes that answered, in the order of their first answer.</summary>
        public required IReadOnlyList<DiscoveredPeer> Peers { get; init; }

        /// <summary>Gets the total scan duration, including the response window.</summary>
        public required TimeSpan Elapsed { get; init; }

        /// <summary>Gets the number of datagrams that were neither valid frames of the handshake nor authenticated answers.</summary>
        public int InvalidReplies { get; init; }
    }
}
