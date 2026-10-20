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
using MajDHTProber.Infrastructure;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Hosts one <see cref="MajDHTNode"/> for the prober: owns its lifecycle, tracks the peers it learns about, remembers
    /// the records it handled, saves and restores its routing table and wraps every public node operation with logging
    /// and peer bookkeeping.
    /// </summary>
    /// <remarks>
    /// A library node can only be started once, so every start creates a fresh node with the session's current settings;
    /// the identity is reused so the node identifier stays stable. All members are thread-safe.
    /// </remarks>
    public sealed class NodeSession : IAsyncDisposable
    {
        /// <summary>The identity vault.</summary>
        private readonly IdentityVault _vault;
        /// <summary>The endpoint prober.</summary>
        private readonly EndpointProber _prober;
        /// <summary>The store of saved routing tables.</summary>
        private readonly RoutingSnapshotStore _routing;
        /// <summary>The session diagnostics logger.</summary>
        private readonly ILogger _logger;
        /// <summary>The logger handed to the library node.</summary>
        private readonly ILogger _nodeLogger;
        /// <summary>Serializes start and stop.</summary>
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        /// <summary>Protects the mutable session state.</summary>
        private readonly Lock _gate = new Lock();
        /// <summary>The records handled by this session, keyed by canonical key text.</summary>
        private readonly Dictionary<string, LocalRecordEntry> _records = new Dictionary<string, LocalRecordEntry>(StringComparer.Ordinal);
        /// <summary>The running node, or null when stopped.</summary>
        private MajDHTNode? _node;
        /// <summary>The running node's routing table, or null when stopped.</summary>
        private TrackingRoutingTable? _table;
        /// <summary>The identity of the current or last node.</summary>
        private DHTIdentity? _identity;
        /// <summary>The settings used by the next start.</summary>
        private ProberSettings _settings;
        /// <summary>The lifecycle state.</summary>
        private NodeSessionState _state = NodeSessionState.Stopped;
        /// <summary>The last start failure.</summary>
        private string? _lastError;
        /// <summary>When the current node started.</summary>
        private DateTimeOffset? _startedAt;
        /// <summary>The latest restoration of the saved routing table, which concurrent callers join instead of repeating.</summary>
        private Task<int>? _restoration;

        /// <summary>Creates a stopped session.</summary>
        /// <param name="label">The user-facing session name, also used as log category.</param>
        /// <param name="identityName">The name of the session's identity in the vault and of its saved routing table.</param>
        /// <param name="role">The session role.</param>
        /// <param name="settings">The settings used by the first start; the session keeps a copy.</param>
        /// <param name="vault">The identity vault.</param>
        /// <param name="prober">The endpoint prober.</param>
        /// <param name="routing">The store of saved routing tables.</param>
        /// <param name="sink">The log sink.</param>
        public NodeSession(string label, string identityName, NodeSessionRole role, ProberSettings settings, IdentityVault vault,
            EndpointProber prober, RoutingSnapshotStore routing, LogSink sink)
        {
            Label = label;
            IdentityName = identityName;
            Role = role;
            _settings = settings.Clone();
            _vault = vault;
            _prober = prober;
            _routing = routing;
            _logger = sink.CreateLogger(label);
            _nodeLogger = sink.CreateLogger(label + " · 协议库");
        }

        /// <summary>Raised after the lifecycle state changed; may be raised on any thread.</summary>
        public event EventHandler? StateChanged;

        /// <summary>Raised after the remembered records changed; may be raised on any thread.</summary>
        public event EventHandler? RecordsChanged;

        /// <summary>Gets the unique session identifier.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Gets the user-facing session name.</summary>
        public string Label { get; }

        /// <summary>Gets the name of the session's identity in the vault.</summary>
        public string IdentityName { get; }

        /// <summary>Gets the session role.</summary>
        public NodeSessionRole Role { get; }

        /// <summary>Gets the peers known to this session.</summary>
        public PeerDirectory Peers { get; } = new PeerDirectory();

        /// <summary>Gets or sets a copy of the settings; changes take effect at the next start.</summary>
        public ProberSettings Settings
        {
            get
            {
                lock (_gate)
                {
                    return _settings.Clone();
                }
            }
            set
            {
                lock (_gate)
                {
                    _settings = value.Clone();
                }
            }
        }

        /// <summary>Gets the lifecycle state.</summary>
        public NodeSessionState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <summary>Gets whether the node is running.</summary>
        public bool IsRunning => State == NodeSessionState.Running;

        /// <summary>Gets the last start failure, or null.</summary>
        public string? LastError
        {
            get
            {
                lock (_gate)
                {
                    return _lastError;
                }
            }
        }

        /// <summary>Gets when the current node started, or null when stopped.</summary>
        public DateTimeOffset? StartedAt
        {
            get
            {
                lock (_gate)
                {
                    return _startedAt;
                }
            }
        }

        /// <summary>Gets the node identifier once the identity is known.</summary>
        public string? NodeId
        {
            get
            {
                lock (_gate)
                {
                    return _identity?.NodeId ?? _vault.TryGetCachedNodeId(IdentityName);
                }
            }
        }

        /// <summary>Gets the running node's routing table, or null when stopped.</summary>
        public TrackingRoutingTable? RoutingTable
        {
            get
            {
                lock (_gate)
                {
                    return _table;
                }
            }
        }

        /// <summary>Gets the remembered records, newest first.</summary>
        public IReadOnlyList<LocalRecordEntry> Records
        {
            get
            {
                List<LocalRecordEntry> records;
                lock (_gate)
                {
                    records = new List<LocalRecordEntry>(_records.Values);
                }
                records.Sort((left, right) => right.At.CompareTo(left.At));
                return records;
            }
        }

        /// <summary>
        /// Starts a fresh node with the current settings, generating the identity first when needed. When the settings ask
        /// for a persistent routing table, the saved table is restored in the background once the node runs.
        /// </summary>
        /// <param name="cancellationToken">Cancels identity generation; the started node then runs until stopped.</param>
        /// <returns>Completion once listeners are bound.</returns>
        /// <exception cref="SettingsValidationException">The settings are invalid.</exception>
        /// <exception cref="SocketException">A configured port could not be bound.</exception>
        /// <exception cref="OperationCanceledException">Starting was cancelled.</exception>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (RoutingTable != null)
                {
                    return;
                }
                SetState(NodeSessionState.Starting, null);
                var settings = Settings;
                var options = NodeOptionsBuilder.Build(settings, _nodeLogger);
                var identity = await _vault.GetOrCreateAsync(IdentityName, settings.IdentityWorkBits, settings.PersistIdentity, cancellationToken)
                    .ConfigureAwait(false);
                var table = new TrackingRoutingTable();
                table.Observed += OnObserved;
                table.Removed += OnRemoved;
                var node = new MajDHTNode(options, identity, new MemoryDHTContentStore(), table);
                try
                {
                    // The start token is the node's lifetime token, so the node must not inherit the caller's cancellation.
                    await node.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    await node.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                lock (_gate)
                {
                    _node = node;
                    _table = table;
                    _identity = identity;
                    _startedAt = DateTimeOffset.UtcNow;
                }
                SetState(NodeSessionState.Running, null);
                _logger.LogInformation("节点已启动：{NodeId}，监听 {Listen}，TCP {Tcp} / UDP {Udp}。", identity.NodeId, settings.ListenAddresses,
                    settings.TcpEnabled ? settings.TcpPort.ToString() : "关闭", settings.UdpEnabled ? settings.UdpPort.ToString() : "关闭");
                if (settings.PersistRoutingTable && _routing.IsPersistent)
                {
                    BeginRestoration();
                }
            }
            catch (Exception exception)
            {
                var message = Display.Error(exception);
                SetState(exception is OperationCanceledException ? NodeSessionState.Stopped : NodeSessionState.Faulted, message);
                _logger.LogError(exception, "节点启动失败。");
                throw;
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <summary>Saves the routing table when the settings ask for it, then stops and drains the running node; does nothing when stopped.</summary>
        /// <returns>Completion after shutdown.</returns>
        public async Task StopAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                MajDHTNode? node;
                TrackingRoutingTable? table;
                lock (_gate)
                {
                    node = _node;
                    table = _table;
                }
                if (node == null || table == null)
                {
                    if (State == NodeSessionState.Faulted)
                    {
                        SetState(NodeSessionState.Stopped, null);
                    }
                    return;
                }
                SetState(NodeSessionState.Stopping, null);
                if (Settings.PersistRoutingTable && _routing.IsPersistent)
                {
                    SaveRoutingTable(node, table);
                }
                table.Observed -= OnObserved;
                table.Removed -= OnRemoved;
                await node.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _node = null;
                    _table = null;
                    _startedAt = null;
                }
                Peers.ReconcileRouting(new HashSet<string>());
                SetState(NodeSessionState.Stopped, null);
                _logger.LogInformation("节点已停止。");
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <summary>Stops the node.</summary>
        /// <returns>Completion after shutdown.</returns>
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        /// <summary>Returns the running node's signed contact.</summary>
        /// <returns>The contact, or null when the node is not running.</returns>
        public DHTPeer? TryGetLocalPeer()
        {
            var node = TryGetNode();
            if (node == null)
            {
                return null;
            }
            try
            {
                return node.LocalPeer;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns endpoints at which other nodes, including nodes hosted by this process, can contact this node: the
        /// advertised endpoints, or the loopback address for wildcard listeners on configured ports.
        /// </summary>
        /// <param name="transport">The transport whose port is used, or null for the preferred transport.</param>
        /// <returns>The contact endpoints; empty when the node is stopped or listens on assigned wildcard ports.</returns>
        public IReadOnlyList<IPEndPoint> GetContactEndpoints(DHTTransport? transport = null)
        {
            var result = new List<IPEndPoint>();
            var local = TryGetLocalPeer();
            if (local == null)
            {
                return result;
            }
            var settings = Settings;
            var selected = transport ?? settings.PreferredTransport;
            foreach (var endpoint in local.Endpoints)
            {
                var port = PortFor(endpoint, selected);
                if (port != 0)
                {
                    result.Add(new IPEndPoint(endpoint.Address, port));
                }
            }
            if (result.Count != 0)
            {
                return result;
            }
            var configuredPort = selected == DHTTransport.Udp
                ? (settings.UdpEnabled ? settings.UdpPort : 0)
                : (settings.TcpEnabled ? settings.TcpPort : 0);
            if (configuredPort == 0)
            {
                configuredPort = selected == DHTTransport.Udp
                    ? (settings.TcpEnabled ? settings.TcpPort : 0)
                    : (settings.UdpEnabled ? settings.UdpPort : 0);
            }
            if (configuredPort == 0)
            {
                return result;
            }
            foreach (var address in NodeOptionsBuilder.ParseAddressList(settings.ListenAddresses, string.Empty, true, new List<string>()))
            {
                var reachable = address.Equals(IPAddress.Any) ? IPAddress.Loopback : address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : address;
                result.Add(new IPEndPoint(reachable, configuredPort));
            }
            return result;
        }

        /// <summary>Contacts every configured bootstrap endpoint and runs a neighbor lookup.</summary>
        /// <param name="cancellationToken">Cancels joining.</param>
        /// <returns>The number of endpoints that answered.</returns>
        /// <exception cref="InvalidOperationException">The node is not running or no bootstrap endpoint is configured.</exception>
        /// <exception cref="IOException">No endpoint answered.</exception>
        public async Task<int> BootstrapAsync(CancellationToken cancellationToken = default)
        {
            var joined = await RunAsync("引导", "已配置的引导端点", node => node.BootstrapAsync(cancellationToken),
                count => count + " 个端点响应").ConfigureAwait(false);
            RefreshRoutingState();
            return joined;
        }

        /// <summary>
        /// Re-contacts the nodes of this session's saved routing table and admits those that still answer under their saved
        /// identity, then discovers the current neighbors. A restoration already running, such as the one started in the
        /// background by <see cref="StartAsync"/>, is joined instead of repeated.
        /// </summary>
        /// <param name="cancellationToken">Cancels waiting for the restoration.</param>
        /// <returns>The number of saved nodes that answered and were admitted; zero when nothing is saved or the saved table is invalid.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="OperationCanceledException">The restoration was cancelled or the node stopped.</exception>
        public Task<int> RestoreRoutingTableAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_restoration != null && !_restoration.IsCompleted)
                {
                    return _restoration.WaitAsync(cancellationToken);
                }
            }
            var node = RequireNode();
            var restoration = RestoreRoutingTableCoreAsync(node, cancellationToken);
            lock (_gate)
            {
                _restoration = restoration;
            }
            return restoration;
        }

        /// <summary>Runs an iterative lookup for a routing identifier and records the responsive nodes.</summary>
        /// <param name="routingId">The lowercase SHA-256 target identifier.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>The nearest responsive nodes.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The identifier is invalid.</exception>
        public async Task<IReadOnlyList<DHTPeer>> FindNodesAsync(string routingId, CancellationToken cancellationToken = default)
        {
            var peers = await RunAsync("查找节点", Display.ShortId(routingId), node => node.FindNodesAsync(routingId, cancellationToken),
                found => "找到 " + found.Count + " 个节点").ConfigureAwait(false);
            RecordPeers(peers, PeerSource.Lookup);
            return peers;
        }

        /// <summary>Refreshes the neighborhood of this node with a lookup of its own identifier.</summary>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>The nearest responsive nodes.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        public Task<IReadOnlyList<DHTPeer>> RefreshNeighborsAsync(CancellationToken cancellationToken = default)
        {
            var nodeId = RequireNode().NodeId;
            return FindNodesAsync(nodeId, cancellationToken);
        }

        /// <summary>
        /// Adds a node by endpoint through the hosted node's own signed Hello exchange: the node that answers is admitted to
        /// the routing table, registered as relay when it offers relay assistance and NAT traversal is enabled, and used
        /// for a neighbor lookup, exactly like a bootstrap endpoint.
        /// </summary>
        /// <param name="endpoint">The node's endpoint.</param>
        /// <param name="options">The add options.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>The outcome; an endpoint that does not answer or is this node itself is reported in the result.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
        public async Task<AddPeerResult> AddPeerAsync(IPEndPoint endpoint, AddPeerOptions options, CancellationToken cancellationToken = default)
        {
            var node = RequireNode();
            var target = EndpointParser.Format(endpoint);
            if (IsOwnEndpoint(endpoint))
            {
                _logger.LogWarning("手动添加节点：{Endpoint} 是本节点自己。", target);
                return new AddPeerResult { Endpoint = endpoint, Elapsed = TimeSpan.Zero, Note = "该端点是本节点自己。" };
            }
            var transport = options.Transport ?? Settings.PreferredTransport;
            _logger.LogInformation("手动添加节点：正在向 {Endpoint} 发送 Hello（优先 {Transport}）。", target, Display.Transport(transport));
            var stopwatch = Stopwatch.StartNew();
            DHTPeer peer;
            try
            {
                peer = await node.AddPeerAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                var failure = DescribeAddFailure(exception);
                _logger.LogWarning(exception, "手动添加节点：{Endpoint} 失败，耗时 {Elapsed}。{Note}", target, Display.Duration(stopwatch.Elapsed), failure);
                return new AddPeerResult { Endpoint = endpoint, Elapsed = stopwatch.Elapsed, Note = failure };
            }
            stopwatch.Stop();
            var admitted = RecordContactedPeer(peer, endpoint.Address, PeerSource.Manual);
            var routingTableSize = RoutingTable?.Snapshot().Count ?? 0;
            string? note = null;
            if (peer.Reachability == DHTReachability.Private)
            {
                note = "该节点声明自己为私有节点，只能经中继访问，无法加入路由表，但它仍会出现在节点列表中。";
            }
            else if (!admitted)
            {
                note = "路由表未接纳该节点（桶已满、网段限额或联系人已过期），但它仍会出现在节点列表中。";
            }
            if ((peer.Capabilities & DHTCapabilities.Relay) != 0 && !Settings.EnableNatTraversal)
            {
                note = AppendNote(note, "该节点具备中继能力，但本节点未启用 NAT 穿透，未注册中继。");
            }
            _logger.LogInformation("手动添加节点：{NodeId}（{Endpoint}）{Admission}，邻居查找后路由表共 {Count} 个节点，耗时 {Elapsed}。",
                Display.ShortId(peer.NodeId), target, admitted ? "已加入路由表" : "未被路由表接纳", routingTableSize, Display.Duration(stopwatch.Elapsed));
            return new AddPeerResult
            {
                Endpoint = endpoint,
                Elapsed = stopwatch.Elapsed,
                Peer = peer,
                AdmittedToRoutingTable = admitted,
                RoutingTableSize = routingTableSize,
                Note = note,
            };
        }

        /// <summary>
        /// Connects this node with another node hosted by this process the way a user-added node is connected: a signed
        /// Hello over loopback, admission to the routing table and a neighbor lookup, so the two nodes verify each other
        /// through the real protocol.
        /// </summary>
        /// <param name="other">The other session, which must be running.</param>
        /// <param name="cancellationToken">Cancels the exchange.</param>
        /// <returns>Whether the other node answered and is a routing table entry afterwards; failures are logged.</returns>
        public async Task<bool> ConnectLocalPeerAsync(NodeSession other, CancellationToken cancellationToken = default)
        {
            var node = TryGetNode();
            var endpoints = other.GetContactEndpoints();
            var expected = other.NodeId;
            if (node == null || endpoints.Count == 0 || expected == null || expected == NodeId)
            {
                return false;
            }
            var endpoint = endpoints[0];
            try
            {
                var peer = await node.AddPeerAsync(endpoint, null, cancellationToken).ConfigureAwait(false);
                var admitted = RecordContactedPeer(peer, endpoint.Address, PeerSource.LocalNode);
                if (peer.NodeId == expected)
                {
                    Peers.SetLabel(peer.NodeId, other.Label);
                }
                else
                {
                    _logger.LogWarning("端点 {Endpoint} 由 {Actual} 应答，而不是{Label}。", EndpointParser.Format(endpoint), Display.ShortId(peer.NodeId), other.Label);
                }
                _logger.LogInformation("已连接{Label}（{NodeId}），{Admission}。", other.Label, Display.ShortId(peer.NodeId), admitted ? "已加入路由表" : "未被路由表接纳");
                return admitted;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                _logger.LogWarning(exception, "连接{Label}（{Endpoint}）失败。", other.Label, EndpointParser.Format(endpoint));
                return false;
            }
        }

        /// <summary>Probes one endpoint with this session's protocol settings; the node does not need to be running.</summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <param name="transport">The probed transport.</param>
        /// <param name="options">Optional probe tuning; defaults to this session's settings.</param>
        /// <param name="cancellationToken">Cancels the probe.</param>
        /// <returns>The probe outcome.</returns>
        /// <exception cref="OperationCanceledException">The probe was cancelled.</exception>
        public async Task<EndpointProbeResult> ProbeEndpointAsync(IPEndPoint endpoint, DHTTransport transport, ProbeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = await _prober.ProbeAsync(endpoint, transport, options ?? ProbeOptions.From(Settings), cancellationToken).ConfigureAwait(false);
            if (result.Peer != null && result.NodeId != NodeId)
            {
                Peers.Observe(result.Peer, PeerSource.Probe);
                Peers.RecordProbe(result.Peer.NodeId, PeerProbeRecord.From(result, DateTimeOffset.UtcNow));
            }
            return result;
        }

        /// <summary>Probes every advertised endpoint and transport of a known peer in parallel and remembers the outcome.</summary>
        /// <param name="peer">The peer.</param>
        /// <param name="cancellationToken">Cancels the probes.</param>
        /// <returns>One result per endpoint and transport; empty when the peer advertises no endpoint.</returns>
        /// <exception cref="OperationCanceledException">The probes were cancelled.</exception>
        public async Task<IReadOnlyList<EndpointProbeResult>> ProbePeerAsync(PeerSnapshot peer, CancellationToken cancellationToken = default)
        {
            var probes = new List<Task<EndpointProbeResult>>();
            var options = ProbeOptions.From(Settings);
            foreach (var endpoint in peer.Contact.Endpoints)
            {
                if (endpoint.TcpPort != 0)
                {
                    probes.Add(_prober.ProbeAsync(new IPEndPoint(endpoint.Address, endpoint.TcpPort), DHTTransport.Tcp, options, cancellationToken));
                }
                if (endpoint.UdpPort != 0)
                {
                    probes.Add(_prober.ProbeAsync(new IPEndPoint(endpoint.Address, endpoint.UdpPort), DHTTransport.Udp, options, cancellationToken));
                }
            }
            if (probes.Count == 0)
            {
                _logger.LogInformation("节点 {NodeId} 未通告任何端点，无法直接探测。", Display.ShortId(peer.NodeId));
                return Array.Empty<EndpointProbeResult>();
            }
            var results = await Task.WhenAll(probes).ConfigureAwait(false);
            EndpointProbeResult? best = null;
            foreach (var result in results)
            {
                if (result.Succeeded && result.NodeId == peer.NodeId && (best == null || (!best.PingSucceeded && result.PingSucceeded)))
                {
                    best = result;
                }
                else if (result.Succeeded && result.NodeId != peer.NodeId)
                {
                    _logger.LogWarning("端点 {Endpoint} 由另一个节点 {Other} 应答，而不是 {Expected}。", EndpointParser.Format(result.Endpoint),
                        Display.ShortId(result.NodeId), Display.ShortId(peer.NodeId));
                }
            }
            var remembered = best ?? (results[0] with { HelloSucceeded = false, Error = "所有端点均未由该节点应答。" });
            Peers.RecordProbe(peer.NodeId, PeerProbeRecord.From(remembered, DateTimeOffset.UtcNow));
            return results;
        }

        /// <summary>Checks whether this node reaches a peer through its own routes by looking up the peer's identifier.</summary>
        /// <param name="peer">The peer.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>Whether the peer answered, with timing and statistics.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        public async Task<RouteCheckResult> CheckRouteAsync(DHTPeer peer, CancellationToken cancellationToken = default)
        {
            var node = RequireNode();
            var stopwatch = Stopwatch.StartNew();
            var found = await FindNodesAsync(peer.NodeId, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var reachable = false;
            foreach (var candidate in found)
            {
                reachable |= candidate.NodeId == peer.NodeId;
            }
            _logger.LogInformation("路由探测 {NodeId}：{Result}。", Display.ShortId(peer.NodeId), reachable ? "已响应" : "未响应");
            return new RouteCheckResult
            {
                Reachable = reachable,
                Elapsed = stopwatch.Elapsed,
                NodesFound = found.Count,
                Statistics = node.GetPeerStatistics(peer.NodeId),
            };
        }

        /// <summary>Registers this node's endpoints with a relay.</summary>
        /// <param name="relay">A contact advertising relay capability.</param>
        /// <param name="cancellationToken">Cancels the initial registration.</param>
        /// <returns>Completion after registration.</returns>
        /// <exception cref="InvalidOperationException">The node is not running or NAT traversal is disabled.</exception>
        /// <exception cref="ArgumentException">The contact cannot provide rendezvous assistance.</exception>
        /// <exception cref="IOException">No transport could register.</exception>
        public Task RegisterRelayAsync(DHTPeer relay, CancellationToken cancellationToken = default)
        {
            return RunAsync("注册中继", Display.ShortId(relay.NodeId), async node =>
            {
                await node.RegisterRelayAsync(relay, cancellationToken).ConfigureAwait(false);
                return true;
            });
        }

        /// <summary>Asks a relay to introduce this node and a peer and attempts a direct connection.</summary>
        /// <param name="peer">The peer, which must be registered with the relay.</param>
        /// <param name="relay">The common relay.</param>
        /// <param name="transport">The transport to punch.</param>
        /// <param name="cancellationToken">Cancels the attempt.</param>
        /// <returns>Whether a direct route was authenticated.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The peer, relay or transport is invalid.</exception>
        public Task<bool> TryPunchAsync(DHTPeer peer, DHTPeer relay, DHTTransport transport, CancellationToken cancellationToken = default)
        {
            return RunAsync("打洞（" + Display.Transport(transport) + "）", Display.ShortId(peer.NodeId) + " 经 " + Display.ShortId(relay.NodeId),
                node => node.TryPunchAsync(peer, relay, transport, cancellationToken), success => success ? "已建立直连" : "未能建立直连");
        }

        /// <summary>Publishes content under this node's publisher namespace.</summary>
        /// <param name="name">The case-sensitive name.</param>
        /// <param name="source">A readable stream positioned at the start of the content.</param>
        /// <param name="revision">The positive revision.</param>
        /// <param name="cancellationToken">Cancels publication.</param>
        /// <returns>The signed record.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The name, source, revision or size is invalid.</exception>
        /// <exception cref="IOException">Quotas prevent publication.</exception>
        public async Task<DHTRecord> PublishAsync(string name, Stream source, long revision = 1, CancellationToken cancellationToken = default)
        {
            var record = await RunAsync("发布", name, node => node.PublishAsync(name, source, revision, cancellationToken),
                published => Display.Bytes(published.Length) + "，" + published.BlockCount + " 块").ConfigureAwait(false);
            UpsertRecord(new LocalRecordEntry { Record = record, Origin = RecordOrigin.Published, At = DateTimeOffset.UtcNow });
            return record;
        }

        /// <summary>Publishes a second name for already published content.</summary>
        /// <param name="name">The new name.</param>
        /// <param name="content">The existing record of the content.</param>
        /// <param name="revision">The positive revision of the new name.</param>
        /// <param name="cancellationToken">Cancels publication.</param>
        /// <returns>The signed alias record.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The name or revision is invalid, or the content is not local.</exception>
        public async Task<DHTRecord> PublishAliasAsync(string name, DHTRecord content, long revision = 1, CancellationToken cancellationToken = default)
        {
            var record = await RunAsync("发布别名", name, node => node.PublishAliasAsync(name, content, revision, cancellationToken)).ConfigureAwait(false);
            UpsertRecord(new LocalRecordEntry { Record = record, Origin = RecordOrigin.Alias, At = DateTimeOffset.UtcNow });
            return record;
        }

        /// <summary>Reannounces a record and remembers how many replicas accepted it.</summary>
        /// <param name="record">The record.</param>
        /// <param name="cancellationToken">Cancels the announcement.</param>
        /// <returns>The number of remote replicas.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The content is not registered locally.</exception>
        /// <exception cref="InvalidDataException">The record is expired or superseded.</exception>
        public async Task<int> AnnounceAsync(DHTRecord record, CancellationToken cancellationToken = default)
        {
            var replicas = await RunAsync("公告", record.Key.ToString(), node => node.AnnounceAsync(record, cancellationToken),
                count => count + " 个副本").ConfigureAwait(false);
            UpdateRecord(record.Key.ToString(), entry => entry with { Replicas = replicas });
            return replicas;
        }

        /// <summary>Withdraws a key of this node's publisher namespace.</summary>
        /// <param name="name">The name.</param>
        /// <param name="revision">The withdrawal revision, or zero to choose one automatically.</param>
        /// <param name="cancellationToken">Cancels the withdrawal.</param>
        /// <returns>The signed withdrawal.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The name or revision is invalid.</exception>
        public async Task<DHTRecord> UnpublishAsync(string name, long revision = 0, CancellationToken cancellationToken = default)
        {
            var withdrawal = await RunAsync("撤回", name, node => node.UnpublishAsync(name, revision, cancellationToken)).ConfigureAwait(false);
            UpsertRecord(new LocalRecordEntry { Record = withdrawal, Origin = RecordOrigin.Withdrawal, At = DateTimeOffset.UtcNow });
            return withdrawal;
        }

        /// <summary>Stops serving a file locally.</summary>
        /// <param name="fileHash">The complete-file SHA-256 hash.</param>
        /// <returns>Whether the node was serving the file.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The hash is invalid.</exception>
        public bool StopProviding(string fileHash)
        {
            var stopped = RequireNode().StopProviding(fileHash);
            _logger.LogInformation("停止提供 {Hash}：{Result}。", Display.ShortId(fileHash), stopped ? "已停止" : "本节点未提供该文件");
            List<string> keys;
            lock (_gate)
            {
                keys = new List<string>();
                foreach (var entry in _records.Values)
                {
                    if (!entry.Record.IsWithdrawn && entry.Record.FileHash == fileHash)
                    {
                        keys.Add(entry.KeyText);
                    }
                }
            }
            foreach (var key in keys)
            {
                UpdateRecord(key, entry => entry with { ProvidingStopped = true });
            }
            return stopped;
        }

        /// <summary>Imports a verified local copy of another publisher's content and announces this node as provider.</summary>
        /// <param name="record">The existing signed record.</param>
        /// <param name="source">A readable stream positioned at the start of the content.</param>
        /// <param name="cancellationToken">Cancels the import.</param>
        /// <returns>The number of remote nodes accepting the provider announcement.</returns>
        /// <exception cref="InvalidOperationException">The node is not running or cannot provide content.</exception>
        /// <exception cref="InvalidDataException">The content differs from the record.</exception>
        public async Task<int> ImportContentAsync(DHTRecord record, Stream source, CancellationToken cancellationToken = default)
        {
            var replicas = await RunAsync("导入内容", record.Key.ToString(), node => node.ImportContentAsync(record, source, cancellationToken),
                count => count + " 个副本").ConfigureAwait(false);
            UpsertRecord(new LocalRecordEntry { Record = record, Origin = RecordOrigin.Imported, At = DateTimeOffset.UtcNow, Replicas = replicas });
            return replicas;
        }

        /// <summary>Resolves the newest signed record of a key.</summary>
        /// <param name="key">The key.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>The record, or null when no reachable node has it.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The key is uninitialized.</exception>
        public async Task<DHTRecord?> ResolveAsync(DHTKey key, CancellationToken cancellationToken = default)
        {
            var record = await RunAsync("解析", key.ToString(), node => node.ResolveAsync(key, cancellationToken),
                resolved => resolved == null ? "未找到" : "修订 " + resolved.Revision).ConfigureAwait(false);
            if (record != null)
            {
                UpsertRecord(new LocalRecordEntry
                {
                    Record = record,
                    Origin = record.IsWithdrawn ? RecordOrigin.Withdrawal : RecordOrigin.Resolved,
                    At = DateTimeOffset.UtcNow,
                });
            }
            return record;
        }

        /// <summary>Finds the providers of a complete-file hash and records them.</summary>
        /// <param name="fileHash">The complete-file SHA-256 hash.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>The provider contacts.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        /// <exception cref="ArgumentException">The hash is invalid.</exception>
        public async Task<IReadOnlyList<DHTPeer>> FindProvidersAsync(string fileHash, CancellationToken cancellationToken = default)
        {
            var providers = await RunAsync("查找提供者", Display.ShortId(fileHash), node => node.FindProvidersAsync(fileHash, cancellationToken),
                found => "找到 " + found.Count + " 个提供者").ConfigureAwait(false);
            RecordPeers(providers, PeerSource.Provider);
            return providers;
        }

        /// <summary>Resolves a key and downloads its verified content.</summary>
        /// <param name="key">The key.</param>
        /// <param name="destination">A writable stream; it may hold partial content after a failure.</param>
        /// <param name="expectedFileHash">An optional trusted complete-file hash.</param>
        /// <param name="progress">Receives the number of bytes written so far, or null.</param>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>The verified record.</returns>
        /// <exception cref="InvalidOperationException">The node is not running or cannot download.</exception>
        /// <exception cref="FileNotFoundException">The key was not found or was withdrawn.</exception>
        /// <exception cref="InvalidDataException">Verification failed.</exception>
        /// <exception cref="IOException">No provider delivered the content.</exception>
        public async Task<DHTRecord> DownloadAsync(DHTKey key, Stream destination, string? expectedFileHash = null, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var counting = new ProgressStream(destination, progress);
            var record = await RunAsync("下载", key.ToString(), node => node.DownloadAsync(key, counting, expectedFileHash, cancellationToken),
                downloaded => Display.Bytes(downloaded.Length)).ConfigureAwait(false);
            UpsertRecord(new LocalRecordEntry { Record = record, Origin = RecordOrigin.Downloaded, At = DateTimeOffset.UtcNow });
            return record;
        }

        /// <summary>Downloads the verified content of a known record.</summary>
        /// <param name="record">The signed record.</param>
        /// <param name="destination">A writable stream; it may hold partial content after a failure.</param>
        /// <param name="expectedFileHash">An optional trusted complete-file hash.</param>
        /// <param name="progress">Receives the number of bytes written so far, or null.</param>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>The verified record.</returns>
        /// <exception cref="InvalidOperationException">The node is not running or cannot download.</exception>
        /// <exception cref="InvalidDataException">Verification failed.</exception>
        /// <exception cref="IOException">No provider delivered the content.</exception>
        public async Task<DHTRecord> DownloadAsync(DHTRecord record, Stream destination, string? expectedFileHash = null, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var counting = new ProgressStream(destination, progress);
            var downloaded = await RunAsync("按记录下载", record.Key.ToString(), node => node.DownloadAsync(record, counting, expectedFileHash, cancellationToken),
                result => Display.Bytes(result.Length)).ConfigureAwait(false);
            UpsertRecord(new LocalRecordEntry { Record = downloaded, Origin = RecordOrigin.Downloaded, At = DateTimeOffset.UtcNow });
            return downloaded;
        }

        /// <summary>Reads and verifies one block from a specific provider, optionally through a relay.</summary>
        /// <param name="record">The signed record.</param>
        /// <param name="provider">The provider.</param>
        /// <param name="index">The zero-based block index.</param>
        /// <param name="relay">An optional relay, or null for direct access with automatic fallback.</param>
        /// <param name="cancellationToken">Cancels the transfer.</param>
        /// <returns>The verified block.</returns>
        /// <exception cref="InvalidOperationException">The node is not running or cannot download.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The index is outside the file.</exception>
        /// <exception cref="InvalidDataException">The block failed verification.</exception>
        /// <exception cref="IOException">The provider or relay is unavailable.</exception>
        public Task<ReadOnlyMemory<byte>> ReadBlockAsync(DHTRecord record, DHTPeer provider, int index, DHTPeer? relay = null,
            CancellationToken cancellationToken = default)
        {
            var target = "块 " + index + " @ " + Display.ShortId(provider.NodeId) + (relay == null ? string.Empty : " 经 " + Display.ShortId(relay.NodeId));
            return RunAsync("读取块", target, node => node.ReadBlockAsync(record, provider, index, relay, cancellationToken),
                block => Display.Bytes(block.Length));
        }

        /// <summary>Returns the running node's per-peer transmission statistics.</summary>
        /// <returns>The statistics, or an empty list when the node is stopped.</returns>
        public IReadOnlyList<DHTPeerStatistics> GetStatistics()
        {
            return TryGetNode()?.PeerStatistics ?? Array.Empty<DHTPeerStatistics>();
        }

        /// <summary>Returns the running node's statistics for one peer.</summary>
        /// <param name="nodeId">The peer identifier.</param>
        /// <returns>The statistics, or null when none were recorded or the node is stopped.</returns>
        public DHTPeerStatistics? GetStatistics(string nodeId)
        {
            return TryGetNode()?.GetPeerStatistics(nodeId);
        }

        /// <summary>Forgets a peer, optionally also evicting it from the routing table.</summary>
        /// <param name="nodeId">The peer identifier.</param>
        /// <param name="evict">Whether to remove the peer from the routing table as well.</param>
        /// <returns>Whether the peer was known.</returns>
        public bool ForgetPeer(string nodeId, bool evict)
        {
            if (evict)
            {
                RoutingTable?.Evict(nodeId);
            }
            var forgotten = Peers.Forget(nodeId);
            _logger.LogInformation("已从列表{Evicted}移除节点 {NodeId}。", evict ? "和路由表" : string.Empty, Display.ShortId(nodeId));
            return forgotten;
        }

        /// <summary>Aligns the peer list's routing table flags with the running node's table.</summary>
        public void RefreshRoutingState()
        {
            var table = RoutingTable;
            if (table == null)
            {
                return;
            }
            var routed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var peer in table.Snapshot())
            {
                routed.Add(peer.NodeId);
            }
            Peers.ReconcileRouting(routed);
        }

        /// <summary>Gets the port of an advertised endpoint for a transport, falling back to the other transport.</summary>
        /// <param name="endpoint">The advertised endpoint.</param>
        /// <param name="transport">The transport.</param>
        /// <returns>The port, or zero when neither transport is advertised.</returns>
        private static int PortFor(DHTEndpoint endpoint, DHTTransport transport)
        {
            var primary = transport == DHTTransport.Udp ? endpoint.UdpPort : endpoint.TcpPort;
            return primary != 0 ? primary : (transport == DHTTransport.Udp ? endpoint.TcpPort : endpoint.UdpPort);
        }

        /// <summary>Appends a sentence to an optional note.</summary>
        /// <param name="note">The current note.</param>
        /// <param name="sentence">The sentence.</param>
        /// <returns>The combined note.</returns>
        private static string AppendNote(string? note, string sentence)
        {
            return string.IsNullOrEmpty(note) ? sentence : note + " " + sentence;
        }

        /// <summary>Explains why the library could not add a node at an endpoint.</summary>
        /// <param name="exception">The failure reported by the library.</param>
        /// <returns>A user-facing explanation.</returns>
        private static string DescribeAddFailure(Exception exception)
        {
            if (exception is ArgumentException)
            {
                return "端点无效：需要单播地址和非零端口。";
            }
            if (exception.InnerException is CryptographicException)
            {
                return "端点有应答，但响应未通过身份校验（可能是本节点自己、身份工作量不足或响应被篡改）。";
            }
            return "端点未响应 Hello 请求（TCP 与 UDP 均超时或被拒绝）。";
        }

        /// <summary>Checks whether an endpoint is one at which this node can be contacted.</summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <returns>Whether the endpoint belongs to this node.</returns>
        private bool IsOwnEndpoint(IPEndPoint endpoint)
        {
            foreach (var transport in new[] { DHTTransport.Tcp, DHTTransport.Udp })
            {
                foreach (var own in GetContactEndpoints(transport))
                {
                    if (own.Equals(endpoint))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Records a contact the hosted node just authenticated at an address and aligns the routing flags.</summary>
        /// <param name="peer">The authenticated contact.</param>
        /// <param name="address">The address at which it answered.</param>
        /// <param name="source">How the contact was learned.</param>
        /// <returns>Whether the contact is a routing table entry.</returns>
        private bool RecordContactedPeer(DHTPeer peer, IPAddress address, PeerSource source)
        {
            var table = RoutingTable;
            var admitted = table != null && table.Contains(peer.NodeId);
            Peers.Observe(peer, source, admitted, address, interaction: true);
            RefreshRoutingState();
            return admitted;
        }

        /// <summary>Starts restoring the saved routing table without delaying the start; the outcome is logged.</summary>
        private void BeginRestoration()
        {
            Task<int> restoration;
            try
            {
                restoration = RestoreRoutingTableAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            _ = restoration.ContinueWith(task => _logger.LogDebug(task.Exception, "后台恢复路由表未完成。"), CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        }

        /// <summary>Loads the saved routing table and restores it into the running node.</summary>
        /// <param name="node">The running node.</param>
        /// <param name="cancellationToken">Cancels the restoration.</param>
        /// <returns>The number of saved nodes that answered and were admitted.</returns>
        /// <exception cref="OperationCanceledException">The restoration was cancelled or the node stopped.</exception>
        private async Task<int> RestoreRoutingTableCoreAsync(MajDHTNode node, CancellationToken cancellationToken)
        {
            var snapshot = _routing.TryLoad(IdentityName);
            if (snapshot == null)
            {
                _logger.LogDebug("没有已保存的路由表可恢复。");
                return 0;
            }
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("正在恢复已保存的路由表（{Bytes}）。", Display.Bytes(snapshot.Length));
            int restored;
            try
            {
                restored = await node.RestoreRoutingTableAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                _logger.LogWarning(exception, "已保存的路由表无效，已丢弃。");
                TryDeleteRoutingSnapshot();
                return 0;
            }
            RefreshRoutingState();
            _logger.LogInformation("路由表恢复完成：{Count} 个已保存的节点应答并加入路由表，耗时 {Elapsed}。", restored, Display.Duration(stopwatch.Elapsed));
            return restored;
        }

        /// <summary>Saves the routing table of a node that is about to stop, keeping the previous file when the table is empty.</summary>
        /// <param name="node">The running node.</param>
        /// <param name="table">The node's routing table.</param>
        private void SaveRoutingTable(MajDHTNode node, TrackingRoutingTable table)
        {
            var count = table.Snapshot().Count;
            if (count == 0)
            {
                _logger.LogDebug("路由表为空，保留上次保存的路由表。");
                return;
            }
            try
            {
                var snapshot = node.ExportRoutingTable();
                _routing.Save(IdentityName, snapshot);
                _logger.LogInformation("已保存路由表：{Count} 个节点，{Bytes}。", count, Display.Bytes(snapshot.Length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "路由表无法保存。");
            }
        }

        /// <summary>Deletes the saved routing table, logging rather than failing when the file cannot be deleted.</summary>
        private void TryDeleteRoutingSnapshot()
        {
            try
            {
                _routing.Delete(IdentityName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "无效的路由表文件无法删除。");
            }
        }

        /// <summary>Runs one node operation with timing and logging.</summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="operation">The user-facing operation name.</param>
        /// <param name="target">The user-facing operation target.</param>
        /// <param name="action">The operation.</param>
        /// <param name="describe">Optionally summarizes the result for the log.</param>
        /// <returns>The operation result.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        private async Task<T> RunAsync<T>(string operation, string target, Func<MajDHTNode, Task<T>> action, Func<T, string>? describe = null)
        {
            var node = RequireNode();
            var stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("{Operation}：开始（{Target}）。", operation, target);
            try
            {
                var result = await action(node).ConfigureAwait(false);
                var detail = describe == null ? string.Empty : describe(result) + "，";
                _logger.LogInformation("{Operation}：完成（{Target}），{Detail}耗时 {Elapsed}。", operation, target, detail, Display.Duration(stopwatch.Elapsed));
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("{Operation}：已取消（{Target}）。", operation, target);
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "{Operation}：失败（{Target}），耗时 {Elapsed}。", operation, target, Display.Duration(stopwatch.Elapsed));
                throw;
            }
        }

        /// <summary>Returns the running node.</summary>
        /// <returns>The node.</returns>
        /// <exception cref="InvalidOperationException">The node is not running.</exception>
        private MajDHTNode RequireNode()
        {
            return TryGetNode() ?? throw new InvalidOperationException("节点未运行，请先启动节点。");
        }

        /// <summary>Returns the running node, or null.</summary>
        /// <returns>The node, or null when stopped.</returns>
        private MajDHTNode? TryGetNode()
        {
            lock (_gate)
            {
                return _node;
            }
        }

        /// <summary>Records peers returned by a lookup, excluding this node itself.</summary>
        /// <param name="peers">The returned peers.</param>
        /// <param name="source">The lookup kind.</param>
        private void RecordPeers(IReadOnlyList<DHTPeer> peers, PeerSource source)
        {
            var table = RoutingTable;
            var self = NodeId;
            foreach (var peer in peers)
            {
                if (peer.NodeId != self)
                {
                    Peers.Observe(peer, source, table?.Contains(peer.NodeId));
                }
            }
        }

        /// <summary>Updates the peer list after the node observed a contact.</summary>
        /// <param name="sender">The routing table.</param>
        /// <param name="observation">The observation.</param>
        private void OnObserved(object? sender, RoutingObservation observation)
        {
            if (IsProbeClient(observation.Peer.NodeId))
            {
                // The prober's own probes and short-lived clients contact hosted nodes too, but they are not members of the probed network.
                return;
            }
            Peers.Observe(observation.Peer, PeerSource.Routing, observation.InTable, observation.VerifiedAddress, interaction: true);
            foreach (var hint in observation.Peer.RelayHints)
            {
                Peers.Observe(hint, PeerSource.RelayHint);
            }
        }

        /// <summary>Checks whether a node is one of the prober's probes or short-lived clients.</summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>Whether the node uses the shared probe identity.</returns>
        private bool IsProbeClient(string nodeId)
        {
            return nodeId == _vault.TryGetCachedNodeId(EndpointProber.IdentityName);
        }

        /// <summary>Updates the peer list after the node dropped a contact.</summary>
        /// <param name="sender">The routing table.</param>
        /// <param name="nodeId">The dropped node.</param>
        private void OnRemoved(object? sender, string nodeId)
        {
            Peers.RecordRoutingRemoval(nodeId);
        }

        /// <summary>Adds or replaces a remembered record, preferring higher revisions and stronger origins.</summary>
        /// <param name="entry">The new entry.</param>
        private void UpsertRecord(LocalRecordEntry entry)
        {
            lock (_gate)
            {
                if (_records.TryGetValue(entry.KeyText, out var known) && known.Record.Revision == entry.Record.Revision
                    && entry.Origin == RecordOrigin.Resolved)
                {
                    // Resolving a record this node already holds adds nothing.
                    return;
                }
                _records[entry.KeyText] = known != null && known.Record.Revision > entry.Record.Revision ? known : entry;
            }
            NotifyRecords();
        }

        /// <summary>Updates a remembered record.</summary>
        /// <param name="keyText">The canonical key text.</param>
        /// <param name="update">Creates the updated entry.</param>
        private void UpdateRecord(string keyText, Func<LocalRecordEntry, LocalRecordEntry> update)
        {
            lock (_gate)
            {
                if (!_records.TryGetValue(keyText, out var known))
                {
                    return;
                }
                _records[keyText] = update(known);
            }
            NotifyRecords();
        }

        /// <summary>Raises <see cref="RecordsChanged"/> while isolating listener failures.</summary>
        private void NotifyRecords()
        {
            try
            {
                RecordsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "记录变更监听器出错。");
            }
        }

        /// <summary>Changes the lifecycle state and notifies listeners.</summary>
        /// <param name="state">The new state.</param>
        /// <param name="error">The failure description for <see cref="NodeSessionState.Faulted"/>, otherwise null.</param>
        private void SetState(NodeSessionState state, string? error)
        {
            lock (_gate)
            {
                _state = state;
                _lastError = error;
            }
            try
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "状态变更监听器出错。");
            }
        }
    }
}
