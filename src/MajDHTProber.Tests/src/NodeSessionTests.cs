using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Drives hosted node sessions against real loopback nodes.</summary>
    public sealed class NodeSessionTests
    {
        /// <summary>A session starts with its settings, exposes contact endpoints and stops again.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task StartsAndStops()
        {
            var sink = new LogSink();
            await using var session = TestNodes.CreateSession(sink, TestNodes.LoopbackSettings());
            var states = new List<NodeSessionState>();
            session.StateChanged += (_, _) => states.Add(session.State);

            await session.StartAsync(TestContext.Current.CancellationToken);

            Assert.True(session.IsRunning);
            Assert.NotNull(session.NodeId);
            Assert.NotNull(session.StartedAt);
            Assert.NotNull(session.RoutingTable);
            var endpoint = Assert.Single(session.GetContactEndpoints());
            Assert.Equal(IPAddress.Loopback, endpoint.Address);
            Assert.NotEqual(0, endpoint.Port);

            await session.StopAsync();

            Assert.Equal(NodeSessionState.Stopped, session.State);
            Assert.Null(session.RoutingTable);
            Assert.Equal(new[] { NodeSessionState.Starting, NodeSessionState.Running, NodeSessionState.Stopping, NodeSessionState.Stopped }, states);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshNeighborsAsync(TestContext.Current.CancellationToken));
        }

        /// <summary>Invalid settings fault the session with a readable error.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task FaultsOnInvalidSettings()
        {
            var settings = TestNodes.LoopbackSettings();
            settings.ListenAddresses = "not-an-address";
            await using var session = TestNodes.CreateSession(new LogSink(), settings);

            await Assert.ThrowsAsync<SettingsValidationException>(() => session.StartAsync(TestContext.Current.CancellationToken));

            Assert.Equal(NodeSessionState.Faulted, session.State);
            Assert.Contains("监听地址", session.LastError, StringComparison.Ordinal);
        }

        /// <summary>A node added by endpoint answers the hosted node's Hello, is recorded, admitted to the routing table and connected.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task AddsPeerByEndpoint()
        {
            using var timeout = TestNodes.Timeout();
            await using var target = await TestNodes.StartAsync(DHTCapabilities.Download | DHTCapabilities.Provide | DHTCapabilities.Relay);
            await using var session = TestNodes.CreateSession(new LogSink(), TestNodes.LoopbackSettings());
            await session.StartAsync(timeout.Token);

            var result = await session.AddPeerAsync(TestNodes.TcpEndpoint(target), new AddPeerOptions { Transport = DHTTransport.Tcp }, timeout.Token);

            Assert.True(result.Added, result.Note);
            Assert.True(result.AdmittedToRoutingTable);
            Assert.Equal(target.NodeId, result.Peer!.NodeId);
            Assert.True(result.RoutingTableSize >= 1);
            Assert.True(result.Elapsed > TimeSpan.Zero);
            var peer = session.Peers.Find(target.NodeId);
            Assert.NotNull(peer);
            Assert.True((peer!.Sources & PeerSource.Manual) != 0);
            Assert.Equal(IPAddress.Loopback, peer.VerifiedAddress);
            Assert.Equal(PeerConnectionState.Connected, peer.GetState(DateTimeOffset.UtcNow));
            Assert.True(session.RoutingTable!.Contains(target.NodeId));
            Assert.True(target.KnownPeers.Count >= 1);
        }

        /// <summary>Adding the session's own endpoint is refused with an explanation.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task RefusesToAddItself()
        {
            using var timeout = TestNodes.Timeout();
            await using var session = TestNodes.CreateSession(new LogSink(), TestNodes.LoopbackSettings());
            await session.StartAsync(timeout.Token);

            var result = await session.AddPeerAsync(session.GetContactEndpoints()[0], new AddPeerOptions(), timeout.Token);

            Assert.False(result.Added);
            Assert.Contains("本节点自己", result.Note, StringComparison.Ordinal);
            Assert.Equal(0, session.Peers.Count);
        }

        /// <summary>An endpoint that answers through neither transport is reported in the result instead of thrown.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ReportsUnreachableEndpointWhenAdding()
        {
            using var timeout = TestNodes.Timeout();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await using var session = TestNodes.CreateSession(new LogSink(), TestNodes.LoopbackSettings());
            await session.StartAsync(timeout.Token);

            var result = await session.AddPeerAsync(new IPEndPoint(IPAddress.Loopback, port), new AddPeerOptions(), timeout.Token);

            Assert.False(result.Added);
            Assert.Null(result.Peer);
            Assert.Contains("未响应", result.Note, StringComparison.Ordinal);
            Assert.Equal(0, session.Peers.Count);
        }

        /// <summary>Probing a known peer covers every advertised endpoint and transport and remembers the outcome.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ProbesKnownPeer()
        {
            using var timeout = TestNodes.Timeout();
            await using var target = await TestNodes.StartAsync();
            await using var session = TestNodes.CreateSession(new LogSink(), TestNodes.LoopbackSettings());
            await session.StartAsync(timeout.Token);
            await session.AddPeerAsync(TestNodes.UdpEndpoint(target), new AddPeerOptions { Transport = DHTTransport.Udp }, timeout.Token);

            var results = await session.ProbePeerAsync(session.Peers.Find(target.NodeId)!, timeout.Token);
            var route = await session.CheckRouteAsync(target.LocalPeer, timeout.Token);

            Assert.Equal(2, results.Count);
            Assert.All(results, result => Assert.Equal(target.NodeId, result.NodeId));
            Assert.All(results, result => Assert.True(result.PingSucceeded, result.Error));
            Assert.Contains(results, result => result.Transport == DHTTransport.Tcp);
            Assert.Contains(results, result => result.Transport == DHTTransport.Udp);
            Assert.True(session.Peers.Find(target.NodeId)!.LastProbe!.Succeeded);
            Assert.True(route.Reachable);
            Assert.NotNull(route.Statistics);
        }

        /// <summary>A session saves its routing table when it stops and rejoins the saved nodes when it starts again.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task RestoresSavedRoutingTable()
        {
            using var timeout = TestNodes.Timeout();
            using var directory = new TempDirectory();
            var sink = new LogSink();
            var routing = new RoutingSnapshotStore(directory.Path, sink.CreateLogger("路由表"));
            await using var target = await TestNodes.StartAsync();
            var settings = TestNodes.LoopbackSettings();
            settings.PersistRoutingTable = true;
            await using var session = TestNodes.CreateSession(sink, settings, "测试节点", routing);
            await session.StartAsync(timeout.Token);
            Assert.Equal(0, await session.RestoreRoutingTableAsync(timeout.Token));
            var added = await session.AddPeerAsync(TestNodes.TcpEndpoint(target), new AddPeerOptions(), timeout.Token);
            Assert.True(added.AdmittedToRoutingTable, added.Note);

            await session.StopAsync();

            Assert.True(File.Exists(Path.Combine(directory.Path, "test" + RoutingSnapshotStore.Extension)));
            Assert.False(session.Peers.Find(target.NodeId)!.InRoutingTable);

            await session.StartAsync(timeout.Token);
            var restored = await session.RestoreRoutingTableAsync(timeout.Token);

            Assert.Equal(1, restored);
            Assert.True(session.RoutingTable!.Contains(target.NodeId));
            Assert.True(session.Peers.Find(target.NodeId)!.InRoutingTable);
            Assert.Contains(sink.Snapshot(), entry => entry.Message.StartsWith("路由表恢复完成", StringComparison.Ordinal));
        }

        /// <summary>Content published by one session is resolved, downloaded with progress and verified by another.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task TransfersContentBetweenSessions()
        {
            using var timeout = TestNodes.Timeout();
            var sink = new LogSink();
            await using var publisher = TestNodes.CreateSession(sink, TestNodes.LoopbackSettings(), "发布者");
            await publisher.StartAsync(timeout.Token);
            await using var downloader = TestNodes.CreateSession(sink, TestNodes.LoopbackSettings(publisher.GetContactEndpoints()[0]), "下载者");
            await downloader.StartAsync(timeout.Token);
            await downloader.BootstrapAsync(timeout.Token);
            var payload = new byte[100_000];
            new Random(7).NextBytes(payload);
            using var source = new MemoryStream(payload);

            var record = await publisher.PublishAsync("tests/transfer.bin", source, 1, timeout.Token);
            var resolved = await downloader.ResolveAsync(record.Key, timeout.Token);
            var providers = await downloader.FindProvidersAsync(record.FileHash, timeout.Token);
            using var destination = new MemoryStream();
            var progress = new List<long>();
            await downloader.DownloadAsync(record.Key, destination, record.FileHash, new SynchronousProgress(progress), timeout.Token);

            Assert.Equal(record.FileHash, resolved!.FileHash);
            Assert.Contains(providers, provider => provider.NodeId == publisher.NodeId);
            Assert.Equal(payload, destination.ToArray());
            Assert.Equal(payload.Length, progress[^1]);
            Assert.Equal(RecordOrigin.Published, Assert.Single(publisher.Records).Origin);
            Assert.Equal(RecordOrigin.Downloaded, Assert.Single(downloader.Records).Origin);
            Assert.Contains(sink.Snapshot(), entry => entry.Category == "下载者" && entry.Message.StartsWith("下载：完成", StringComparison.Ordinal));
        }

        /// <summary>Withdrawing and stopping provision update the remembered records.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task TracksWithdrawalAndProvision()
        {
            using var timeout = TestNodes.Timeout();
            await using var session = TestNodes.CreateSession(new LogSink(), TestNodes.LoopbackSettings());
            await session.StartAsync(timeout.Token);
            using var source = new MemoryStream(Encoding.UTF8.GetBytes("withdraw me"));
            var record = await session.PublishAsync("tests/withdraw", source, 1, timeout.Token);

            Assert.True(session.StopProviding(record.FileHash));
            Assert.True(Assert.Single(session.Records).ProvidingStopped);

            var withdrawal = await session.UnpublishAsync("tests/withdraw", 0, timeout.Token);

            Assert.True(withdrawal.IsWithdrawn);
            Assert.Equal(RecordOrigin.Withdrawal, Assert.Single(session.Records).Origin);
        }

        /// <summary>Records progress synchronously on the reporting thread.</summary>
        private sealed class SynchronousProgress : IProgress<long>
        {
            /// <summary>The recorded values.</summary>
            private readonly List<long> _values;

            /// <summary>Creates the recorder.</summary>
            /// <param name="values">Receives the values.</param>
            internal SynchronousProgress(List<long> values)
            {
                _values = values;
            }

            /// <summary>Records one value.</summary>
            /// <param name="value">The value.</param>
            public void Report(long value)
            {
                lock (_values)
                {
                    _values.Add(value);
                }
            }
        }
    }
}
