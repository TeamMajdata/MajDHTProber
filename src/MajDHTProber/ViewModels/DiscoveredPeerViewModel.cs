using CommunityToolkit.Mvvm.ComponentModel;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One node found by a LAN scan.</summary>
    public sealed partial class DiscoveredPeerViewModel : ViewModelBase
    {
        /// <summary>Creates a row.</summary>
        /// <param name="found">The scan entry.</param>
        /// <param name="isSelf">Whether the node is the active node itself.</param>
        /// <param name="isKnown">Whether the node was already in the peer list before the scan.</param>
        public DiscoveredPeerViewModel(DiscoveredPeer found, bool isSelf, bool isKnown)
        {
            Found = found;
            IsSelf = isSelf;
            IsKnown = isKnown;
            ShortId = Display.ShortId(found.NodeId);
            EndpointText = Display.IPEndPoint(found.Endpoint);
            CapabilitiesText = Display.Capabilities(found.Peer.Capabilities);
            RoundTripText = Display.Duration(found.RoundTrip) + (found.Replies > 1 ? "（" + found.Replies + " 次应答）" : string.Empty);
            StatusText = isSelf ? "本节点" : isKnown ? "已在列表" : string.Empty;
        }

        /// <summary>Gets the scan entry.</summary>
        public DiscoveredPeer Found { get; }

        /// <summary>Gets the node identifier.</summary>
        public string NodeId => Found.NodeId;

        /// <summary>Gets the shortened node identifier.</summary>
        public string ShortId { get; }

        /// <summary>Gets the answering endpoint text.</summary>
        public string EndpointText { get; }

        /// <summary>Gets the capabilities text.</summary>
        public string CapabilitiesText { get; }

        /// <summary>Gets the round trip text, with the number of answers when the node answered more than once.</summary>
        public string RoundTripText { get; }

        /// <summary>Gets whether the node is the active node itself.</summary>
        public bool IsSelf { get; }

        /// <summary>Gets whether the node was already in the peer list before the scan.</summary>
        public bool IsKnown { get; }

        /// <summary>Gets or sets the status text: the relation to the active node, or the outcome of adding it.</summary>
        [ObservableProperty]
        public partial string StatusText { get; set; }

        /// <summary>Gets or sets whether the node was added to the active node through its own Hello during this scan.</summary>
        [ObservableProperty]
        public partial bool IsAdded { get; set; }

        /// <summary>Gets whether the add button applies: the node is neither the active node nor added yet.</summary>
        public bool CanAdd => !IsSelf && !IsAdded;

        /// <summary>Refreshes the derived add state after the added flag changed.</summary>
        /// <param name="value">The new flag.</param>
        partial void OnIsAddedChanged(bool value)
        {
            OnPropertyChanged(nameof(CanAdd));
        }
    }
}
