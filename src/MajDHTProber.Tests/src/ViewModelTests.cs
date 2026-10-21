using System;
using System.Linq;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;
using MajDHTProber.ViewModels;
using Microsoft.Extensions.Logging;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Exercises view model logic without a UI thread.</summary>
    public sealed class ViewModelTests
    {
        /// <summary>The peer list filters by state and search text and keeps counts per state.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task FiltersPeers()
        {
            await using var first = await TestNodes.StartAsync();
            await using var second = await TestNodes.StartAsync(DHTCapabilities.Traversal);
            using var fixture = new ContextFixture();
            var peers = fixture.Manager.Primary.Peers;
            peers.Observe(first.LocalPeer, PeerSource.Routing, true, first.LocalPeer.Endpoints[0]);
            peers.Observe(new DHTPeer(second.LocalContact, Array.Empty<DHTEndpoint>()), PeerSource.Lookup);

            var viewModel = new PeersViewModel(fixture.Context);

            Assert.Equal(2, viewModel.VisiblePeers.Count);
            Assert.Equal(PeerConnectionState.Connected, viewModel.VisiblePeers[0].State);
            Assert.Equal(2, viewModel.Filters[0].Count);
            Assert.Equal(1, viewModel.Filters[1].Count);

            viewModel.SelectedFilter = viewModel.Filters.First(filter => filter.State == PeerConnectionState.Discovered);
            Assert.Equal(second.NodeId, Assert.Single(viewModel.VisiblePeers).NodeId);

            viewModel.SelectedFilter = viewModel.Filters[0];
            viewModel.SearchText = first.NodeId.Substring(10, 12).ToUpperInvariant();
            Assert.Equal(first.NodeId, Assert.Single(viewModel.VisiblePeers).NodeId);

            viewModel.SearchText = "traversal";
            Assert.Equal(second.NodeId, Assert.Single(viewModel.VisiblePeers).NodeId);

            viewModel.SelectedPeer = viewModel.VisiblePeers[0];
            Assert.NotNull(viewModel.Detail);
            Assert.True(viewModel.Detail!.HasNoEndpoints);
            Assert.True(viewModel.Detail.Item.IsTraversal);
            Assert.Empty(viewModel.Detail.TraversalCandidates);

            peers.Forget(second.NodeId);
            Assert.Empty(viewModel.VisiblePeers);
            Assert.Null(viewModel.Detail);
        }

        /// <summary>The scan panel lists the answering nodes and adds one through the active node's own Hello.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ScansAndAddsLanNodes()
        {
            using var timeout = TestNodes.Timeout();
            await using var remote = await TestNodes.StartAsync();
            using var fixture = new ContextFixture();
            await fixture.Manager.Primary.StartAsync(timeout.Token);
            var viewModel = new PeersViewModel(fixture.Context) { ScanTargets = new[] { TestNodes.UdpEndpoint(remote) } };

            viewModel.ToggleScanFormCommand.Execute(null);
            Assert.True(viewModel.IsScanFormOpen);
            Assert.Equal(ProberSettings.DefaultPort, viewModel.ScanPort);

            viewModel.ScanPort = 0;
            await viewModel.ScanLanCommand.ExecuteAsync(null);
            Assert.True(viewModel.ScanResult.IsWarning);
            Assert.Empty(viewModel.DiscoveredPeers);

            viewModel.ScanPort = ProberSettings.DefaultPort;
            await viewModel.ScanLanCommand.ExecuteAsync(null);
            Assert.True(viewModel.ScanResult.IsSuccess, viewModel.ScanResult.Text);
            var row = Assert.Single(viewModel.DiscoveredPeers);
            Assert.Equal(remote.NodeId, row.NodeId);
            Assert.True(row.CanAdd);
            Assert.False(row.IsKnown);
            Assert.Equal(PeerSource.Lan, fixture.Manager.Primary.Peers.Find(remote.NodeId)!.Sources);

            await viewModel.AddDiscoveredCommand.ExecuteAsync(row);
            Assert.True(row.IsAdded);
            Assert.False(row.CanAdd);
            Assert.True(viewModel.ScanResult.IsSuccess, viewModel.ScanResult.Text);
            var peer = fixture.Manager.Primary.Peers.Find(remote.NodeId)!;
            Assert.True((peer.Sources & PeerSource.Manual) != 0);
            Assert.True(peer.InRoutingTable);
            Assert.Equal(remote.NodeId, viewModel.SelectedPeer?.NodeId);

            await viewModel.ScanLanCommand.ExecuteAsync(null);
            Assert.True(Assert.Single(viewModel.DiscoveredPeers).IsKnown);
        }

        /// <summary>Adding a node without an endpoint explains what is missing.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ExplainsMissingEndpoint()
        {
            using var fixture = new ContextFixture();
            var viewModel = new PeersViewModel(fixture.Context);

            await viewModel.AddPeerCommand.ExecuteAsync(null);

            Assert.True(viewModel.AddResult.IsVisible);
            Assert.True(viewModel.AddResult.IsWarning);
        }

        /// <summary>Settings edits mark the page dirty, invalid values are listed and valid values are saved.</summary>
        [Fact]
        public void ValidatesAndSavesSettings()
        {
            using var fixture = new ContextFixture();
            var viewModel = new SettingsViewModel(fixture.Context);
            Assert.False(viewModel.IsDirty);

            viewModel.ListenAddresses = "999.0.0.1";
            Assert.True(viewModel.IsDirty);
            viewModel.SaveCommand.Execute(null);
            Assert.True(viewModel.HasErrors);
            Assert.Contains(viewModel.Errors, error => error.Contains("999.0.0.1", StringComparison.Ordinal));

            viewModel.ListenAddresses = "127.0.0.1";
            viewModel.PreferredTransportIndex = 1;
            viewModel.SaveCommand.Execute(null);
            Assert.False(viewModel.HasErrors);
            Assert.False(viewModel.IsDirty);
            Assert.Equal(DHTTransport.Udp, fixture.Manager.Settings.PreferredTransport);

            viewModel.ThemeIndex = 2;
            Assert.Equal("Dark", fixture.Interaction.Theme);
            viewModel.RevertCommand.Execute(null);
            Assert.False(viewModel.IsDirty);
            Assert.Equal(0, viewModel.ThemeIndex);
        }

        /// <summary>The log page shows captured events, filters them and follows clears.</summary>
        [Fact]
        public void FiltersLogEntries()
        {
            using var fixture = new ContextFixture();
            var sink = fixture.Manager.Sink;
            sink.MinimumLevel = LogLevel.Debug;
            using var viewModel = new LogsViewModel(fixture.Context);
            var initial = viewModel.Entries.Count;

            sink.Write(LogLevel.Debug, "甲", "调试消息");
            sink.Write(LogLevel.Warning, "乙", "警告消息");
            viewModel.Flush();
            Assert.Equal(initial + 2, viewModel.Entries.Count);
            Assert.Contains("乙", viewModel.Categories);

            viewModel.SelectedLevelFilter = viewModel.LevelFilters.First(option => option.Level == LogLevel.Warning);
            Assert.All(viewModel.Entries, entry => Assert.True(entry.Level >= LogLevel.Warning));

            viewModel.SelectedLevelFilter = viewModel.LevelFilters[0];
            viewModel.SearchText = "调试";
            Assert.Equal("调试消息", Assert.Single(viewModel.Entries).Message);

            viewModel.SearchText = string.Empty;
            viewModel.SelectedCategory = "乙";
            Assert.Equal("警告消息", Assert.Single(viewModel.Entries).Message);

            viewModel.IsPaused = true;
            sink.Write(LogLevel.Warning, "乙", "暂停期间");
            viewModel.Flush();
            Assert.Single(viewModel.Entries);
            viewModel.IsPaused = false;
            Assert.Equal(2, viewModel.Entries.Count);

            viewModel.ClearCommand.Execute(null);
            Assert.Empty(viewModel.Entries);
        }

        /// <summary>The overview reflects a running node and the pull form rejects unknown input.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ReflectsRunningNode()
        {
            using var fixture = new ContextFixture();
            var sessions = new System.Collections.ObjectModel.ObservableCollection<SessionItemViewModel> { new SessionItemViewModel(fixture.Manager.Primary) };
            var overview = new OverviewViewModel(fixture.Context, sessions);
            Assert.True(overview.Active.CanStart);

            await overview.StartCommand.ExecuteAsync(null);

            Assert.True(overview.Active.IsRunning);
            Assert.Equal(fixture.Manager.Primary.NodeId, overview.NodeIdText);
            Assert.Single(overview.Endpoints);
            Assert.False(overview.ShowPrivateWarning);

            var content = new ContentViewModel(fixture.Context) { DownloadInput = "nothing" };
            await content.DownloadCommand.ExecuteAsync(null);
            Assert.True(content.DownloadResult.IsWarning);

            await content.PublishCommand.ExecuteAsync(null);
            Assert.True(content.PublishResult.IsSuccess, content.PublishResult.Text);
            Assert.Single(content.Records);
            content.UseForDownloadCommand.Execute(null);
            await content.DownloadCommand.ExecuteAsync(null);
            Assert.True(content.DownloadResult.IsSuccess, content.DownloadResult.Text);
            Assert.True(content.HasTextPreview);
            Assert.Equal("Hello from MajDHT Prober!", content.PreviewText);

            await overview.StopCommand.ExecuteAsync(null);
            Assert.True(overview.Active.CanStart);
        }

        /// <summary>Creates a session manager and view model context in a temporary directory.</summary>
        private sealed class ContextFixture : IDisposable
        {
            /// <summary>The temporary data directory.</summary>
            private readonly TempDirectory _directory = new TempDirectory();
            /// <summary>The identity vault.</summary>
            private readonly IdentityVault _vault;

            /// <summary>Creates the fixture with loopback settings.</summary>
            internal ContextFixture()
            {
                var sink = new LogSink();
                var paths = new AppPaths(_directory.Path);
                var store = new SettingsStore(paths.SettingsFile, sink.CreateLogger("设置"));
                store.Save(TestNodes.LoopbackSettings());
                _vault = new IdentityVault(null, sink.CreateLogger("身份"));
                Manager = new NodeSessionManager(store, _vault, sink);
                Interaction = new NullUserInteraction();
                Context = new ProberContext(Manager, InlineUiDispatcher.Instance, Interaction, new NotificationCenter(InlineUiDispatcher.Instance));
            }

            /// <summary>Gets the session manager.</summary>
            internal NodeSessionManager Manager { get; }

            /// <summary>Gets the recording user interaction.</summary>
            internal NullUserInteraction Interaction { get; }

            /// <summary>Gets the view model context.</summary>
            internal ProberContext Context { get; }

            /// <summary>Stops every node and deletes the directory.</summary>
            public void Dispose()
            {
                Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _vault.Dispose();
                _directory.Dispose();
            }
        }
    }
}
