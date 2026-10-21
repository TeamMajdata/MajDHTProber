using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>
    /// Covers LAN discovery against real loopback nodes. Loopback sockets do not receive broadcasts, so the scans send the
    /// Hello to explicit targets; the broadcast address computation is checked separately.
    /// </summary>
    public sealed class LanDiscoveryTests
    {
        /// <summary>Every node that answers the Hello is listed once with its endpoint, round trip and observed source.</summary>
        /// <returns>Completion after the scan.</returns>
        [Fact]
        public async Task FindsAnsweringNodes()
        {
            using var timeout = TestNodes.Timeout();
            await using var first = await TestNodes.StartAsync();
            await using var second = await TestNodes.StartAsync(DHTCapabilities.Download | DHTCapabilities.Forwarder);
            var closed = ClosedUdpPort();
            var sink = new LogSink();
            var vault = new IdentityVault(null, sink.CreateLogger("身份"));
            var discovery = new LanDiscovery(vault, sink);
            var options = new LanScanOptions
            {
                Targets = new[] { TestNodes.UdpEndpoint(first), TestNodes.UdpEndpoint(second), closed },
                ResponseWindow = TimeSpan.FromSeconds(1),
                Probe = TestNodes.ProbeOptions(),
            };

            var result = await discovery.ScanAsync(options, timeout.Token);

            // The nodes try to verify the scanner with a Ping of their own, which it ignores, so it never becomes a routing contact.
            Assert.DoesNotContain(first.KnownPeers, peer => peer.NodeId == vault.TryGetCachedNodeId(EndpointProber.IdentityName));
            Assert.Equal(3, result.Targets.Count);
            Assert.Equal(2, result.Peers.Count);
            Assert.Equal(0, result.InvalidReplies);
            Assert.True(result.Elapsed >= options.ResponseWindow);
            var found = Assert.Single(result.Peers, peer => peer.NodeId == second.NodeId);
            Assert.Equal(TestNodes.UdpEndpoint(second), found.Endpoint);
            Assert.True(found.Peer.WasObservedAt(DHTTransport.Udp, found.Endpoint));
            Assert.Equal(DHTCapabilities.Download | DHTCapabilities.Forwarder, found.Peer.Capabilities);
            Assert.True(found.RoundTrip > TimeSpan.Zero);
            Assert.Equal(1, found.Replies);
            Assert.Equal(IPAddress.Loopback, found.ObservedEndpoint.Address);
            Assert.Contains(result.Peers, peer => peer.NodeId == first.NodeId);
            Assert.Contains(sink.Snapshot(), entry => entry.Category == "局域网扫描" && entry.Message.StartsWith("扫描完成", StringComparison.Ordinal));
        }

        /// <summary>Datagrams that are not signed answers to the broadcast Hello are counted and ignored.</summary>
        /// <returns>Completion after the scan.</returns>
        [Fact]
        public async Task IgnoresInvalidReplies()
        {
            using var timeout = TestNodes.Timeout();
            using var impostor = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var echo = Task.Run(async () =>
            {
                var received = await impostor.ReceiveAsync(timeout.Token);
                var garbage = new byte[64];
                new Random(3).NextBytes(garbage);
                await impostor.SendAsync(garbage, received.RemoteEndPoint, timeout.Token);
            }, timeout.Token);
            var sink = new LogSink();
            var discovery = new LanDiscovery(new IdentityVault(null, sink.CreateLogger("身份")), sink);
            var options = new LanScanOptions
            {
                Targets = new[] { (IPEndPoint)impostor.Client.LocalEndPoint! },
                ResponseWindow = TimeSpan.FromSeconds(1),
                Probe = TestNodes.ProbeOptions(),
            };

            var result = await discovery.ScanAsync(options, timeout.Token);
            await echo;

            Assert.Empty(result.Peers);
            Assert.Equal(1, result.InvalidReplies);
        }

        /// <summary>A session records the answering nodes as LAN discoveries but leaves itself out.</summary>
        /// <returns>Completion after the scan.</returns>
        [Fact]
        public async Task SessionRecordsScannedNodes()
        {
            using var timeout = TestNodes.Timeout();
            await using var remote = await TestNodes.StartAsync();
            await using var session = TestNodes.CreateSession(new LogSink(), TestNodes.LoopbackSettings());
            await session.StartAsync(timeout.Token);
            var options = session.CreateLanScanOptions(null) with
            {
                Targets = new[] { TestNodes.UdpEndpoint(remote), session.GetContactEndpoints(DHTTransport.Udp)[0] },
                ResponseWindow = TimeSpan.FromSeconds(1),
            };

            var result = await session.ScanLanAsync(options, timeout.Token);

            Assert.Equal(2, result.Peers.Count);
            Assert.Contains(result.Peers, peer => peer.NodeId == session.NodeId);
            var recorded = Assert.Single(session.Peers.Snapshot());
            Assert.Equal(remote.NodeId, recorded.NodeId);
            Assert.Equal(PeerSource.Lan, recorded.Sources);
            Assert.False(recorded.InRoutingTable);
            Assert.Equal(PeerConnectionState.Discovered, recorded.GetState(DateTimeOffset.UtcNow));
            Assert.True(recorded.Peer.WasObservedAt(DHTTransport.Udp, TestNodes.UdpEndpoint(remote)));
        }

        /// <summary>Scan options default to the session's UDP port and fall back to the default port for assigned or disabled UDP.</summary>
        [Fact]
        public void DerivesScanOptionsFromSettings()
        {
            var settings = TestNodes.LoopbackSettings();
            settings.UdpPort = 43210;
            var session = TestNodes.CreateSession(new LogSink(), settings);

            Assert.Equal(43210, session.CreateLanScanOptions(null).Port);
            Assert.Equal(5000, session.CreateLanScanOptions(5000).Port);
            Assert.Equal(0, session.CreateLanScanOptions(null).Probe.IdentityWorkBits);

            settings.UdpPort = 0;
            session.Settings = settings;
            Assert.Equal(ProberSettings.DefaultPort, session.CreateLanScanOptions(null).Port);

            settings.UdpPort = 43210;
            settings.UdpEnabled = false;
            session.Settings = settings;
            Assert.Equal(ProberSettings.DefaultPort, session.CreateLanScanOptions(null).Port);
        }

        /// <summary>Broadcast targets are distinct IPv4 subnet broadcast addresses carrying the scanned port.</summary>
        [Fact]
        public void ComputesBroadcastTargets()
        {
            var targets = LanDiscovery.BroadcastTargets(42000);

            Assert.NotEmpty(targets);
            Assert.All(targets, target => Assert.Equal(AddressFamily.InterNetwork, target.AddressFamily));
            Assert.All(targets, target => Assert.Equal(42000, target.Port));
            Assert.All(targets, target => Assert.NotEqual(169, target.Address.GetAddressBytes()[0]));
            Assert.Equal(targets.Count, targets.Select(target => target.Address).Distinct().Count());
            Assert.Throws<ArgumentOutOfRangeException>(() => LanDiscovery.BroadcastTargets(0));
        }

        /// <summary>A non-positive response window is rejected before anything is sent.</summary>
        /// <returns>Completion after the check.</returns>
        [Fact]
        public async Task RejectsInvalidWindow()
        {
            var sink = new LogSink();
            var discovery = new LanDiscovery(new IdentityVault(null, sink.CreateLogger("身份")), sink);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => discovery.ScanAsync(new LanScanOptions { ResponseWindow = TimeSpan.Zero },
                TestContext.Current.CancellationToken));
        }

        /// <summary>The frame codec follows the documented layout: header, digests, cookies and envelopes at fixed offsets.</summary>
        [Fact]
        public void EncodesTransportFrames()
        {
            var digest = new byte[32];
            var cookie = new byte[32];
            var envelope = new byte[] { 1, 2, 3, 4 };
            new Random(1).NextBytes(digest);
            new Random(2).NextBytes(cookie);

            var hello = UdpFrames.Hello(digest);
            Assert.Equal(69, hello.Length);
            Assert.Equal(new byte[] { 0x4d, 0x44, 0x55, 1, 0 }, hello[..5]);
            Assert.Equal(digest, hello[5..37]);
            Assert.All(hello[37..], padding => Assert.Equal(0, padding));

            var challenge = new byte[69];
            challenge[0] = 0x4d;
            challenge[1] = 0x44;
            challenge[2] = 0x55;
            challenge[3] = 1;
            challenge[4] = 1;
            cookie.CopyTo(challenge, 5);
            digest.CopyTo(challenge, 37);
            Assert.True(UdpFrames.TryReadChallenge(challenge, digest, out var readCookie));
            Assert.Equal(cookie, readCookie);
            Assert.False(UdpFrames.TryReadChallenge(challenge, new byte[32], out _));
            Assert.False(UdpFrames.TryReadChallenge(challenge[..68], digest, out _));

            var request = UdpFrames.Request(cookie, envelope);
            Assert.Equal(2, request[4]);
            Assert.Equal(cookie, request[5..37]);
            Assert.Equal(envelope, request[37..]);

            var response = new byte[37 + envelope.Length];
            request.AsSpan(0, 37).CopyTo(response);
            response[4] = 4;
            digest.CopyTo(response, 5);
            envelope.CopyTo(response, 37);
            Assert.True(UdpFrames.TryReadResponse(response, digest, out var payload));
            Assert.Equal(envelope, payload.ToArray());
            Assert.False(UdpFrames.TryReadResponse(response, new byte[32], out _));
            Assert.False(UdpFrames.TryReadResponse(hello, digest, out _));
            Assert.Throws<ArgumentException>(() => UdpFrames.Hello(new byte[31]));
            Assert.Throws<ArgumentException>(() => UdpFrames.Request(cookie, Array.Empty<byte>()));
        }

        /// <summary>Finds a loopback UDP port that nothing listens on.</summary>
        /// <returns>The closed endpoint.</returns>
        private static IPEndPoint ClosedUdpPort()
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return (IPEndPoint)socket.Client.LocalEndPoint!;
        }
    }
}
