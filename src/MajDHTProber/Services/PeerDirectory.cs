using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using MajDHTNetwork;
using MajDHTProber.Models;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Keeps everything a hosted node session knows about remote nodes: routing table observations, manual additions,
    /// lookup results, provider results and probes, merged into one <see cref="PeerSnapshot"/> per node.
    /// </summary>
    /// <remarks>
    /// All members are thread-safe; updates arrive from node threads. Change events are raised after the internal lock is
    /// released and may be raised on any thread.
    /// </remarks>
    public sealed class PeerDirectory
    {
        /// <summary>Protects the peer map.</summary>
        private readonly Lock _gate = new Lock();
        /// <summary>The known peers keyed by node identifier.</summary>
        private readonly Dictionary<string, PeerSnapshot> _peers = new Dictionary<string, PeerSnapshot>(StringComparer.Ordinal);
        /// <summary>The time source.</summary>
        private readonly Func<DateTimeOffset> _clock;

        /// <summary>Creates an empty directory.</summary>
        /// <param name="clock">An optional time source, used by tests; defaults to the current UTC time.</param>
        public PeerDirectory(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Raised after a peer was added or updated.</summary>
        public event EventHandler<PeerSnapshot>? PeerChanged;

        /// <summary>Raised after a peer was forgotten, with its node identifier.</summary>
        public event EventHandler<string>? PeerForgotten;

        /// <summary>Gets the number of known peers.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _peers.Count;
                }
            }
        }

        /// <summary>Returns every known peer.</summary>
        /// <returns>An owned snapshot list.</returns>
        public IReadOnlyList<PeerSnapshot> Snapshot()
        {
            lock (_gate)
            {
                return new List<PeerSnapshot>(_peers.Values);
            }
        }

        /// <summary>Finds a peer by identifier.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>The peer, or null when unknown.</returns>
        public PeerSnapshot? Find(string nodeId)
        {
            lock (_gate)
            {
                return _peers.TryGetValue(nodeId, out var peer) ? peer : null;
            }
        }

        /// <summary>Records a signed contact learned from a source, keeping the contact with the higher sequence.</summary>
        /// <param name="contact">The signed contact.</param>
        /// <param name="source">How the contact was learned.</param>
        /// <param name="inRoutingTable">The routing table membership after this event, or null when it is unknown.</param>
        /// <param name="verifiedAddress">An address at which the node proved an advertised endpoint, or null.</param>
        /// <param name="interaction">Whether this event is an authenticated exchange with the node.</param>
        /// <returns>The updated snapshot.</returns>
        public PeerSnapshot Observe(DHTPeer contact, PeerSource source, bool? inRoutingTable = null, IPAddress? verifiedAddress = null,
            bool interaction = false)
        {
            var now = _clock();
            PeerSnapshot updated;
            lock (_gate)
            {
                if (_peers.TryGetValue(contact.NodeId, out var known))
                {
                    updated = known with
                    {
                        Contact = contact.Sequence >= known.Contact.Sequence ? contact : known.Contact,
                        Sources = known.Sources | source,
                        LastSeen = now,
                        InRoutingTable = inRoutingTable ?? known.InRoutingTable,
                        VerifiedAddress = verifiedAddress ?? known.VerifiedAddress,
                        LastInteractionAt = interaction ? now : known.LastInteractionAt,
                    };
                }
                else
                {
                    updated = new PeerSnapshot
                    {
                        NodeId = contact.NodeId,
                        Contact = contact,
                        Sources = source,
                        FirstSeen = now,
                        LastSeen = now,
                        InRoutingTable = inRoutingTable ?? false,
                        VerifiedAddress = verifiedAddress,
                        LastInteractionAt = interaction ? now : null,
                    };
                }
                _peers[contact.NodeId] = updated;
            }
            Notify(updated);
            return updated;
        }

        /// <summary>
        /// Records that the node dropped a peer from its routing table. For a peer known to be private this is how the node
        /// handles every authenticated exchange, so it counts as an interaction instead of a failure.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>The updated snapshot, or null when the peer is unknown.</returns>
        public PeerSnapshot? RecordRoutingRemoval(string nodeId)
        {
            var now = _clock();
            PeerSnapshot? updated = null;
            lock (_gate)
            {
                if (_peers.TryGetValue(nodeId, out var known))
                {
                    updated = known.Contact.Reachability == DHTReachability.Private
                        ? known with { InRoutingTable = false, LastInteractionAt = now, LastSeen = now }
                        : known with { InRoutingTable = false, RemovedAt = now };
                    _peers[nodeId] = updated;
                }
            }
            if (updated != null)
            {
                Notify(updated);
            }
            return updated;
        }

        /// <summary>Remembers the latest probe result of a peer.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="probe">The probe result.</param>
        /// <returns>The updated snapshot, or null when the peer is unknown.</returns>
        public PeerSnapshot? RecordProbe(string nodeId, PeerProbeRecord probe)
        {
            return Update(nodeId, known => known with { LastProbe = probe, Sources = known.Sources | PeerSource.Probe });
        }

        /// <summary>Sets a user-facing label.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="label">The label, or null to clear it.</param>
        /// <returns>The updated snapshot, or null when the peer is unknown.</returns>
        public PeerSnapshot? SetLabel(string nodeId, string? label)
        {
            return Update(nodeId, known => known with { Label = label });
        }

        /// <summary>Aligns routing table membership flags with the node's current table entries.</summary>
        /// <param name="routedNodeIds">The identifiers of the current table entries.</param>
        /// <returns>The number of peers whose flag changed.</returns>
        public int ReconcileRouting(ISet<string> routedNodeIds)
        {
            var changed = new List<PeerSnapshot>();
            lock (_gate)
            {
                foreach (var peer in new List<PeerSnapshot>(_peers.Values))
                {
                    var inTable = routedNodeIds.Contains(peer.NodeId);
                    if (inTable != peer.InRoutingTable)
                    {
                        var updated = peer with { InRoutingTable = inTable };
                        _peers[peer.NodeId] = updated;
                        changed.Add(updated);
                    }
                }
            }
            foreach (var peer in changed)
            {
                Notify(peer);
            }
            return changed.Count;
        }

        /// <summary>Forgets a peer.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>Whether the peer was known.</returns>
        public bool Forget(string nodeId)
        {
            bool removed;
            lock (_gate)
            {
                removed = _peers.Remove(nodeId);
            }
            if (removed)
            {
                try
                {
                    PeerForgotten?.Invoke(this, nodeId);
                }
                catch (Exception)
                {
                    // Listener failures must not break callers on node threads.
                }
            }
            return removed;
        }

        /// <summary>Forgets every peer.</summary>
        public void Clear()
        {
            List<string> removed;
            lock (_gate)
            {
                removed = new List<string>(_peers.Keys);
                _peers.Clear();
            }
            foreach (var nodeId in removed)
            {
                try
                {
                    PeerForgotten?.Invoke(this, nodeId);
                }
                catch (Exception)
                {
                    // Listener failures must not break callers.
                }
            }
        }

        /// <summary>Applies an update to a known peer.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="update">Creates the updated snapshot.</param>
        /// <returns>The updated snapshot, or null when the peer is unknown.</returns>
        private PeerSnapshot? Update(string nodeId, Func<PeerSnapshot, PeerSnapshot> update)
        {
            PeerSnapshot? updated = null;
            lock (_gate)
            {
                if (_peers.TryGetValue(nodeId, out var known))
                {
                    updated = update(known);
                    _peers[nodeId] = updated;
                }
            }
            if (updated != null)
            {
                Notify(updated);
            }
            return updated;
        }

        /// <summary>Notifies listeners while isolating listener failures.</summary>
        /// <param name="peer">The changed peer.</param>
        private void Notify(PeerSnapshot peer)
        {
            try
            {
                PeerChanged?.Invoke(this, peer);
            }
            catch (Exception)
            {
                // Updates arrive from node threads; listener failures must never reach the node.
            }
        }
    }
}
