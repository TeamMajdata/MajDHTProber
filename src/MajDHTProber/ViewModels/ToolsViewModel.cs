using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MajDHTProber.ViewModels.Tools;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>The debugging tools page, grouping the individual tools as tabs.</summary>
    public sealed partial class ToolsViewModel : PageViewModel
    {
        /// <summary>The tab index of the end-to-end test.</summary>
        private const int RoundTripTab = 4;
        /// <summary>The tab index of the statistics.</summary>
        private const int StatisticsTab = 5;

        /// <summary>Counts ticks to throttle statistics refreshes.</summary>
        private int _ticks;

        /// <summary>Creates the page.</summary>
        /// <param name="context">The shared services.</param>
        public ToolsViewModel(ProberContext context)
            : base(context)
        {
            Ping = new PingToolViewModel(context);
            Lookup = new LookupToolViewModel(context);
            Block = new BlockToolViewModel(context);
            Nat = new NatToolViewModel(context);
            RoundTrip = new RoundTripToolViewModel(context);
            Statistics = new StatisticsToolViewModel(context);
        }

        /// <summary>Gets the endpoint probe tool.</summary>
        public PingToolViewModel Ping { get; }

        /// <summary>Gets the lookup tool.</summary>
        public LookupToolViewModel Lookup { get; }

        /// <summary>Gets the block read tool.</summary>
        public BlockToolViewModel Block { get; }

        /// <summary>Gets the relay and hole punching tool.</summary>
        public NatToolViewModel Nat { get; }

        /// <summary>Gets the end-to-end communication test.</summary>
        public RoundTripToolViewModel RoundTrip { get; }

        /// <summary>Gets the transmission statistics.</summary>
        public StatisticsToolViewModel Statistics { get; }

        /// <summary>Gets or sets the selected tab.</summary>
        [ObservableProperty]
        public partial int SelectedTabIndex { get; set; }

        /// <summary>Reloads the choice lists when the page becomes visible.</summary>
        public override void OnActivated()
        {
            ReloadChoices();
        }

        /// <summary>Reloads the choice lists after the active session changed.</summary>
        public override void OnActiveSessionChanged()
        {
            ReloadChoices();
        }

        /// <summary>Refreshes the statistics tab while it is visible.</summary>
        /// <param name="now">The current time.</param>
        public override void OnTick(DateTimeOffset now)
        {
            if (SelectedTabIndex == StatisticsTab && ++_ticks % 2 == 0)
            {
                Statistics.Refresh(now);
            }
        }

        /// <summary>Reloads the tab content that depends on the selected tab.</summary>
        /// <param name="value">The new tab index.</param>
        partial void OnSelectedTabIndexChanged(int value)
        {
            ReloadChoices();
        }

        /// <summary>Reloads every peer and session choice list.</summary>
        private void ReloadChoices()
        {
            Block.RefreshRelays();
            Nat.RefreshChoices();
            if (SelectedTabIndex == RoundTripTab || RoundTrip.Targets.Count == 0)
            {
                RoundTrip.RefreshTargets();
            }
            Statistics.Refresh(DateTimeOffset.UtcNow);
        }
    }
}
