#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>Summarizes how well the hosted node is currently connected to a remote node.</summary>
    public enum PeerConnectionState
    {
        /// <summary>Known from referrals or lookups only; no authenticated exchange or probe has confirmed it yet.</summary>
        Discovered = 0,
        /// <summary>The node is a live entry of the hosted node's routing table.</summary>
        Connected = 1,
        /// <summary>A recent authenticated exchange or probe succeeded, but the node is not a routing table entry.</summary>
        Reachable = 2,
        /// <summary>The node failed a request or probe after its last success.</summary>
        Disconnected = 3,
        /// <summary>The node's signed contact lease has expired.</summary>
        Expired = 4,
    }
}
