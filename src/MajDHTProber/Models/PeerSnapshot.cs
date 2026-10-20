using System;
using System.Net;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>An immutable view of everything the prober knows about one remote node.</summary>
    public sealed record PeerSnapshot
    {
        /// <summary>How long a successful exchange or probe keeps a node <see cref="PeerConnectionState.Reachable"/>.</summary>
        public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);

        /// <summary>Gets the node identifier.</summary>
        public required string NodeId { get; init; }

        /// <summary>Gets the newest signed contact seen for the node.</summary>
        public required DHTPeer Contact { get; init; }

        /// <summary>Gets every way the node was learned.</summary>
        public PeerSource Sources { get; init; }

        /// <summary>Gets when the node was first recorded.</summary>
        public DateTimeOffset FirstSeen { get; init; }

        /// <summary>Gets when the node was last recorded from any source.</summary>
        public DateTimeOffset LastSeen { get; init; }

        /// <summary>Gets whether the node is a routing table entry of the hosted node.</summary>
        public bool InRoutingTable { get; init; }

        /// <summary>Gets the last address at which the node proved an advertised endpoint, if any.</summary>
        public IPAddress? VerifiedAddress { get; init; }

        /// <summary>Gets when the hosted node last authenticated an exchange with the node.</summary>
        public DateTimeOffset? LastInteractionAt { get; init; }

        /// <summary>Gets when the hosted node last dropped the node from its routing table after a failure.</summary>
        public DateTimeOffset? RemovedAt { get; init; }

        /// <summary>Gets the latest probe result.</summary>
        public PeerProbeRecord? LastProbe { get; init; }

        /// <summary>Gets an optional user-facing label, such as the name of a node hosted by this prober.</summary>
        public string? Label { get; init; }

        /// <summary>Classifies the connection state at a point in time.</summary>
        /// <param name="now">The time to evaluate leases and recency against.</param>
        /// <returns>The connection state.</returns>
        /// <remarks>
        /// A routing table entry with a live contact is connected. Otherwise the most recent evidence wins: a success within
        /// <see cref="RecentWindow"/> that is newer than any failure makes the node reachable, an expired contact is expired,
        /// any recorded failure makes it disconnected, and anything else is merely discovered.
        /// </remarks>
        public PeerConnectionState GetState(DateTimeOffset now)
        {
            var expired = Contact.ExpiresAt <= now;
            if (InRoutingTable && !expired)
            {
                return PeerConnectionState.Connected;
            }
            var lastSuccess = Latest(LastInteractionAt, LastProbe is { Succeeded: true } ? LastProbe.At : null);
            var lastFailure = Latest(RemovedAt, LastProbe is { Succeeded: false } ? LastProbe.At : null);
            if (!expired && lastSuccess.HasValue && now - lastSuccess.Value <= RecentWindow
                && (!lastFailure.HasValue || lastFailure.Value < lastSuccess.Value))
            {
                return PeerConnectionState.Reachable;
            }
            if (expired)
            {
                return PeerConnectionState.Expired;
            }
            if (lastFailure.HasValue && (!lastSuccess.HasValue || lastFailure.Value >= lastSuccess.Value))
            {
                return PeerConnectionState.Disconnected;
            }
            return PeerConnectionState.Discovered;
        }

        /// <summary>Returns the later of two optional times.</summary>
        /// <param name="left">The first time.</param>
        /// <param name="right">The second time.</param>
        /// <returns>The later time, or null when both are absent.</returns>
        private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
        {
            if (!left.HasValue)
            {
                return right;
            }
            if (!right.HasValue)
            {
                return left;
            }
            return left.Value >= right.Value ? left : right;
        }
    }
}
