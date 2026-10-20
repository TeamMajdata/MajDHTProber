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
            var contact = node.LocalPeer;
            var now = DateTimeOffset.UtcNow;
            var discovered = new PeerSnapshot { NodeId = contact.NodeId, Contact = contact, FirstSeen = now, LastSeen = now };

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
            Assert.Equal(PeerConnectionState.Expired, (discovered with { InRoutingTable = true }).GetState(contact.ExpiresAt.AddSeconds(1)));
        }

        /// <summary>The directory merges sources, keeps flags and raises change events.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task MergesObservations()
        {
            await using var node = await TestNodes.StartAsync();
            var contact = node.LocalPeer;
            var directory = new PeerDirectory();
            var changes = new List<PeerSnapshot>();
            var forgotten = new List<string>();
            directory.PeerChanged += (_, peer) => changes.Add(peer);
            directory.PeerForgotten += (_, id) => forgotten.Add(id);

            directory.Observe(contact, PeerSource.Lookup);
            var merged = directory.Observe(contact, PeerSource.Routing, true, IPAddress.Loopback, interaction: true);

            Assert.Equal(PeerSource.Lookup | PeerSource.Routing, merged.Sources);
            Assert.True(merged.InRoutingTable);
            Assert.Equal(IPAddress.Loopback, merged.VerifiedAddress);
            Assert.NotNull(merged.LastInteractionAt);
            Assert.Equal(2, changes.Count);

            var removed = directory.RecordRoutingRemoval(contact.NodeId)!;
            Assert.False(removed.InRoutingTable);
            Assert.NotNull(removed.RemovedAt);

            Assert.Equal(1, directory.ReconcileRouting(new HashSet<string> { contact.NodeId }));
            Assert.True(directory.Find(contact.NodeId)!.InRoutingTable);

            directory.SetLabel(contact.NodeId, "标签");
            Assert.Equal("标签", directory.Find(contact.NodeId)!.Label);
            Assert.Null(directory.RecordProbe("unknown", new PeerProbeRecord { At = DateTimeOffset.UtcNow, Succeeded = true }));

            Assert.True(directory.Forget(contact.NodeId));
            Assert.Equal(new[] { contact.NodeId }, forgotten);
            Assert.Equal(0, directory.Count);
        }

        /// <summary>Observed contacts become table entries and every observation and removal is reported.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task TracksObservedContacts()
        {
            await using var remote = await TestNodes.StartAsync();
            var table = new TrackingRoutingTable();
            var observed = new List<RoutingObservation>();
            var removed = new List<string>();
            table.Observed += (_, observation) => observed.Add(observation);
            table.Removed += (_, id) => removed.Add(id);

            Assert.Null(table.LocalNodeId);
            Assert.Empty(table.Snapshot());
            Assert.False(table.Contains(remote.NodeId));
            table.Initialize(new DHTIdentity().NodeId, 8);

            table.Observe(remote.LocalPeer, IPAddress.Loopback);
            Assert.True(table.Contains(remote.NodeId));
            Assert.Single(table.Snapshot());
            var admission = Assert.Single(observed);
            Assert.True(admission.InTable);
            Assert.Equal(IPAddress.Loopback, admission.VerifiedAddress);
            Assert.Same(remote.LocalPeer, admission.Peer);

            table.Observe(remote.LocalPeer, null);
            Assert.Equal(2, observed.Count);
            Assert.True(observed[1].InTable);
            Assert.Null(observed[1].VerifiedAddress);

            table.Remove(remote.NodeId);
            Assert.False(table.Contains(remote.NodeId));
            Assert.Equal(new[] { remote.NodeId }, removed);

            table.Observe(remote.LocalPeer, IPAddress.Loopback);
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

            table.Observe(remote.LocalPeer, IPAddress.Loopback);
            Assert.True(table.Contains(remote.NodeId));
            table.Remove(remote.NodeId);
            Assert.False(table.Contains(remote.NodeId));
        }
    }
}
