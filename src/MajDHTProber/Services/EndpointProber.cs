using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Tunes endpoint probes and short-lived client nodes.</summary>
    public sealed record ProbeOptions
    {
        /// <summary>Gets the timeout of each transport attempt.</summary>
        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>Gets the total UDP transmissions per request.</summary>
        public int UdpAttempts { get; init; } = 2;

        /// <summary>Gets the identity work of the probe identity; it must reach the probed network's requirement.</summary>
        public int IdentityWorkBits { get; init; } = 12;

        /// <summary>Gets whether the probe identity is stored on disk so it is generated only once.</summary>
        public bool PersistIdentity { get; init; } = true;

        /// <summary>Creates probe options that match the prober settings.</summary>
        /// <param name="settings">The prober settings.</param>
        /// <returns>The probe options.</returns>
        public static ProbeOptions From(ProberSettings settings)
        {
            return new ProbeOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 0.05, 120)),
                UdpAttempts = Math.Clamp(settings.UdpAttempts, 1, 5),
                IdentityWorkBits = Math.Clamp(settings.IdentityWorkBits, 0, 20),
                PersistIdentity = settings.PersistIdentity,
            };
        }
    }

    /// <summary>A started short-lived client node together with its tracking routing table.</summary>
    public sealed class EphemeralNode : IAsyncDisposable
    {
        /// <summary>Wraps a started node.</summary>
        /// <param name="node">The started node.</param>
        /// <param name="routingTable">The node's tracking routing table.</param>
        internal EphemeralNode(MajDHTNode node, TrackingRoutingTable routingTable)
        {
            Node = node;
            RoutingTable = routingTable;
        }

        /// <summary>Gets the started node.</summary>
        public MajDHTNode Node { get; }

        /// <summary>Gets the node's tracking routing table.</summary>
        public TrackingRoutingTable RoutingTable { get; }

        /// <summary>Stops the node and releases its sockets.</summary>
        /// <returns>Completion after shutdown.</returns>
        public ValueTask DisposeAsync()
        {
            return Node.DisposeAsync();
        }
    }

    /// <summary>
    /// Probes node endpoints from short-lived client nodes that use a dedicated probe identity, so probing never changes the
    /// hosted nodes' routing state. A probe performs the library's signed Hello exchange with the endpoint, then sends one
    /// encrypted lookup request to the advertised endpoints of whichever node answered.
    /// </summary>
    /// <remarks>
    /// The library has no public ping operation; a bootstrap of a fresh client is its closest public equivalent. The client
    /// listens on a wildcard address without advertising any endpoint, so remote nodes answer it but never add it to their
    /// routing tables.
    /// </remarks>
    public sealed class EndpointProber
    {
        /// <summary>The name of the shared probe identity in the identity vault.</summary>
        public const string IdentityName = "probe";

        /// <summary>The identity vault providing the probe identity.</summary>
        private readonly IdentityVault _vault;
        /// <summary>The logger handed to probe client nodes.</summary>
        private readonly ILogger _clientLogger;
        /// <summary>The prober diagnostics logger.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a prober.</summary>
        /// <param name="vault">The identity vault providing the probe identity.</param>
        /// <param name="sink">The log sink receiving probe diagnostics.</param>
        public EndpointProber(IdentityVault vault, LogSink sink)
        {
            _vault = vault;
            _clientLogger = sink.CreateLogger("探针节点");
            _logger = sink.CreateLogger("探测");
        }

        /// <summary>Probes one endpoint.</summary>
        /// <param name="endpoint">The endpoint to probe.</param>
        /// <param name="transport">The transport tried first; the library falls back to the other transport.</param>
        /// <param name="options">The probe tuning.</param>
        /// <param name="cancellationToken">Cancels the probe.</param>
        /// <returns>The probe outcome; failures to reach the endpoint are reported in the result rather than thrown.</returns>
        /// <exception cref="OperationCanceledException">The probe was cancelled.</exception>
        /// <exception cref="IOException">The probe identity could not be stored.</exception>
        public async Task<EndpointProbeResult> ProbeAsync(IPEndPoint endpoint, DHTTransport transport, ProbeOptions options,
            CancellationToken cancellationToken = default)
        {
            var identity = await _vault.GetOrCreateAsync(IdentityName, options.IdentityWorkBits, options.PersistIdentity, cancellationToken)
                .ConfigureAwait(false);
            var target = EndpointParser.Format(endpoint);
            _logger.LogDebug("开始探测 {Endpoint}（优先 {Transport}）。", target, transport);
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var capture = new HelloCapture(endpoint.Address, stopwatch);
            var table = new TrackingRoutingTable();
            table.Observed += capture.OnObserved;
            table.Removed += capture.OnRemoved;
            var node = new MajDHTNode(CreateOptions(endpoint, transport, options, true), identity,
                new MemoryDHTContentStore(1024 * 1024, 256), table);
            string? error = null;
            var bootstrapped = false;
            TimeSpan? finishedAt = null;
            DHTPeerStatistics? statistics;
            try
            {
                await node.StartAsync(cancellationToken).ConfigureAwait(false);
                await node.BootstrapAsync(cancellationToken).ConfigureAwait(false);
                bootstrapped = true;
                finishedAt = stopwatch.Elapsed;
            }
            catch (IOException)
            {
                error = "端点未响应 Hello 请求（TCP 与 UDP 均超时、被拒绝或认证失败）。";
            }
            catch (SocketException exception)
            {
                error = "本地套接字错误：" + exception.SocketErrorCode + "。";
            }
            catch (Exception exception) when (exception is InvalidDataException or CryptographicException or ArgumentException)
            {
                error = "握手数据无效：" + exception.GetType().Name + "。";
            }
            finally
            {
                var answered = capture.Read();
                statistics = answered.NodeId == null ? null : node.GetPeerStatistics(answered.NodeId);
                await node.DisposeAsync().ConfigureAwait(false);
            }
            var result = Compose(endpoint, transport, startedAt, stopwatch.Elapsed, capture.Read(), statistics, bootstrapped, finishedAt, error);
            if (result.Succeeded)
            {
                _logger.LogInformation("探测 {Endpoint} 成功：节点 {NodeId}，Hello {Rtt:F1} ms，加密请求{Rpc}。", target,
                    ShortId(result.NodeId), result.HelloRoundTrip?.TotalMilliseconds ?? 0, result.RpcSucceeded ? "成功" : "未通过");
            }
            else
            {
                _logger.LogWarning("探测 {Endpoint} 失败：{Error}", target, result.Error);
            }
            return result;
        }

        /// <summary>
        /// Starts a short-lived download-only client that joins the network through one endpoint, for end-to-end tests from
        /// an independent vantage point.
        /// </summary>
        /// <param name="bootstrap">The endpoint the client joins through.</param>
        /// <param name="transport">The preferred transport.</param>
        /// <param name="options">The client tuning.</param>
        /// <param name="cancellationToken">Cancels startup and joining.</param>
        /// <returns>The started, bootstrapped client; the caller disposes it.</returns>
        /// <exception cref="IOException">The endpoint did not answer.</exception>
        /// <exception cref="OperationCanceledException">Startup was cancelled.</exception>
        public async Task<EphemeralNode> StartClientAsync(IPEndPoint bootstrap, DHTTransport transport, ProbeOptions options,
            CancellationToken cancellationToken = default)
        {
            var identity = await _vault.GetOrCreateAsync(IdentityName, options.IdentityWorkBits, options.PersistIdentity, cancellationToken)
                .ConfigureAwait(false);
            var table = new TrackingRoutingTable();
            var node = new MajDHTNode(CreateOptions(bootstrap, transport, options, false), identity, new MemoryDHTContentStore(), table);
            try
            {
                await node.StartAsync(cancellationToken).ConfigureAwait(false);
                await node.BootstrapAsync(cancellationToken).ConfigureAwait(false);
                return new EphemeralNode(node, table);
            }
            catch
            {
                await node.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Creates the options of a probe or client node.</summary>
        /// <param name="bootstrap">The single bootstrap endpoint.</param>
        /// <param name="transport">The preferred transport.</param>
        /// <param name="options">The probe tuning.</param>
        /// <param name="minimalLookup">Whether the follow-up lookup queries only the answering node.</param>
        /// <returns>The node options.</returns>
        internal DHTNodeOptions CreateOptions(IPEndPoint bootstrap, DHTTransport transport, ProbeOptions options, bool minimalLookup)
        {
            var wildcard = bootstrap.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            return new DHTNodeOptions
            {
                ListenAddresses = new List<IPAddress> { wildcard },
                TcpPort = 0,
                UdpPort = 0,
                PreferredTransport = transport,
                Capabilities = DHTCapabilities.Download,
                BootstrapEndpoints = new List<IPEndPoint> { bootstrap },
                EnableNatTraversal = false,
                BucketSize = minimalLookup ? 1 : 8,
                MaxLookupQueries = minimalLookup ? 1 : 64,
                LookupParallelism = minimalLookup ? 1 : 3,
                DisjointLookupPaths = minimalLookup ? 1 : 3,
                MaintenanceInterval = TimeSpan.FromHours(1),
                IdentityWorkBits = options.IdentityWorkBits,
                RequestTimeout = options.RequestTimeout,
                UdpAttempts = options.UdpAttempts,
                Logger = _clientLogger,
            };
        }

        /// <summary>Combines the captured events and statistics into a result.</summary>
        /// <param name="endpoint">The probed endpoint.</param>
        /// <param name="transport">The transport tried first.</param>
        /// <param name="startedAt">When the probe started.</param>
        /// <param name="elapsed">The total probe duration.</param>
        /// <param name="answered">The captured Hello and lookup events.</param>
        /// <param name="statistics">The probe client's statistics for the answering node.</param>
        /// <param name="bootstrapped">Whether the bootstrap call completed.</param>
        /// <param name="finishedAt">When the bootstrap call completed, measured from the start.</param>
        /// <param name="error">The failure description, or null.</param>
        /// <returns>The probe result.</returns>
        private static EndpointProbeResult Compose(IPEndPoint endpoint, DHTTransport transport, DateTimeOffset startedAt, TimeSpan elapsed,
            HelloCapture.Result answered, DHTPeerStatistics? statistics, bool bootstrapped, TimeSpan? finishedAt, string? error)
        {
            var routes = statistics?.Routes ?? (IReadOnlyList<DHTRouteStatistics>)Array.Empty<DHTRouteStatistics>();
            var requestedAnswered = false;
            foreach (var route in routes)
            {
                requestedAnswered |= route.Transport == transport && route.SentPackets > route.LostPackets;
            }
            var helloSucceeded = bootstrapped || answered.HelloAt.HasValue;
            var rpcSucceeded = bootstrapped && answered.Peer != null && !answered.PeerRemoved;
            return new EndpointProbeResult
            {
                Endpoint = endpoint,
                RequestedTransport = transport,
                StartedAt = startedAt,
                Elapsed = elapsed,
                HelloSucceeded = helloSucceeded,
                HelloRoundTrip = answered.HelloAt,
                RpcSucceeded = rpcSucceeded,
                RpcRoundTrip = rpcSucceeded && finishedAt.HasValue && answered.HelloAt.HasValue ? finishedAt - answered.HelloAt : null,
                RequestedTransportAnswered = requestedAnswered,
                Peer = answered.Peer,
                NodeId = answered.NodeId,
                IsPrivate = answered.Peer == null && answered.NodeId != null,
                Routes = routes,
                Error = helloSucceeded ? null : error ?? "端点未响应。",
            };
        }

        /// <summary>Shortens a node identifier for log messages.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>The first twelve characters, or a dash.</returns>
        private static string ShortId(string? nodeId)
        {
            return nodeId == null ? "-" : nodeId.Length <= 12 ? nodeId : nodeId.Substring(0, 12);
        }

        /// <summary>
        /// Captures, from the probe client's routing table, which node answered the Hello at the probed address and whether
        /// the follow-up lookup request to it failed.
        /// </summary>
        private sealed class HelloCapture
        {
            /// <summary>Protects the captured values.</summary>
            private readonly Lock _gate = new Lock();
            /// <summary>The probed address, which the node reports as the verified address of the Hello answer.</summary>
            private readonly IPAddress _address;
            /// <summary>Measures elapsed time since the probe started.</summary>
            private readonly Stopwatch _stopwatch;
            /// <summary>The answering contact, when the node retained it.</summary>
            private DHTPeer? _peer;
            /// <summary>The answering node's identifier.</summary>
            private string? _nodeId;
            /// <summary>When the Hello answer was authenticated.</summary>
            private TimeSpan? _helloAt;
            /// <summary>Whether the client dropped the answering node after a failed request.</summary>
            private bool _peerRemoved;

            /// <summary>Creates a capture for one probed address.</summary>
            /// <param name="address">The probed address.</param>
            /// <param name="stopwatch">The running probe stopwatch.</param>
            internal HelloCapture(IPAddress address, Stopwatch stopwatch)
            {
                _address = address;
                _stopwatch = stopwatch;
            }

            /// <summary>Records the first contact observed at the probed address.</summary>
            /// <param name="sender">The routing table.</param>
            /// <param name="observation">The observation.</param>
            internal void OnObserved(object? sender, RoutingObservation observation)
            {
                lock (_gate)
                {
                    if (_peer == null && _nodeId == null && _address.Equals(observation.VerifiedAddress))
                    {
                        _peer = observation.Peer;
                        _nodeId = observation.Peer.NodeId;
                        _helloAt = _stopwatch.Elapsed;
                    }
                }
            }

            /// <summary>
            /// Records a removal: before any observation it is a private node answering the Hello, afterwards it is the
            /// failure of the follow-up request.
            /// </summary>
            /// <param name="sender">The routing table.</param>
            /// <param name="nodeId">The removed node.</param>
            internal void OnRemoved(object? sender, string nodeId)
            {
                lock (_gate)
                {
                    if (_nodeId == null)
                    {
                        _nodeId = nodeId;
                        _helloAt = _stopwatch.Elapsed;
                    }
                    else if (_peer != null && _peer.NodeId == nodeId)
                    {
                        _peerRemoved = true;
                    }
                }
            }

            /// <summary>Returns a consistent copy of the captured values.</summary>
            /// <returns>The captured values.</returns>
            internal Result Read()
            {
                lock (_gate)
                {
                    return new Result(_peer, _nodeId, _helloAt, _peerRemoved);
                }
            }

            /// <summary>A consistent copy of the captured values.</summary>
            /// <param name="Peer">The answering contact, when retained.</param>
            /// <param name="NodeId">The answering node's identifier.</param>
            /// <param name="HelloAt">When the Hello answer was authenticated.</param>
            /// <param name="PeerRemoved">Whether the follow-up request failed.</param>
            internal readonly record struct Result(DHTPeer? Peer, string? NodeId, TimeSpan? HelloAt, bool PeerRemoved);
        }
    }
}
