using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTNetwork.Protocol;
using MajDHTProber.Infrastructure;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Discovers nodes on the local network by broadcasting the protocol's signed Hello request over UDP and collecting
    /// the signed answers. Every node that listens on the scanned port answers a Hello regardless of how the datagram was
    /// addressed, so the scan works with any MajDHTNetwork node, not only with other probers.
    /// </summary>
    /// <remarks>
    /// A scan uses the shared probe identity on a socket of its own and speaks the documented UDP cookie handshake: the
    /// Hello frame announcing the sealed request is broadcast, every node answers with a Challenge, and the scan completes
    /// the exchange with each responder individually. Answers prove that a node with the returned identity listens at the
    /// answering endpoint; joining it still goes through the hosted node's own signed Hello in
    /// <see cref="NodeSession.AddPeerAsync"/>. The scanner answers no request, so the nodes it reaches cannot verify it
    /// and never admit it to their routing tables.
    /// </remarks>
    public sealed class LanDiscovery
    {
        /// <summary>The lease of the scan's own signed contact, which only has to outlive one scan.</summary>
        private static readonly TimeSpan s_contactLifetime = TimeSpan.FromHours(1);
        /// <summary>The Windows socket option that stops a closed target port from faulting later receives with a reset.</summary>
        private const int SioUdpConnectionReset = -1744830452;

        /// <summary>The identity vault providing the probe identity.</summary>
        private readonly IdentityVault _vault;
        /// <summary>The scan diagnostics logger.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a scanner.</summary>
        /// <param name="vault">The identity vault providing the probe identity.</param>
        /// <param name="sink">The log sink receiving scan diagnostics.</param>
        public LanDiscovery(IdentityVault vault, LogSink sink)
        {
            _vault = vault;
            _logger = sink.CreateLogger("局域网扫描");
        }

        /// <summary>Lists the subnet broadcast endpoints of every operational IPv4 interface.</summary>
        /// <param name="port">The UDP port the endpoints carry.</param>
        /// <returns>
        /// One endpoint per distinct subnet broadcast address, skipping loopback and link-local addresses; the limited
        /// broadcast address when no interface qualifies.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">The port is zero or out of range.</exception>
        public static IReadOnlyList<IPEndPoint> BroadcastTargets(int port)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            var addresses = new List<IPAddress>();
            try
            {
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        var broadcast = SubnetBroadcast(unicast);
                        if (broadcast != null && !addresses.Contains(broadcast))
                        {
                            addresses.Add(broadcast);
                        }
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Fall back to the limited broadcast address when the interfaces cannot be enumerated.
            }
            if (addresses.Count == 0)
            {
                addresses.Add(IPAddress.Broadcast);
            }
            var result = new List<IPEndPoint>(addresses.Count);
            foreach (var address in addresses)
            {
                result.Add(new IPEndPoint(address, port));
            }
            return result;
        }

        /// <summary>Broadcasts one signed Hello and collects the signed answers for the response window.</summary>
        /// <param name="options">The scan tuning.</param>
        /// <param name="cancellationToken">Cancels the scan.</param>
        /// <returns>The answering nodes; a target that does not answer is not an error.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The port is invalid or the response window is not positive.</exception>
        /// <exception cref="SocketException">No scanning socket could be bound.</exception>
        /// <exception cref="IOException">The probe identity could not be stored.</exception>
        /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
        public async Task<LanScanResult> ScanAsync(LanScanOptions options, CancellationToken cancellationToken = default)
        {
            if (options.ResponseWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "The response window must be positive.");
            }
            var targets = options.Targets ?? BroadcastTargets(options.Port);
            var identity = await _vault.GetOrCreateAsync(EndpointProber.IdentityName, options.Probe.IdentityWorkBits, options.Probe.PersistIdentity, cancellationToken)
                .ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            var self = DHTContact.Create(identity, DHTCapabilities.Download, DHTReachability.Unknown, false, Array.Empty<DHTPeer>(), s_contactLifetime);
            var request = new Packet(self, Crypto.Random(Packet.RequestIdLength), (byte)Operation.Hello, ReadOnlyMemory<byte>.Empty);
            var exchange = new HelloExchange(identity, request, request.SealHello(identity), _logger);
            _logger.LogInformation("开始扫描：向 {Count} 个端点广播 Hello，收集应答 {Window}。", targets.Count, Display.Duration(options.ResponseWindow));
            var sockets = new List<UdpClient>();
            try
            {
                var hello = UdpFrames.Hello(exchange.Digest);
                foreach (var target in targets)
                {
                    var socket = SocketFor(sockets, target.AddressFamily);
                    await socket.SendAsync(hello, target, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("已向 {Target} 发送 Hello 帧。", EndpointParser.Format(target));
                }
                var sentAt = stopwatch.Elapsed;
                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                window.CancelAfter(options.ResponseWindow);
                var receivers = new List<Task>(sockets.Count);
                foreach (var socket in sockets)
                {
                    receivers.Add(ReceiveAsync(socket, exchange, stopwatch, sentAt, window.Token));
                }
                await Task.WhenAll(receivers).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }
            }
            stopwatch.Stop();
            var result = new LanScanResult
            {
                Targets = targets,
                Peers = exchange.Peers,
                Elapsed = stopwatch.Elapsed,
                InvalidReplies = exchange.InvalidReplies,
            };
            _logger.LogInformation("扫描完成：发现 {Peers} 个节点，{Invalid} 个无效应答，耗时 {Elapsed}。", result.Peers.Count, result.InvalidReplies,
                Display.Duration(result.Elapsed));
            return result;
        }

        /// <summary>Computes the subnet broadcast address of an IPv4 interface address.</summary>
        /// <param name="unicast">The interface address.</param>
        /// <returns>The broadcast address, or null for non-IPv4, link-local or mask-less addresses.</returns>
        private static IPAddress? SubnetBroadcast(UnicastIPAddressInformation unicast)
        {
            var address = unicast.Address;
            if (address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
            {
                return null;
            }
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return null;
            }
            var mask = unicast.IPv4Mask.GetAddressBytes();
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)(bytes[index] | ~mask[index]);
            }
            return new IPAddress(bytes);
        }

        /// <summary>Returns the scanning socket of an address family, binding one on an assigned port when none exists yet.</summary>
        /// <param name="sockets">The sockets bound so far.</param>
        /// <param name="family">The address family.</param>
        /// <returns>The socket.</returns>
        /// <exception cref="SocketException">The socket could not be bound.</exception>
        private static UdpClient SocketFor(List<UdpClient> sockets, AddressFamily family)
        {
            foreach (var existing in sockets)
            {
                if (existing.Client.AddressFamily == family)
                {
                    return existing;
                }
            }
            var socket = new UdpClient(family);
            try
            {
                socket.EnableBroadcast = family == AddressFamily.InterNetwork;
                socket.Client.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        socket.Client.IOControl(SioUdpConnectionReset, new byte[] { 0 }, null);
                    }
                    catch (SocketException)
                    {
                        // Receives then report a closed target port as a reset, which the receive loop tolerates.
                    }
                }
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            sockets.Add(socket);
            return socket;
        }

        /// <summary>Receives datagrams on one socket until the response window closes.</summary>
        /// <param name="socket">The scanning socket.</param>
        /// <param name="exchange">Handles the datagrams.</param>
        /// <param name="stopwatch">The scan clock.</param>
        /// <param name="sentAt">The scan clock reading when the broadcast was sent.</param>
        /// <param name="windowToken">Ends the loop when the window closes or the scan is cancelled.</param>
        /// <returns>Completion when the window closed.</returns>
        private static async Task ReceiveAsync(UdpClient socket, HelloExchange exchange, Stopwatch stopwatch, TimeSpan sentAt, CancellationToken windowToken)
        {
            while (!windowToken.IsCancellationRequested)
            {
                UdpReceiveResult datagram;
                try
                {
                    datagram = await socket.ReceiveAsync(windowToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // A closed target port surfaces as a reset on some platforms; other answers may still arrive.
                    continue;
                }
                try
                {
                    await exchange.HandleAsync(socket, datagram, stopwatch.Elapsed - sentAt, windowToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // The responder vanished between its Challenge and the Request; other answers may still arrive.
                }
            }
        }

        /// <summary>Drives the cookie handshake of one broadcast Hello with every responder and authenticates the answers.</summary>
        private sealed class HelloExchange
        {
            /// <summary>The probe identity the answers are sealed to.</summary>
            private readonly DHTIdentity _identity;
            /// <summary>The broadcast request the answers must correlate with.</summary>
            private readonly Packet _request;
            /// <summary>The sealed request announced by the Hello frame and carried by every Request frame.</summary>
            private readonly byte[] _envelope;
            /// <summary>The scan diagnostics logger.</summary>
            private readonly ILogger _logger;
            /// <summary>Protects the state, which several receive loops update.</summary>
            private readonly Lock _gate = new Lock();
            /// <summary>The responders whose Challenge was already answered with a Request.</summary>
            private readonly HashSet<IPEndPoint> _challenged = new HashSet<IPEndPoint>();
            /// <summary>The answering nodes in the order of their first answer.</summary>
            private readonly List<DiscoveredPeer> _peers = new List<DiscoveredPeer>();
            /// <summary>The index of each answering node in <see cref="_peers"/>.</summary>
            private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
            /// <summary>The number of datagrams that were not valid frames of this exchange.</summary>
            private int _invalid;

            /// <summary>Creates an exchange.</summary>
            /// <param name="identity">The probe identity.</param>
            /// <param name="request">The broadcast request.</param>
            /// <param name="envelope">The sealed request.</param>
            /// <param name="logger">The scan diagnostics logger.</param>
            internal HelloExchange(DHTIdentity identity, Packet request, byte[] envelope, ILogger logger)
            {
                _identity = identity;
                _request = request;
                _envelope = envelope;
                _logger = logger;
                Digest = Crypto.Hash(envelope);
            }

            /// <summary>Gets the SHA-256 digest of the sealed request, which correlates every frame of the exchange.</summary>
            internal byte[] Digest { get; }

            /// <summary>Gets a copy of the answering nodes.</summary>
            internal IReadOnlyList<DiscoveredPeer> Peers
            {
                get
                {
                    lock (_gate)
                    {
                        return new List<DiscoveredPeer>(_peers);
                    }
                }
            }

            /// <summary>Gets the number of datagrams that were not valid frames of this exchange.</summary>
            internal int InvalidReplies
            {
                get
                {
                    lock (_gate)
                    {
                        return _invalid;
                    }
                }
            }

            /// <summary>Handles one datagram: answers a Challenge with the Request, or authenticates and records a Response.</summary>
            /// <param name="socket">The socket the datagram arrived on, used to answer a Challenge.</param>
            /// <param name="datagram">The received datagram.</param>
            /// <param name="roundTrip">The time since the broadcast.</param>
            /// <param name="cancellationToken">Cancels answering a Challenge.</param>
            /// <returns>Completion after the datagram was handled.</returns>
            /// <exception cref="SocketException">The Request could not be sent.</exception>
            /// <exception cref="OperationCanceledException">Sending was cancelled.</exception>
            internal async Task HandleAsync(UdpClient socket, UdpReceiveResult datagram, TimeSpan roundTrip, CancellationToken cancellationToken)
            {
                if (UdpFrames.TryReadChallenge(datagram.Buffer, Digest, out var cookie))
                {
                    lock (_gate)
                    {
                        if (!_challenged.Add(datagram.RemoteEndPoint))
                        {
                            return;
                        }
                    }
                    await socket.SendAsync(UdpFrames.Request(cookie, _envelope), datagram.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("{Source} 发来 Challenge，已回复携带 Cookie 的 Hello 请求。", EndpointParser.Format(datagram.RemoteEndPoint));
                    return;
                }
                if (UdpFrames.IsFrame(datagram.Buffer, UdpFrames.HelloKind) || UdpFrames.IsFrame(datagram.Buffer, UdpFrames.RequestKind))
                {
                    // A node that answered now tries to verify the scanner with a Ping of its own before admitting it to its
                    // routing table. The scanner serves nothing, so the request is dropped and the node never admits it.
                    _logger.LogDebug("忽略来自 {Source} 的入站请求帧；扫描器不应答请求。", EndpointParser.Format(datagram.RemoteEndPoint));
                    return;
                }
                if (!UdpFrames.TryReadResponse(datagram.Buffer, Digest, out var envelope))
                {
                    CountInvalid(datagram.RemoteEndPoint, null);
                    return;
                }
                DHTContact sender;
                IPEndPoint observed;
                try
                {
                    var response = Packet.Open(_identity, envelope);
                    EndpointProber.VerifyResponse(response, _request, _identity, null);
                    using var reader = new Wire(ResponseStatus.ReadResult(response.Body));
                    observed = HelloMessage.ReadResponse(reader);
                    sender = response.Sender;
                }
                catch (Exception exception) when (exception is InvalidDataException or CryptographicException or IOException)
                {
                    CountInvalid(datagram.RemoteEndPoint, exception);
                    return;
                }
                Record(sender, datagram.RemoteEndPoint, observed, roundTrip);
            }

            /// <summary>Counts a datagram that is not a valid frame of this exchange.</summary>
            /// <param name="source">The datagram source.</param>
            /// <param name="exception">The authentication failure, or null for a malformed frame.</param>
            private void CountInvalid(IPEndPoint source, Exception? exception)
            {
                lock (_gate)
                {
                    _invalid++;
                }
                _logger.LogDebug(exception, "来自 {Source} 的数据报不是本次扫描的有效应答。", EndpointParser.Format(source));
            }

            /// <summary>Records an authenticated answer, merging repeated answers of one node.</summary>
            /// <param name="sender">The answering contact.</param>
            /// <param name="source">The endpoint the answer came from.</param>
            /// <param name="observed">The endpoint the node observed for the scanning socket.</param>
            /// <param name="roundTrip">The time since the broadcast.</param>
            private void Record(DHTContact sender, IPEndPoint source, IPEndPoint observed, TimeSpan roundTrip)
            {
                lock (_gate)
                {
                    if (_index.TryGetValue(sender.NodeId, out var position))
                    {
                        _peers[position] = _peers[position] with { Replies = _peers[position].Replies + 1 };
                        return;
                    }
                    _index[sender.NodeId] = _peers.Count;
                    _peers.Add(new DiscoveredPeer
                    {
                        Peer = DHTPeer.Observed(sender, DHTTransport.Udp, source),
                        Endpoint = source,
                        RoundTrip = roundTrip,
                        ObservedEndpoint = observed,
                    });
                }
                _logger.LogInformation("发现节点 {NodeId}（{Endpoint}），往返 {RoundTrip}。", Display.ShortId(sender.NodeId), EndpointParser.Format(source),
                    Display.Duration(roundTrip));
            }
        }
    }
}
