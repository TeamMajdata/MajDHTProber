using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Covers connection state classification, the peer directory and the tracking routing table with real contacts.</summary>
    public sealed class PeerTrackingTests
    {
        /// <summary>The state follows routing membership first, then the most recent evidence.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ClassifiesConnectionStates()
        {
            await using var node = await TestNodes.StartAsync();
            var peer = node.LocalPeer;
            var now = DateTimeOffset.UtcNow;
            var discovered = new PeerSnapshot { NodeId = peer.NodeId, Peer = peer, FirstSeen = now, LastSeen = now };

            Assert.Equal(PeerConnectionState.Discovered, discovered.GetState(now));
            Assert.Equal(PeerConnectionState.Connected, (discovered with { InRoutingTable = true }).GetState(now));
            Assert.Equal(PeerConnectionState.Reachable, (discovered with { LastInteractionAt = now.AddMinutes(-1) }).GetState(now));
            Assert.Equal(PeerConnectionState.Discovered, (discovered with { LastInteractionAt = now.AddMinutes(-10) }).GetState(now));
            Assert.Equal(PeerConnectionState.Disconnected,
                (discovered with { LastInteractionAt = now.AddMinutes(-2), RemovedAt = now.AddMinutes(-1) }).GetState(now));
            Assert.Equal(PeerConnectionState.Reachable,
                (discovered with { RemovedAt = now.AddMinutes(-2), LastProbe = new PeerProbeRecord { At = now.AddMinutes(-1), Succeeded = true } }).GetState(now));
            Assert.Equal(PeerConnectionState.Disconnected,
                (discovered with { LastProbe = new PeerProbeRecord { At = now, Succeeded = false } }).GetState(now));
            Assert.Equal(PeerConnectionState.Expired, (discovered with { InRoutingTable = true }).GetState(peer.ExpiresAt.AddSeconds(1)));
        }

        /// <summary>The directory merges sources, keeps flags and raises change events.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task MergesObservations()
        {
            await using var node = await TestNodes.StartAsync();
            var peer = node.LocalPeer;
            var observed = peer.Endpoints[0];
            var directory = new PeerDirectory();
            var changes = new List<PeerSnapshot>();
            var forgotten = new List<string>();
            directory.PeerChanged += (_, changed) => changes.Add(changed);
            directory.PeerForgotten += (_, id) => forgotten.Add(id);

            directory.Observe(peer, PeerSource.Lookup);
            var merged = directory.Observe(peer, PeerSource.Routing, true, observed, interaction: true);

            Assert.Equal(PeerSource.Lookup | PeerSource.Routing, merged.Sources);
            Assert.True(merged.InRoutingTable);
            Assert.Equal(observed, merged.ObservedEndpoint);
            Assert.NotNull(merged.LastInteractionAt);
            Assert.Equal(2, changes.Count);

            var removed = directory.RecordRoutingRemoval(peer.NodeId)!;
            Assert.False(removed.InRoutingTable);
            Assert.NotNull(removed.RemovedAt);

            Assert.Equal(1, directory.ReconcileRouting(new HashSet<string> { peer.NodeId }));
            Assert.True(directory.Find(peer.NodeId)!.InRoutingTable);

            directory.SetLabel(peer.NodeId, "标签");
            Assert.Equal("标签", directory.Find(peer.NodeId)!.Label);
            Assert.Null(directory.RecordProbe("unknown", new PeerProbeRecord { At = DateTimeOffset.UtcNow, Succeeded = true }));

            Assert.True(directory.Forget(peer.NodeId));
            Assert.Equal(new[] { peer.NodeId }, forgotten);
            Assert.Equal(0, directory.Count);
        }

        /// <summary>
        /// Endpoints reported by different sources for one node are merged by address, and a newer contact replaces an older
        /// one without losing the endpoints known so far.
        /// </summary>
        [Fact]
        public void MergesEndpointsAndContacts()
        {
            var identity = new DHTIdentity();
            var first = DHTContact.Create(identity, DHTCapabilities.Download);
            var second = DHTContact.Create(identity, DHTCapabilities.Download | DHTCapabilities.Provide);
            var loopbackTcp = new DHTEndpoint(IPAddress.Loopback, 42000, 0);
            var loopbackUdp = new DHTEndpoint(IPAddress.Loopback, 0, 42001);
            var remote = new DHTEndpoint(IPAddress.Parse("203.0.113.5"), 42002, 42002);
            var directory = new PeerDirectory();

            directory.Observe(new DHTPeer(second, new[] { loopbackTcp }), PeerSource.Lookup);
            var merged = directory.Observe(new DHTPeer(first, new[] { loopbackUdp, remote }), PeerSource.Routing);

            Assert.Equal(second.Sequence, merged.Peer.Sequence);
            Assert.Equal(DHTCapabilities.Download | DHTCapabilities.Provide, merged.Peer.Capabilities);
            Assert.Equal(2, merged.Peer.Endpoints.Count);
            Assert.Contains(merged.Peer.Endpoints, endpoint => endpoint.Equals(new DHTEndpoint(IPAddress.Loopback, 42000, 42001)));
            Assert.Contains(merged.Peer.Endpoints, endpoint => endpoint.Equals(remote));
        }

        /// <summary>Observed contacts become table entries and every observation and removal is reported with the table's endpoints.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task TracksObservedContacts()
        {
            await using var remote = await TestNodes.StartAsync();
            var contact = remote.LocalContact;
            var endpoint = remote.LocalPeer.Endpoints[0];
            var table = new TrackingRoutingTable();
            var observed = new List<RoutingObservation>();
            var removed = new List<string>();
            table.Observed += (_, observation) => observed.Add(observation);
            table.Removed += (_, id) => removed.Add(id);

            Assert.Null(table.LocalNodeId);
            Assert.Empty(table.Snapshot());
            Assert.False(table.Contains(remote.NodeId));
            table.Initialize(new DHTIdentity().NodeId, 8);

            table.Observe(contact, endpoint);
            Assert.True(table.Contains(remote.NodeId));
            Assert.Single(table.Snapshot());
            var admission = Assert.Single(observed);
            Assert.True(admission.InTable);
            Assert.Equal(endpoint, admission.Observed);
            Assert.Equal(remote.NodeId, admission.Peer.NodeId);
            Assert.Equal(endpoint, Assert.Single(admission.Peer.Endpoints));

            table.Observe(contact, null);
            Assert.Equal(2, observed.Count);
            Assert.True(observed[1].InTable);
            Assert.Null(observed[1].Observed);
            Assert.Equal(endpoint, Assert.Single(observed[1].Peer.Endpoints));

            table.Remove(remote.NodeId);
            Assert.False(table.Contains(remote.NodeId));
            Assert.Equal(new[] { remote.NodeId }, removed);

            // A refresh without an observed endpoint cannot admit a new contact, and the report says so.
            table.Observe(contact, null);
            Assert.False(observed[2].InTable);
            Assert.Empty(observed[2].Peer.Endpoints);

            table.Observe(contact, endpoint);
            table.Evict(remote.NodeId);
            Assert.False(table.Contains(remote.NodeId));
            Assert.Single(removed);
        }

        /// <summary>A throwing listener never reaches the node.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task IsolatesListenerFailures()
        {
            await using var remote = await TestNodes.StartAsync();
            var table = new TrackingRoutingTable();
            table.Observed += (_, _) => throw new InvalidOperationException("listener");
            table.Removed += (_, _) => throw new InvalidOperationException("listener");
            table.Initialize(new DHTIdentity().NodeId, 8);

            table.Observe(remote.LocalContact, remote.LocalPeer.Endpoints[0]);
            Assert.True(table.Contains(remote.NodeId));
            table.Remove(remote.NodeId);
            Assert.False(table.Contains(remote.NodeId));
        }
    }
}
