using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Services;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Probes real loopback nodes over TCP and UDP.</summary>
    public sealed class EndpointProberTests
    {
        /// <summary>A live node answers the signed Hello and the encrypted Ping over each transport.</summary>
        /// <param name="transport">The probed transport.</param>
        /// <returns>Completion after the probe.</returns>
        [Theory]
        [InlineData(DHTTransport.Tcp)]
        [InlineData(DHTTransport.Udp)]
        public async Task ProbesLiveNode(DHTTransport transport)
        {
            using var timeout = TestNodes.Timeout();
            await using var target = await TestNodes.StartAsync();
            var sink = new LogSink();
            var vault = new IdentityVault(null, sink.CreateLogger("身份"));
            var prober = new EndpointProber(vault, sink);
            var endpoint = transport == DHTTransport.Tcp ? TestNodes.TcpEndpoint(target) : TestNodes.UdpEndpoint(target);

            var result = await prober.ProbeAsync(endpoint, transport, TestNodes.ProbeOptions(), timeout.Token);

            Assert.True(result.Succeeded, result.Error);
            Assert.True(result.PingSucceeded, result.Error);
            Assert.Null(result.Error);
            Assert.False(result.IsPrivate);
            Assert.Equal(transport, result.Transport);
            Assert.Equal(target.NodeId, result.NodeId);
            Assert.Equal(target.NodeId, result.Peer?.NodeId);
            Assert.NotNull(result.HelloRoundTrip);
            Assert.NotNull(result.PingRoundTrip);
            Assert.Equal(IPAddress.Loopback, result.ObservedEndpoint?.Address);
            Assert.True(result.Transmissions >= 2);
            Assert.Equal(0, result.LostTransmissions);
            Assert.DoesNotContain(target.KnownPeers, peer => peer.NodeId == vault.TryGetCachedNodeId(EndpointProber.IdentityName));
            Assert.Contains(sink.Snapshot(), entry => entry.Category == "探测" && entry.Message.Contains("成功", StringComparison.Ordinal));
        }

        /// <summary>A closed endpoint is reported as a failed probe instead of an exception.</summary>
        /// <returns>Completion after the probe.</returns>
        [Fact]
        public async Task ReportsClosedEndpoint()
        {
            using var timeout = TestNodes.Timeout();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var sink = new LogSink();
            var prober = new EndpointProber(new IdentityVault(null, sink.CreateLogger("身份")), sink);

            var result = await prober.ProbeAsync(new IPEndPoint(IPAddress.Loopback, port), DHTTransport.Tcp, TestNodes.ProbeOptions(), timeout.Token);

            Assert.False(result.Succeeded);
            Assert.False(result.PingSucceeded);
            Assert.Null(result.NodeId);
            Assert.Null(result.Peer);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.True(result.LostTransmissions > 0);
            Assert.True(result.Elapsed < TimeSpan.FromSeconds(20));
        }

        /// <summary>A short-lived client joins through an endpoint and downloads published content.</summary>
        /// <returns>Completion after the download.</returns>
        [Fact]
        public async Task ClientJoinsAndDownloads()
        {
            using var timeout = TestNodes.Timeout();
            await using var target = await TestNodes.StartAsync();
            using var source = new MemoryStream(Encoding.UTF8.GetBytes("client download"));
            var record = await target.PublishAsync("tests/client", source, cancellationToken: timeout.Token);
            var sink = new LogSink();
            var prober = new EndpointProber(new IdentityVault(null, sink.CreateLogger("身份")), sink);

            await using var client = await prober.StartClientAsync(TestNodes.TcpEndpoint(target), DHTTransport.Tcp, TestNodes.ProbeOptions(), timeout.Token);
            using var destination = new MemoryStream();
            await client.Node.DownloadAsync(record.Key, destination, record.FileHash, timeout.Token);

            Assert.Equal("client download", Encoding.UTF8.GetString(destination.ToArray()));
            Assert.True(client.RoutingTable.Contains(target.NodeId));
        }
    }
}
