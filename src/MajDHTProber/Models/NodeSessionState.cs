#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>The lifecycle state of a node hosted by the prober.</summary>
    public enum NodeSessionState
    {
        /// <summary>The node is not running.</summary>
        Stopped,
        /// <summary>The identity is being prepared or listeners are being bound.</summary>
        Starting,
        /// <summary>The node is running.</summary>
        Running,
        /// <summary>The node is draining and shutting down.</summary>
        Stopping,
        /// <summary>The last start attempt failed.</summary>
        Faulted,
    }

    /// <summary>The role of a node hosted by the prober.</summary>
    public enum NodeSessionRole
    {
        /// <summary>The configured main probing node.</summary>
        Primary,
        /// <summary>An additional loopback node used to test communication between nodes.</summary>
        Local,
    }
}
