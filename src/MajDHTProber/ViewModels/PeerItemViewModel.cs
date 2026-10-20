using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One row of the peer list.</summary>
    public sealed partial class PeerItemViewModel : TonedViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="snapshot">The peer snapshot.</param>
        /// <param name="now">The current time.</param>
        public PeerItemViewModel(PeerSnapshot snapshot, DateTimeOffset now)
        {
            NodeId = snapshot.NodeId;
            Snapshot = snapshot;
            Update(snapshot, now, null);
        }

        /// <summary>Gets the node identifier.</summary>
        public string NodeId { get; }

        /// <summary>Gets the shortened node identifier.</summary>
        public string ShortId => Display.ShortId(NodeId);

        /// <summary>Gets or sets the latest snapshot.</summary>
        [ObservableProperty]
        public partial PeerSnapshot Snapshot { get; set; }

        /// <summary>Gets or sets the connection state.</summary>
        [ObservableProperty]
        public partial PeerConnectionState State { get; set; }

        /// <summary>Gets or sets the display name: the label, or the shortened identifier.</summary>
        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the connection state text.</summary>
        [ObservableProperty]
        public partial string StateText { get; set; } = string.Empty;

        /// <summary>Gets or sets the primary address text.</summary>
        [ObservableProperty]
        public partial string AddressText { get; set; } = string.Empty;

        /// <summary>Gets or sets the capabilities text.</summary>
        [ObservableProperty]
        public partial string CapabilitiesText { get; set; } = string.Empty;

        /// <summary>Gets or sets the reachability text.</summary>
        [ObservableProperty]
        public partial string ReachabilityText { get; set; } = string.Empty;

        /// <summary>Gets or sets the sources text.</summary>
        [ObservableProperty]
        public partial string SourcesText { get; set; } = string.Empty;

        /// <summary>Gets or sets when the peer was last seen, relative to now.</summary>
        [ObservableProperty]
        public partial string LastSeenText { get; set; } = string.Empty;

        /// <summary>Gets or sets the measured packet loss text.</summary>
        [ObservableProperty]
        public partial string LossText { get; set; } = Display.None;

        /// <summary>Gets or sets whether the peer offers relay assistance.</summary>
        [ObservableProperty]
        public partial bool IsRelay { get; set; }

        /// <summary>Gets or sets whether the user added the peer manually.</summary>
        [ObservableProperty]
        public partial bool IsManual { get; set; }

        /// <summary>Gets or sets whether the peer is another node hosted by this prober.</summary>
        [ObservableProperty]
        public partial bool IsLocalNode { get; set; }

        /// <summary>Gets the signed contact.</summary>
        public DHTPeer Contact => Snapshot.Contact;

        /// <summary>Updates the row.</summary>
        /// <param name="snapshot">The latest snapshot.</param>
        /// <param name="now">The current time.</param>
        /// <param name="statistics">The hosted node's statistics for the peer, or null.</param>
        public void Update(PeerSnapshot snapshot, DateTimeOffset now, DHTPeerStatistics? statistics)
        {
            Snapshot = snapshot;
            State = snapshot.GetState(now);
            StateText = Display.State(State);
            Tone = State switch
            {
                PeerConnectionState.Connected => StatusTone.Success,
                PeerConnectionState.Reachable => StatusTone.Info,
                PeerConnectionState.Disconnected => StatusTone.Danger,
                PeerConnectionState.Expired => StatusTone.Warning,
                _ => StatusTone.Neutral,
            };
            Title = string.IsNullOrEmpty(snapshot.Label) ? ShortId : snapshot.Label + " · " + Display.ShortId(NodeId, 6, 4);
            AddressText = Display.PrimaryAddress(snapshot.Contact);
            CapabilitiesText = Display.Capabilities(snapshot.Contact.Capabilities);
            ReachabilityText = Display.Reachability(snapshot.Contact.Reachability);
            SourcesText = Display.Sources(snapshot.Sources);
            LastSeenText = Display.Relative(snapshot.LastInteractionAt ?? snapshot.LastSeen, now);
            if (statistics != null)
            {
                LossText = statistics.SentPackets == 0 ? Display.None : Display.Percent(statistics.PacketLossRate);
            }
            IsRelay = (snapshot.Contact.Capabilities & DHTCapabilities.Relay) != 0;
            IsManual = (snapshot.Sources & PeerSource.Manual) != 0;
            IsLocalNode = (snapshot.Sources & PeerSource.LocalNode) != 0;
        }

        /// <summary>Checks whether the row matches a search text.</summary>
        /// <param name="search">The case-insensitive search text; empty matches everything.</param>
        /// <returns>Whether the identifier, label, address or capabilities contain the text.</returns>
        public bool Matches(string? search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }
            var text = search.Trim();
            return NodeId.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (Snapshot.Label?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || AddressText.Contains(text, StringComparison.OrdinalIgnoreCase)
                || CapabilitiesText.Contains(text, StringComparison.OrdinalIgnoreCase)
                || SourcesText.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
