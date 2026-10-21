using System;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>Records how the prober learned about a remote node; a node can be learned in several ways.</summary>
    [Flags]
    public enum PeerSource
    {
        /// <summary>No source recorded.</summary>
        None = 0,
        /// <summary>The hosted node authenticated an exchange with it and offered it to the routing table.</summary>
        Routing = 1,
        /// <summary>The user added it by endpoint.</summary>
        Manual = 2,
        /// <summary>An iterative lookup returned it as a responsive nearby node.</summary>
        Lookup = 4,
        /// <summary>A provider lookup returned it as a content provider.</summary>
        Provider = 8,
        /// <summary>An endpoint probe reached it.</summary>
        Probe = 16,
        /// <summary>It is another node hosted by this prober process.</summary>
        LocalNode = 32,
        /// <summary>Another contact embedded it as a hinted Traversal node or Forwarder.</summary>
        Hint = 64,
        /// <summary>It answered a LAN broadcast scan.</summary>
        Lan = 128,
    }
}
