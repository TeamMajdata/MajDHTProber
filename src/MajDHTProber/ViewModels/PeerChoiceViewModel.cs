using System.Collections.Generic;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One selectable peer in a tool's drop-down list.</summary>
    public sealed class PeerChoiceViewModel
    {
        /// <summary>Creates a choice.</summary>
        /// <param name="peer">The peer, or null for a "none" choice.</param>
        /// <param name="label">The display text.</param>
        public PeerChoiceViewModel(DHTPeer? peer, string label)
        {
            Peer = peer;
            Label = label;
        }

        /// <summary>Gets the peer at its observed endpoints, or null for a "none" choice.</summary>
        public DHTPeer? Peer { get; }

        /// <summary>Gets the display text.</summary>
        public string Label { get; }

        /// <summary>Creates a choice for a peer.</summary>
        /// <param name="peer">The peer.</param>
        /// <param name="label">An optional label shown before the identifier.</param>
        /// <returns>The choice.</returns>
        public static PeerChoiceViewModel For(DHTPeer peer, string? label = null)
        {
            var text = (string.IsNullOrEmpty(label) ? string.Empty : label + " · ") + Display.ShortId(peer.NodeId, 8, 4)
                + "  " + Display.PrimaryAddress(peer);
            return new PeerChoiceViewModel(peer, text);
        }

        /// <summary>Builds choices from a session's known peers.</summary>
        /// <param name="session">The session.</param>
        /// <param name="required">The capabilities a peer must advertise at least one of; zero includes every peer.</param>
        /// <returns>The unexpired peers, ordered by label.</returns>
        public static List<PeerChoiceViewModel> FromSession(NodeSession session, DHTCapabilities required = 0)
        {
            var result = new List<PeerChoiceViewModel>();
            foreach (var peer in session.Peers.Snapshot())
            {
                if (peer.Peer.IsExpired || (required != 0 && (peer.Peer.Capabilities & required) == 0))
                {
                    continue;
                }
                result.Add(For(peer.Peer, peer.Label ?? (peer.Sources.HasFlag(PeerSource.Manual) ? "手动" : null)));
            }
            result.Sort((left, right) => string.CompareOrdinal(left.Label, right.Label));
            return result;
        }
    }
}
