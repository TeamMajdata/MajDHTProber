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
using MajDHTNetwork.Protocol;
using MajDHTNetwork.Transport;
using MajDHTProber.Infrastructure;
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
    /// Probes node endpoints through the library's public protocol API. A probe binds a transport of its own under a
    /// dedicated probe identity, sends the signed Hello request that learns the signed contact listening at the endpoint,
    /// and then sends one encrypted Ping request to the same endpoint over the same transport, timing both exchanges.
    /// </summary>
    /// <remarks>
    /// The probe answers no request, so remote nodes answer it but never use it as a routing contact. Probing never
    /// involves the hosted nodes, so it never changes their routing state either.
    /// </remarks>
    public sealed class EndpointProber
    {
        /// <summary>The name of the shared probe identity in the identity vault.</summary>
        public const string IdentityName = "probe";

        /// <summary>The lease of the probe's own signed contact, which only has to outlive one probe.</summary>
        private static readonly TimeSpan s_contactLifetime = TimeSpan.FromHours(1);

        /// <summary>The identity vault providing the probe identity.</summary>
        private readonly IdentityVault _vault;
        /// <summary>The logger handed to probe transports and client nodes.</summary>
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

        /// <summary>Probes one endpoint over one transport with a signed Hello followed by an encrypted Ping.</summary>
        /// <param name="endpoint">The endpoint to probe.</param>
        /// <param name="transport">The transport; the probe never falls back to the other transport.</param>
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
            _logger.LogDebug("开始探测 {Endpoint}（{Transport}）。", target, Display.Transport(transport));
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var counter = new TransmissionCounter();
            HelloOutcome? hello = null;
            var ping = PingOutcome.NotAttempted;
            string? error = null;
            await using (var socket = new SocketTransport(CreateProbeOptions(endpoint, transport, options), RejectInbound, _clientLogger))
            {
                try
                {
                    socket.Bind();
                    var self = DHTContact.Create(identity, DHTCapabilities.Download, DHTReachability.Unknown, false, Array.Empty<DHTPeer>(),
                        s_contactLifetime);
                    socket.Run();
                    hello = await HelloAsync(socket, identity, self, endpoint, transport, counter, cancellationToken).ConfigureAwait(false);
                    if (options.IdentityWorkBits > 0 && hello.Peer.IdentityWork < options.IdentityWorkBits)
                    {
                        error = "应答节点的身份工作量为 " + hello.Peer.IdentityWork + " 位，低于要求的 " + options.IdentityWorkBits + " 位，已跳过加密 Ping。";
                    }
                    else
                    {
                        ping = await PingAsync(socket, identity, self, hello.Peer, endpoint, transport, counter, cancellationToken).ConfigureAwait(false);
                        error = ping.Error;
                    }
                }
                catch (IOException exception)
                {
                    error = "端点未响应 Hello 请求（" + Display.Transport(transport) + " 超时或被拒绝）。";
                    _logger.LogDebug(exception, "Hello 交换失败。");
                }
                catch (SocketException exception)
                {
                    error = "本地套接字错误：" + exception.SocketErrorCode + "。";
                }
                catch (Exception exception) when (exception is InvalidDataException or CryptographicException or ArgumentException)
                {
                    error = "Hello 响应无效：" + exception.GetType().Name + "。";
                }
            }
            stopwatch.Stop();
            var result = new EndpointProbeResult
            {
                Endpoint = endpoint,
                Transport = transport,
                StartedAt = startedAt,
                Elapsed = stopwatch.Elapsed,
                HelloSucceeded = hello != null,
                HelloRoundTrip = hello?.RoundTrip,
                ObservedEndpoint = hello?.ObservedEndpoint,
                PingSucceeded = ping.Succeeded,
                PingRoundTrip = ping.RoundTrip,
                Peer = hello?.Peer,
                Transmissions = counter.Sent,
                LostTransmissions = counter.Lost,
                Error = error,
            };
            if (result.Succeeded)
            {
                _logger.LogInformation("探测 {Endpoint} 成功：节点 {NodeId}，Hello {Hello}，加密 Ping {Ping}。", target, Display.ShortId(result.NodeId),
                    Display.Duration(result.HelloRoundTrip), result.PingSucceeded ? Display.Duration(result.PingRoundTrip) : "失败");
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
            var node = new MajDHTNode(CreateClientOptions(bootstrap, transport, options), identity, new MemoryDHTContentStore(), table);
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

        /// <summary>Creates the options of a short-lived client node.</summary>
        /// <param name="bootstrap">The single bootstrap endpoint.</param>
        /// <param name="transport">The preferred transport.</param>
        /// <param name="options">The client tuning.</param>
        /// <returns>The node options: a wildcard listener on assigned ports without NAT traversal.</returns>
        internal DHTNodeOptions CreateClientOptions(IPEndPoint bootstrap, DHTTransport transport, ProbeOptions options)
        {
            return new DHTNodeOptions
            {
                ListenAddresses = new List<IPAddress> { WildcardFor(bootstrap) },
                TcpPort = 0,
                UdpPort = 0,
                PreferredTransport = transport,
                Capabilities = DHTCapabilities.Download,
                BootstrapEndpoints = new List<IPEndPoint> { bootstrap },
                EnableNatTraversal = false,
                MaintenanceInterval = TimeSpan.FromHours(1),
                IdentityWorkBits = options.IdentityWorkBits,
                RequestTimeout = options.RequestTimeout,
                UdpAttempts = options.UdpAttempts,
                Logger = _clientLogger,
            };
        }

        /// <summary>Creates the transport options of one probe: a wildcard listener of the probed transport on an assigned port.</summary>
        /// <param name="endpoint">The probed endpoint, which selects the address family.</param>
        /// <param name="transport">The probed transport.</param>
        /// <param name="options">The probe tuning.</param>
        /// <returns>The transport options.</returns>
        internal DHTNodeOptions CreateProbeOptions(IPEndPoint endpoint, DHTTransport transport, ProbeOptions options)
        {
            return new DHTNodeOptions
            {
                ListenAddresses = new List<IPAddress> { WildcardFor(endpoint) },
                TcpPort = transport == DHTTransport.Tcp ? 0 : null,
                UdpPort = transport == DHTTransport.Udp ? 0 : null,
                PreferredTransport = transport,
                Capabilities = DHTCapabilities.Download,
                EnableNatTraversal = false,
                IdentityWorkBits = options.IdentityWorkBits,
                RequestTimeout = options.RequestTimeout,
                UdpAttempts = options.UdpAttempts,
                Logger = _clientLogger,
            };
        }

        /// <summary>Sends the signed Hello request and authenticates the answer.</summary>
        /// <param name="socket">The bound and running probe transport.</param>
        /// <param name="identity">The probe identity.</param>
        /// <param name="self">The probe's signed contact.</param>
        /// <param name="endpoint">The probed endpoint.</param>
        /// <param name="transport">The probed transport.</param>
        /// <param name="counter">Counts the transmissions of the exchange.</param>
        /// <param name="cancellationToken">Cancels the exchange.</param>
        /// <returns>
        /// The answering contact at the probed endpoint, the round trip and the endpoint the responder observed for the probe.
        /// </returns>
        /// <exception cref="IOException">The endpoint did not answer.</exception>
        /// <exception cref="CryptographicException">The answer was forged, miscorrelated or sent by the probe identity itself.</exception>
        /// <exception cref="InvalidDataException">The answer is malformed.</exception>
        /// <exception cref="OperationCanceledException">The exchange was cancelled.</exception>
        private static async Task<HelloOutcome> HelloAsync(SocketTransport socket, DHTIdentity identity, DHTContact self, IPEndPoint endpoint,
            DHTTransport transport, TransmissionCounter counter, CancellationToken cancellationToken)
        {
            var request = new Packet(self, Crypto.Random(Packet.RequestIdLength), (byte)Operation.Hello, ReadOnlyMemory<byte>.Empty);
            var envelope = request.SealHello(identity);
            var stopwatch = Stopwatch.StartNew();
            using var encoded = await socket.ExchangeAtAsync(transport, endpoint, envelope, cancellationToken, counter).ConfigureAwait(false);
            stopwatch.Stop();
            // The contact and the observed endpoint are owned copies, so the frame is released when this method ends.
            var response = Packet.Open(identity, encoded.Bytes);
            VerifyResponse(response, request, identity, null);
            using var reader = new Wire(ResponseStatus.ReadResult(response.Body));
            var observed = HelloMessage.ReadResponse(reader);
            return new HelloOutcome(DHTPeer.Observed(response.Sender, transport, endpoint), stopwatch.Elapsed, observed);
        }

        /// <summary>Sends one encrypted Ping request to the node that answered the Hello, reporting failures in the outcome.</summary>
        /// <param name="socket">The bound and running probe transport.</param>
        /// <param name="identity">The probe identity.</param>
        /// <param name="self">The probe's signed contact.</param>
        /// <param name="peer">The contact that answered the Hello.</param>
        /// <param name="endpoint">The probed endpoint.</param>
        /// <param name="transport">The probed transport.</param>
        /// <param name="counter">Counts the transmissions of the exchange.</param>
        /// <param name="cancellationToken">Cancels the exchange.</param>
        /// <returns>The outcome with the round trip or a failure description.</returns>
        /// <exception cref="OperationCanceledException">The exchange was cancelled.</exception>
        private static async Task<PingOutcome> PingAsync(SocketTransport socket, DHTIdentity identity, DHTContact self, DHTPeer peer, IPEndPoint endpoint,
            DHTTransport transport, TransmissionCounter counter, CancellationToken cancellationToken)
        {
            var request = new Packet(self, Crypto.Random(Packet.RequestIdLength), (byte)Operation.Ping, ReadOnlyMemory<byte>.Empty);
            var envelope = request.Seal(identity, peer);
            var stopwatch = Stopwatch.StartNew();
            OwnedMemory encoded;
            try
            {
                encoded = await socket.ExchangeAtAsync(transport, endpoint, envelope, cancellationToken, counter).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                return PingOutcome.Failed("加密 Ping 未获应答（" + Display.Transport(transport) + " 超时或被拒绝）。");
            }
            stopwatch.Stop();
            using (encoded)
            {
                Packet response;
                try
                {
                    response = Packet.Open(identity, encoded.Bytes);
                    VerifyResponse(response, request, identity, peer.NodeId);
                }
                catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
                {
                    return PingOutcome.Failed("加密 Ping 的响应无效：" + exception.GetType().Name + "。");
                }
                try
                {
                    using var reader = new Wire(ResponseStatus.ReadResult(response.Body));
                    reader.End();
                }
                catch (IOException)
                {
                    return PingOutcome.Failed("对方拒绝了加密 Ping 请求。");
                }
                catch (InvalidDataException exception)
                {
                    return PingOutcome.Failed("加密 Ping 的响应无效：" + exception.GetType().Name + "。");
                }
                return PingOutcome.Answered(stopwatch.Elapsed);
            }
        }

        /// <summary>Authenticates a response against its request: operation, correlation and sender.</summary>
        /// <param name="response">The opened response.</param>
        /// <param name="request">The originating request.</param>
        /// <param name="identity">The probe identity, which must not be the sender.</param>
        /// <param name="expectedNodeId">The identity that must have answered, or null to accept any other identity.</param>
        /// <exception cref="CryptographicException">The response does not belong to the request or comes from the wrong identity.</exception>
        private static void VerifyResponse(Packet response, Packet request, DHTIdentity identity, string? expectedNodeId)
        {
            if (response.Kind != (request.Kind | 128) || !CryptographicOperations.FixedTimeEquals(response.RequestId.Span, request.RequestId.Span)
                || response.Sender.NodeId == identity.NodeId || (expectedNodeId != null && response.Sender.NodeId != expectedNodeId))
            {
                throw new CryptographicException("Response sender, operation or correlation mismatch.");
            }
        }

        /// <summary>Drops every request that reaches a probe, which serves nothing.</summary>
        /// <param name="envelope">The received envelope.</param>
        /// <param name="transport">The receiving transport.</param>
        /// <param name="source">The observed source endpoint.</param>
        /// <param name="cancellationToken">The handler lifetime.</param>
        /// <returns>A faulted task, so the transport answers nothing.</returns>
        private static Task<ReadOnlyMemory<byte>> RejectInbound(ReadOnlyMemory<byte> envelope, DHTTransport transport, IPEndPoint source,
            CancellationToken cancellationToken)
        {
            return Task.FromException<ReadOnlyMemory<byte>>(new IOException("The probe does not serve requests."));
        }

        /// <summary>Returns the wildcard listen address of an endpoint's address family.</summary>
        /// <param name="endpoint">The remote endpoint.</param>
        /// <returns>The IPv4 or IPv6 wildcard address.</returns>
        private static IPAddress WildcardFor(IPEndPoint endpoint)
        {
            return endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        }

        /// <summary>The authenticated answer to a Hello request.</summary>
        /// <param name="Peer">The answering contact at the probed endpoint.</param>
        /// <param name="RoundTrip">The time between sending the request and receiving the answer.</param>
        /// <param name="ObservedEndpoint">The endpoint the responder observed for the probe.</param>
        private sealed record HelloOutcome(DHTPeer Peer, TimeSpan RoundTrip, IPEndPoint ObservedEndpoint);

        /// <summary>The outcome of an encrypted Ping request.</summary>
        /// <param name="Succeeded">Whether the node answered with a success status.</param>
        /// <param name="RoundTrip">The round trip of an answered request.</param>
        /// <param name="Error">The failure description, or null.</param>
        private readonly record struct PingOutcome(bool Succeeded, TimeSpan? RoundTrip, string? Error)
        {
            /// <summary>Gets the outcome of a Ping that was never sent.</summary>
            internal static PingOutcome NotAttempted => new PingOutcome(false, null, null);

            /// <summary>Creates a successful outcome.</summary>
            /// <param name="roundTrip">The measured round trip.</param>
            /// <returns>The outcome.</returns>
            internal static PingOutcome Answered(TimeSpan roundTrip)
            {
                return new PingOutcome(true, roundTrip, null);
            }

            /// <summary>Creates a failed outcome.</summary>
            /// <param name="error">The failure description.</param>
            /// <returns>The outcome.</returns>
            internal static PingOutcome Failed(string error)
            {
                return new PingOutcome(false, null, error);
            }
        }
    }
}
