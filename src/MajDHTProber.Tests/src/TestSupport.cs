using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>A unique temporary directory that is deleted on disposal.</summary>
    internal sealed class TempDirectory : IDisposable
    {
        /// <summary>Creates the directory.</summary>
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "majdht-prober-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>Gets the directory path.</summary>
        internal string Path { get; }

        /// <summary>Deletes the directory and its content, ignoring failures.</summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leftover temporary files do not affect other tests.
            }
        }
    }

    /// <summary>Creates nodes, settings and services for tests on loopback sockets without identity work.</summary>
    internal static class TestNodes
    {
        /// <summary>The per-test deadline of socket tests.</summary>
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

        /// <summary>Creates a cancellation source that ends a socket test after <see cref="Deadline"/>.</summary>
        /// <returns>The cancellation source.</returns>
        internal static CancellationTokenSource Timeout()
        {
            return new CancellationTokenSource(Deadline);
        }

        /// <summary>Creates loopback options without identity work.</summary>
        /// <param name="capabilities">The node capabilities.</param>
        /// <param name="bootstrap">Optional bootstrap endpoints.</param>
        /// <returns>The options.</returns>
        internal static DHTNodeOptions LoopbackOptions(DHTCapabilities capabilities = DHTCapabilities.Download | DHTCapabilities.Provide,
            params IPEndPoint[] bootstrap)
        {
            return new DHTNodeOptions
            {
                ListenAddresses = new List<IPAddress> { IPAddress.Loopback },
                TcpPort = 0,
                UdpPort = 0,
                Capabilities = capabilities,
                IdentityWorkBits = 0,
                EnableNatTraversal = false,
                RequestTimeout = TimeSpan.FromSeconds(2),
                UdpAttempts = 2,
                BootstrapEndpoints = new List<IPEndPoint>(bootstrap),
            };
        }

        /// <summary>Starts a loopback node with a fresh identity.</summary>
        /// <param name="capabilities">The node capabilities.</param>
        /// <returns>The started node; the caller disposes it.</returns>
        internal static async Task<MajDHTNode> StartAsync(DHTCapabilities capabilities = DHTCapabilities.Download | DHTCapabilities.Provide)
        {
            var node = new MajDHTNode(LoopbackOptions(capabilities), new DHTIdentity());
            await node.StartAsync();
            return node;
        }

        /// <summary>Returns the TCP endpoint a loopback node advertises.</summary>
        /// <param name="node">The started node.</param>
        /// <returns>The TCP endpoint.</returns>
        internal static IPEndPoint TcpEndpoint(MajDHTNode node)
        {
            var endpoint = node.LocalPeer.Endpoints[0];
            return new IPEndPoint(endpoint.Address, endpoint.TcpPort);
        }

        /// <summary>Returns the UDP endpoint a loopback node advertises.</summary>
        /// <param name="node">The started node.</param>
        /// <returns>The UDP endpoint.</returns>
        internal static IPEndPoint UdpEndpoint(MajDHTNode node)
        {
            var endpoint = node.LocalPeer.Endpoints[0];
            return new IPEndPoint(endpoint.Address, endpoint.UdpPort);
        }

        /// <summary>Creates prober settings for a loopback session without identity work.</summary>
        /// <param name="bootstrap">Optional bootstrap endpoints.</param>
        /// <returns>The settings.</returns>
        internal static ProberSettings LoopbackSettings(params IPEndPoint[] bootstrap)
        {
            var settings = new ProberSettings
            {
                ListenAddresses = "127.0.0.1",
                TcpPort = 0,
                UdpPort = 0,
                IdentityWorkBits = 0,
                PersistIdentity = false,
                EnableNatTraversal = false,
                RequestTimeoutSeconds = 2,
                UdpAttempts = 2,
                MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Debug,
            };
            foreach (var endpoint in bootstrap)
            {
                settings.BootstrapEndpoints.Add(EndpointParser.Format(endpoint));
            }
            return settings;
        }

        /// <summary>Creates probe options for loopback tests.</summary>
        /// <returns>The probe options.</returns>
        internal static ProbeOptions ProbeOptions()
        {
            return new ProbeOptions
            {
                IdentityWorkBits = 0,
                PersistIdentity = false,
                RequestTimeout = TimeSpan.FromSeconds(1),
                UdpAttempts = 1,
            };
        }

        /// <summary>Creates a session with memory-only identities.</summary>
        /// <param name="sink">The log sink.</param>
        /// <param name="settings">The session settings.</param>
        /// <param name="label">The session label.</param>
        /// <returns>The stopped session.</returns>
        internal static NodeSession CreateSession(LogSink sink, ProberSettings settings, string label = "测试节点")
        {
            var vault = new IdentityVault(null, sink.CreateLogger("身份"));
            return new NodeSession(label, "test", NodeSessionRole.Primary, settings, vault, new EndpointProber(vault, sink), sink);
        }

        /// <summary>Waits until a condition holds.</summary>
        /// <param name="condition">The condition.</param>
        /// <param name="cancellationToken">Ends waiting.</param>
        /// <returns>Completion once the condition holds.</returns>
        /// <exception cref="OperationCanceledException">The condition did not hold before cancellation.</exception>
        internal static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (!condition())
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
