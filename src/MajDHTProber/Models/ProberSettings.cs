using System.Collections.Generic;
using System.Text.Json.Serialization;
using MajDHTNetwork;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>
    /// The persisted prober configuration. Network fields map onto <see cref="DHTNodeOptions"/> of the primary node; the
    /// remaining fields configure the prober itself.
    /// </summary>
    public sealed class ProberSettings
    {
        /// <summary>The default TCP and UDP port of the primary node.</summary>
        public const int DefaultPort = 42000;

        /// <summary>Gets or sets one or two comma-separated listen addresses, at most one per address family.</summary>
        public string ListenAddresses { get; set; } = "0.0.0.0";

        /// <summary>
        /// Gets or sets whether the listeners are known to accept unsolicited packets from other networks, such as on a fixed
        /// public address or behind a router port mapping; the node then advertises public reachability at once instead of
        /// discovering it through dial-backs.
        /// </summary>
        public bool PubliclyReachable { get; set; }

        /// <summary>Gets or sets whether the TCP listener is enabled.</summary>
        public bool TcpEnabled { get; set; } = true;

        /// <summary>Gets or sets the TCP port, or zero for an assigned port.</summary>
        public int TcpPort { get; set; } = DefaultPort;

        /// <summary>Gets or sets whether the UDP listener is enabled.</summary>
        public bool UdpEnabled { get; set; } = true;

        /// <summary>Gets or sets the UDP port, or zero for an assigned port.</summary>
        public int UdpPort { get; set; } = DefaultPort;

        /// <summary>Gets or sets the transport tried first.</summary>
        public DHTTransport PreferredTransport { get; set; } = DHTTransport.Tcp;

        /// <summary>Gets or sets whether the node advertises and enforces the download capability.</summary>
        public bool CapabilityDownload { get; set; } = true;

        /// <summary>Gets or sets whether the node advertises and enforces the provide capability.</summary>
        public bool CapabilityProvide { get; set; } = true;

        /// <summary>Gets or sets whether the node acts as a Traversal node that coordinates direct connections and dial-backs.</summary>
        public bool CapabilityTraversal { get; set; }

        /// <summary>Gets or sets whether the node acts as a Forwarder that relays encrypted envelopes between peers.</summary>
        public bool CapabilityForwarder { get; set; }

        /// <summary>Gets or sets the bootstrap endpoints in <c>host:port</c> form; at most sixteen are used.</summary>
        public List<string> BootstrapEndpoints { get; set; } = new List<string>();

        /// <summary>Gets or sets the identity work difficulty; it must match the probed network.</summary>
        public int IdentityWorkBits { get; set; } = 12;

        /// <summary>Gets or sets whether node identities are stored on disk so node identifiers survive restarts.</summary>
        public bool PersistIdentity { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the primary node saves its routing table when it stops and re-contacts the saved nodes when it
        /// starts, so it rejoins the network without depending on bootstrap endpoints alone.
        /// </summary>
        public bool PersistRoutingTable { get; set; } = true;

        /// <summary>
        /// Gets or sets whether traversal registration, forwarding sessions, reachability checks and hole punching are enabled.
        /// </summary>
        public bool EnableNatTraversal { get; set; } = true;

        /// <summary>Gets or sets the timeout of each transport attempt in seconds.</summary>
        public double RequestTimeoutSeconds { get; set; } = 5;

        /// <summary>Gets or sets the total UDP transmissions per request.</summary>
        public int UdpAttempts { get; set; } = 3;

        /// <summary>Gets or sets the block size for new publications in bytes.</summary>
        public int BlockSize { get; set; } = 16384;

        /// <summary>Gets or sets the maintenance interval in minutes.</summary>
        public double MaintenanceIntervalMinutes { get; set; } = 5;

        /// <summary>Gets or sets the lease of new publications in hours.</summary>
        public double RecordLifetimeHours { get; set; } = 24;

        /// <summary>Gets or sets the lowest captured log severity.</summary>
        public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;

        /// <summary>Gets or sets the UI theme: <c>Default</c> (follow the system), <c>Light</c> or <c>Dark</c>.</summary>
        public string Theme { get; set; } = "Default";

        /// <summary>Gets or sets whether the primary node starts together with the application.</summary>
        public bool AutoStart { get; set; }

        /// <summary>Gets the capabilities selected by the capability flags.</summary>
        [JsonIgnore]
        public DHTCapabilities Capabilities
        {
            get
            {
                var capabilities = (DHTCapabilities)0;
                if (CapabilityDownload)
                {
                    capabilities |= DHTCapabilities.Download;
                }
                if (CapabilityProvide)
                {
                    capabilities |= DHTCapabilities.Provide;
                }
                if (CapabilityTraversal)
                {
                    capabilities |= DHTCapabilities.Traversal;
                }
                if (CapabilityForwarder)
                {
                    capabilities |= DHTCapabilities.Forwarder;
                }
                return capabilities;
            }
        }

        /// <summary>Creates an independent copy.</summary>
        /// <returns>A deep copy of the settings.</returns>
        public ProberSettings Clone()
        {
            var copy = (ProberSettings)MemberwiseClone();
            copy.BootstrapEndpoints = new List<string>(BootstrapEndpoints);
            return copy;
        }
    }
}
