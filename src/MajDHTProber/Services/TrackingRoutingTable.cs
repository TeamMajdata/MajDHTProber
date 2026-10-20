using System;
using System.Collections.Generic;
using System.Net;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Describes one contact the hosted node offered to its routing table.</summary>
    public sealed class RoutingObservation : EventArgs
    {
        /// <summary>Creates the event data.</summary>
        /// <param name="peer">The authenticated contact.</param>
        /// <param name="verifiedAddress">The address at which the contact proved an advertised endpoint, or null.</param>
        /// <param name="inTable">Whether the contact is a table entry after the observation.</param>
        public RoutingObservation(DHTPeer peer, IPAddress? verifiedAddress, bool inTable)
        {
            Peer = peer;
            VerifiedAddress = verifiedAddress;
            InTable = inTable;
        }

        /// <summary>Gets the authenticated contact.</summary>
        public DHTPeer Peer { get; }

        /// <summary>Gets the address at which the contact proved an advertised endpoint, or null.</summary>
        public IPAddress? VerifiedAddress { get; }

        /// <summary>Gets whether the contact is a table entry after the observation.</summary>
        public bool InTable { get; }
    }

    /// <summary>
    /// Wraps the library's routing table and reports every contact the node observes or drops, so the prober can keep its
    /// peer list in step with the node without changing how the node admits contacts.
    /// </summary>
    /// <remarks>
    /// The node calls every member concurrently and expects quick returns without network I/O. Event handlers therefore
    /// run synchronously on node threads and must only record state; their failures are isolated from the node.
    /// </remarks>
    public sealed class TrackingRoutingTable : IDHTRoutingTable
    {
        /// <summary>The decorated routing table that owns the actual entries.</summary>
        private readonly IDHTRoutingTable _inner;
        /// <summary>The owning node's identifier once initialized.</summary>
        private volatile string? _localNodeId;

        /// <summary>Creates a tracking table over the library's default Kademlia table.</summary>
        public TrackingRoutingTable()
            : this(new KademliaDHTRoutingTable())
        {
        }

        /// <summary>Creates a tracking table over another table.</summary>
        /// <param name="inner">An unbound routing table.</param>
        public TrackingRoutingTable(IDHTRoutingTable inner)
        {
            _inner = inner;
        }

        /// <summary>Raised after the node observed a contact; raised on node threads.</summary>
        public event EventHandler<RoutingObservation>? Observed;

        /// <summary>Raised after the node dropped a contact after a failure or because it became private; raised on node threads.</summary>
        public event EventHandler<string>? Removed;

        /// <summary>Gets the owning node's identifier, or null before the node initialized the table.</summary>
        public string? LocalNodeId => _localNodeId;

        /// <summary>Binds the decorated table to the owning node.</summary>
        /// <param name="localNodeId">The owning node's identifier.</param>
        /// <param name="bucketSize">The configured bucket size.</param>
        /// <exception cref="InvalidOperationException">The table is already bound.</exception>
        public void Initialize(string localNodeId, int bucketSize)
        {
            _inner.Initialize(localNodeId, bucketSize);
            _localNodeId = localNodeId;
        }

        /// <summary>Forwards an authenticated contact to the decorated table and reports it.</summary>
        /// <param name="peer">The authenticated contact.</param>
        /// <param name="verifiedAddress">The proven advertised address, or null to refresh a known contact only.</param>
        public void Observe(DHTPeer peer, IPAddress? verifiedAddress)
        {
            _inner.Observe(peer, verifiedAddress);
            Raise(Observed, new RoutingObservation(peer, verifiedAddress, Contains(peer.NodeId)));
        }

        /// <summary>Removes a contact from the decorated table and reports it.</summary>
        /// <param name="nodeId">The node identifier.</param>
        public void Remove(string nodeId)
        {
            _inner.Remove(nodeId);
            Raise(Removed, nodeId);
        }

        /// <summary>Returns the decorated table's nearest live contacts.</summary>
        /// <param name="routingId">The target identifier.</param>
        /// <param name="count">The maximum number of contacts.</param>
        /// <returns>The nearest contacts.</returns>
        public IReadOnlyList<DHTPeer> Closest(string routingId, int count)
        {
            return _inner.Closest(routingId, count);
        }

        /// <summary>Removes a contact on behalf of the user without reporting it as a node failure.</summary>
        /// <param name="nodeId">The node identifier.</param>
        public void Evict(string nodeId)
        {
            _inner.Remove(nodeId);
        }

        /// <summary>Checks whether a node is a live table entry.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>Whether the node is present.</returns>
        public bool Contains(string nodeId)
        {
            if (_localNodeId == null)
            {
                return false;
            }
            // A present node has distance zero to its own identifier, so it is always the nearest result.
            foreach (var peer in _inner.Closest(nodeId, 1))
            {
                return peer.NodeId == nodeId;
            }
            return false;
        }

        /// <summary>Returns every live table entry.</summary>
        /// <param name="maximum">The maximum number of entries.</param>
        /// <returns>The entries nearest the local node first, or an empty list before initialization.</returns>
        public IReadOnlyList<DHTPeer> Snapshot(int maximum = 4096)
        {
            var local = _localNodeId;
            return local == null ? Array.Empty<DHTPeer>() : _inner.Closest(local, maximum);
        }

        /// <summary>Notifies listeners while isolating listener failures from node threads.</summary>
        /// <typeparam name="T">The event data type.</typeparam>
        /// <param name="handler">The event handlers.</param>
        /// <param name="args">The event data.</param>
        private void Raise<T>(EventHandler<T>? handler, T args)
        {
            try
            {
                handler?.Invoke(this, args);
            }
            catch (Exception)
            {
                // Routing table calls happen inside request handling; listener failures must never reach the node.
            }
        }
    }
}
