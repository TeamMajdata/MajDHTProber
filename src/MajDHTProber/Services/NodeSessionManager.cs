using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Owns the prober settings and every node hosted by the prober: the configured primary node and any number of loopback
    /// nodes used to test communication between nodes. One session is active; the UI performs operations through it.
    /// </summary>
    public sealed class NodeSessionManager : IAsyncDisposable
    {
        /// <summary>The identity name of the primary node.</summary>
        public const string PrimaryIdentityName = "primary";
        /// <summary>The maximum number of loopback nodes.</summary>
        public const int MaxLocalNodes = 16;

        /// <summary>The settings persistence.</summary>
        private readonly SettingsStore _store;
        /// <summary>The diagnostics logger.</summary>
        private readonly ILogger _logger;
        /// <summary>Protects the session list, the active session and the settings.</summary>
        private readonly Lock _gate = new Lock();
        /// <summary>The hosted sessions, primary first.</summary>
        private readonly List<NodeSession> _sessions = new List<NodeSession>();
        /// <summary>The session operations act on.</summary>
        private NodeSession _active;
        /// <summary>The current settings.</summary>
        private ProberSettings _settings;

        /// <summary>Creates the manager and the stopped primary session from the stored settings.</summary>
        /// <param name="store">The settings persistence.</param>
        /// <param name="vault">The identity vault.</param>
        /// <param name="sink">The log sink.</param>
        /// <param name="routing">The store of saved routing tables, or null to keep routing tables in memory only.</param>
        public NodeSessionManager(SettingsStore store, IdentityVault vault, LogSink sink, RoutingSnapshotStore? routing = null)
        {
            _store = store;
            Vault = vault;
            Sink = sink;
            Routing = routing ?? new RoutingSnapshotStore(null, sink.CreateLogger("路由表"));
            _logger = sink.CreateLogger("会话管理");
            _settings = store.Load();
            sink.MinimumLevel = _settings.MinimumLogLevel;
            Prober = new EndpointProber(vault, sink);
            RoundTrip = new RoundTripTester(Prober, sink);
            Primary = new NodeSession("主节点", PrimaryIdentityName, NodeSessionRole.Primary, _settings, vault, Prober, Routing, sink);
            _sessions.Add(Primary);
            _active = Primary;
        }

        /// <summary>Raised after a session was added or removed.</summary>
        public event EventHandler? SessionsChanged;

        /// <summary>Raised after the active session changed.</summary>
        public event EventHandler? ActiveSessionChanged;

        /// <summary>Raised after the settings were saved.</summary>
        public event EventHandler? SettingsChanged;

        /// <summary>Gets the identity vault.</summary>
        public IdentityVault Vault { get; }

        /// <summary>Gets the log sink.</summary>
        public LogSink Sink { get; }

        /// <summary>Gets the store of saved routing tables.</summary>
        public RoutingSnapshotStore Routing { get; }

        /// <summary>Gets the endpoint prober.</summary>
        public EndpointProber Prober { get; }

        /// <summary>Gets the end-to-end transfer tester.</summary>
        public RoundTripTester RoundTrip { get; }

        /// <summary>Gets the primary session.</summary>
        public NodeSession Primary { get; }

        /// <summary>Gets a copy of the current settings.</summary>
        public ProberSettings Settings
        {
            get
            {
                lock (_gate)
                {
                    return _settings.Clone();
                }
            }
        }

        /// <summary>Gets a snapshot of the hosted sessions, primary first.</summary>
        public IReadOnlyList<NodeSession> Sessions
        {
            get
            {
                lock (_gate)
                {
                    return _sessions.ToArray();
                }
            }
        }

        /// <summary>Gets or sets the session operations act on.</summary>
        /// <exception cref="ArgumentException">The session is not hosted by this manager.</exception>
        public NodeSession ActiveSession
        {
            get
            {
                lock (_gate)
                {
                    return _active;
                }
            }
            set
            {
                lock (_gate)
                {
                    if (!_sessions.Contains(value))
                    {
                        throw new ArgumentException("The session is not hosted by this manager.", nameof(value));
                    }
                    if (ReferenceEquals(_active, value))
                    {
                        return;
                    }
                    _active = value;
                }
                Raise(ActiveSessionChanged);
            }
        }

        /// <summary>Validates, saves and applies settings; the primary node uses them at its next start.</summary>
        /// <param name="settings">The new settings.</param>
        /// <exception cref="SettingsValidationException">The settings are invalid.</exception>
        /// <exception cref="System.IO.IOException">The settings could not be written.</exception>
        /// <exception cref="UnauthorizedAccessException">The settings directory is not writable.</exception>
        public void SaveSettings(ProberSettings settings)
        {
            var errors = NodeOptionsBuilder.Validate(settings);
            if (errors.Count != 0)
            {
                throw new SettingsValidationException(errors);
            }
            _store.Save(settings);
            lock (_gate)
            {
                _settings = settings.Clone();
            }
            Sink.MinimumLevel = settings.MinimumLogLevel;
            Primary.Settings = settings;
            Raise(SettingsChanged);
        }

        /// <summary>Saves settings and restarts the primary node when it is running.</summary>
        /// <param name="settings">The new settings.</param>
        /// <param name="cancellationToken">Cancels the restart.</param>
        /// <returns>Completion after the restart.</returns>
        /// <exception cref="SettingsValidationException">The settings are invalid.</exception>
        /// <exception cref="System.Net.Sockets.SocketException">A configured port could not be bound.</exception>
        public async Task ApplySettingsAsync(ProberSettings settings, CancellationToken cancellationToken = default)
        {
            SaveSettings(settings);
            if (Primary.IsRunning)
            {
                _logger.LogInformation("设置已更改，正在重启主节点。");
                await Primary.StopAsync().ConfigureAwait(false);
                await Primary.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Starts a new loopback node that joins through the running hosted nodes and connects it with each of them in both
        /// directions through real Hello exchanges over loopback, so the nodes can communicate without any external network.
        /// </summary>
        /// <param name="request">The node request.</param>
        /// <param name="cancellationToken">Cancels identity generation, startup and the connections.</param>
        /// <returns>The new session; it stays in the list in the faulted state when starting fails.</returns>
        /// <exception cref="InvalidOperationException">The maximum number of loopback nodes is reached.</exception>
        /// <exception cref="SettingsValidationException">The derived settings are invalid.</exception>
        /// <exception cref="OperationCanceledException">Starting was cancelled.</exception>
        public async Task<NodeSession> AddLocalNodeAsync(LocalNodeRequest request, CancellationToken cancellationToken = default)
        {
            NodeSession session;
            lock (_gate)
            {
                if (_sessions.Count > MaxLocalNodes)
                {
                    throw new InvalidOperationException($"最多只能创建 {MaxLocalNodes} 个本地节点。");
                }
                var index = NextLocalIndex();
                var bootstrap = new List<IPEndPoint>();
                foreach (var existing in _sessions)
                {
                    foreach (var endpoint in existing.GetContactEndpoints())
                    {
                        if (bootstrap.Count < NodeOptionsBuilder.MaxBootstrapEndpoints)
                        {
                            bootstrap.Add(endpoint);
                        }
                    }
                }
                var settings = DeriveLocalSettings(_settings, bootstrap, request);
                session = new NodeSession("本地节点 " + index, "local-" + index, NodeSessionRole.Local, settings, Vault, Prober, Routing, Sink);
                _sessions.Add(session);
            }
            Raise(SessionsChanged);
            _logger.LogInformation("正在创建 {Label}。", session.Label);
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            foreach (var other in Sessions)
            {
                if (!ReferenceEquals(other, session) && other.IsRunning)
                {
                    await other.ConnectLocalPeerAsync(session, cancellationToken).ConfigureAwait(false);
                    await session.ConnectLocalPeerAsync(other, cancellationToken).ConfigureAwait(false);
                }
            }
            return session;
        }

        /// <summary>Stops and removes a loopback node.</summary>
        /// <param name="session">The session.</param>
        /// <returns>Completion after the node stopped.</returns>
        /// <exception cref="InvalidOperationException">The session is the primary node.</exception>
        public async Task RemoveLocalNodeAsync(NodeSession session)
        {
            if (ReferenceEquals(session, Primary))
            {
                throw new InvalidOperationException("主节点不能被移除。");
            }
            await session.StopAsync().ConfigureAwait(false);
            var activeChanged = false;
            lock (_gate)
            {
                _sessions.Remove(session);
                if (ReferenceEquals(_active, session))
                {
                    _active = Primary;
                    activeChanged = true;
                }
            }
            foreach (var other in Sessions)
            {
                var id = session.NodeId;
                if (id != null)
                {
                    other.Peers.SetLabel(id, session.Label + "（已移除）");
                }
            }
            _logger.LogInformation("已移除 {Label}。", session.Label);
            Raise(SessionsChanged);
            if (activeChanged)
            {
                Raise(ActiveSessionChanged);
            }
        }

        /// <summary>Starts the primary node when the settings ask for an automatic start.</summary>
        /// <param name="cancellationToken">Cancels the start.</param>
        /// <returns>Completion after the attempt; failures are logged by the session.</returns>
        public async Task AutoStartAsync(CancellationToken cancellationToken = default)
        {
            if (!Settings.AutoStart)
            {
                return;
            }
            try
            {
                await Primary.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning("主节点自动启动失败：{Error}", exception.Message);
            }
        }

        /// <summary>Stops every hosted node.</summary>
        /// <returns>Completion after every node stopped.</returns>
        public async ValueTask DisposeAsync()
        {
            foreach (var session in Sessions)
            {
                try
                {
                    await session.StopAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "停止 {Label} 时出错。", session.Label);
                }
            }
        }

        /// <summary>Derives the settings of a loopback node from the primary settings.</summary>
        /// <param name="primary">The primary settings.</param>
        /// <param name="bootstrap">The endpoints of the running hosted nodes.</param>
        /// <param name="request">The node request.</param>
        /// <returns>
        /// Settings listening on 127.0.0.1 with assigned ports and the protocol parameters of the primary; the routing table is
        /// never saved because the assigned ports change with every start.
        /// </returns>
        internal static ProberSettings DeriveLocalSettings(ProberSettings primary, IEnumerable<IPEndPoint> bootstrap, LocalNodeRequest request)
        {
            var settings = primary.Clone();
            settings.ListenAddresses = IPAddress.Loopback.ToString();
            settings.AdvertisedAddresses = string.Empty;
            settings.TcpEnabled = true;
            settings.TcpPort = 0;
            settings.UdpEnabled = true;
            settings.UdpPort = 0;
            settings.CapabilityDownload = (request.Capabilities & DHTCapabilities.Download) != 0;
            settings.CapabilityProvide = (request.Capabilities & DHTCapabilities.Provide) != 0;
            settings.CapabilityRelay = (request.Capabilities & DHTCapabilities.Relay) != 0;
            settings.PersistIdentity = true;
            settings.PersistRoutingTable = false;
            settings.AutoStart = false;
            settings.BootstrapEndpoints = new List<string>();
            foreach (var endpoint in bootstrap)
            {
                if (settings.BootstrapEndpoints.Count < NodeOptionsBuilder.MaxBootstrapEndpoints)
                {
                    settings.BootstrapEndpoints.Add(EndpointParser.Format(endpoint));
                }
            }
            return settings;
        }

        /// <summary>Chooses the smallest loopback node index not used by a hosted session.</summary>
        /// <returns>The index, starting at one.</returns>
        /// <remarks>The caller must hold the lock; reusing indices reuses stored identities.</remarks>
        private int NextLocalIndex()
        {
            for (var index = 1; ; index++)
            {
                var name = "local-" + index;
                if (!_sessions.Exists(session => session.IdentityName == name))
                {
                    return index;
                }
            }
        }

        /// <summary>Raises an event while isolating listener failures.</summary>
        /// <param name="handler">The event handlers.</param>
        private void Raise(EventHandler? handler)
        {
            try
            {
                handler?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "会话事件监听器出错。");
            }
        }
    }
}
