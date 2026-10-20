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
        /// <param name="contact">The peer, or null for a "none" choice.</param>
        /// <param name="label">The display text.</param>
        public PeerChoiceViewModel(DHTPeer? contact, string label)
        {
            Contact = contact;
            Label = label;
        }

        /// <summary>Gets the peer, or null for a "none" choice.</summary>
        public DHTPeer? Contact { get; }

        /// <summary>Gets the display text.</summary>
        public string Label { get; }

        /// <summary>Creates a choice for a contact.</summary>
        /// <param name="contact">The contact.</param>
        /// <param name="label">An optional label shown before the identifier.</param>
        /// <returns>The choice.</returns>
        public static PeerChoiceViewModel For(DHTPeer contact, string? label = null)
        {
            var text = (string.IsNullOrEmpty(label) ? string.Empty : label + " · ") + Display.ShortId(contact.NodeId, 8, 4)
                + "  " + Display.PrimaryAddress(contact);
            return new PeerChoiceViewModel(contact, text);
        }

        /// <summary>Builds choices from a session's known peers.</summary>
        /// <param name="session">The session.</param>
        /// <param name="relayOnly">Whether only peers offering relay assistance are included.</param>
        /// <returns>The choices, ordered by label.</returns>
        public static List<PeerChoiceViewModel> FromSession(NodeSession session, bool relayOnly)
        {
            var result = new List<PeerChoiceViewModel>();
            foreach (var peer in session.Peers.Snapshot())
            {
                if (peer.Contact.IsExpired || (relayOnly && (peer.Contact.Capabilities & DHTCapabilities.Relay) == 0))
                {
                    continue;
                }
                result.Add(For(peer.Contact, peer.Label ?? (peer.Sources.HasFlag(PeerSource.Manual) ? "手动" : null)));
            }
            result.Sort((left, right) => string.CompareOrdinal(left.Label, right.Label));
            return result;
        }
    }
}
