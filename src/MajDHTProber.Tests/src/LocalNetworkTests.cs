using System.Collections.Generic;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Builds a loopback test network through the session manager and tests communication end to end.</summary>
    public sealed class LocalNetworkTests
    {
        /// <summary>Local nodes join the primary node, know each other and pass both end-to-end tests.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task LinksLocalNodesAndPassesRoundTrips()
        {
            using var timeout = TestNodes.Timeout();
            using var directory = new TempDirectory();
            var paths = new AppPaths(directory.Path);
            var sink = new LogSink();
            var store = new SettingsStore(paths.SettingsFile, sink.CreateLogger("设置"));
            store.Save(TestNodes.LoopbackSettings());
            using var vault = new IdentityVault(paths.IdentityDirectory, sink.CreateLogger("身份"));
            await using var manager = new NodeSessionManager(store, vault, sink);
            var sessionEvents = 0;
            manager.SessionsChanged += (_, _) => sessionEvents++;

            await manager.Primary.StartAsync(timeout.Token);
            var local = await manager.AddLocalNodeAsync(new LocalNodeRequest(), timeout.Token);

            Assert.True(local.IsRunning);
            Assert.Equal("local-1", local.IdentityName);
            Assert.Equal(2, manager.Sessions.Count);
            Assert.Equal(1, sessionEvents);
            Assert.True(manager.Primary.RoutingTable!.Contains(local.NodeId!));
            Assert.True(local.RoutingTable!.Contains(manager.Primary.NodeId!));
            Assert.True((manager.Primary.Peers.Find(local.NodeId!)!.Sources & PeerSource.LocalNode) != 0);
            Assert.Equal(local.Label, manager.Primary.Peers.Find(local.NodeId!)!.Label);

            var stages = new List<RoundTripStage>();
            var betweenSessions = await manager.RoundTrip.RunAsync(manager.Primary, local, 48 * 1024, true, new InlineProgress(stages), timeout.Token);
            Assert.True(betweenSessions.Succeeded, string.Join("; ", betweenSessions.Stages));
            Assert.Contains(stages, stage => stage.Name == "撤回测试记录" && stage.Succeeded);

            var throughClient = await manager.RoundTrip.RunAsync(local, manager.Primary.GetContactEndpoints()[0], 16 * 1024, false, null, timeout.Token);
            Assert.True(throughClient.Succeeded, string.Join("; ", throughClient.Stages));
            Assert.NotNull(throughClient.Throughput);

            manager.ActiveSession = local;
            await manager.RemoveLocalNodeAsync(local);
            Assert.Same(manager.Primary, manager.ActiveSession);
            Assert.Single(manager.Sessions);
            Assert.Equal(NodeSessionState.Stopped, local.State);
        }

        /// <summary>Saving settings validates them and applies them to the stopped primary node.</summary>
        [Fact]
        public void SavesValidatedSettings()
        {
            using var directory = new TempDirectory();
            var paths = new AppPaths(directory.Path);
            var sink = new LogSink();
            var store = new SettingsStore(paths.SettingsFile, sink.CreateLogger("设置"));
            using var vault = new IdentityVault(null, sink.CreateLogger("身份"));
            var manager = new NodeSessionManager(store, vault, sink);
            var settings = TestNodes.LoopbackSettings();
            settings.PreferredTransport = DHTTransport.Udp;

            manager.SaveSettings(settings);

            Assert.Equal(DHTTransport.Udp, manager.Primary.Settings.PreferredTransport);
            Assert.Equal(DHTTransport.Udp, store.Load().PreferredTransport);
            settings.ListenAddresses = "bad";
            Assert.Throws<SettingsValidationException>(() => manager.SaveSettings(settings));
            Assert.Equal("127.0.0.1", store.Load().ListenAddresses);
        }

        /// <summary>Records progress reports synchronously.</summary>
        private sealed class InlineProgress : System.IProgress<RoundTripStage>
        {
            /// <summary>The recorded stages.</summary>
            private readonly List<RoundTripStage> _stages;

            /// <summary>Creates the recorder.</summary>
            /// <param name="stages">Receives the stages.</param>
            internal InlineProgress(List<RoundTripStage> stages)
            {
                _stages = stages;
            }

            /// <summary>Records one stage.</summary>
            /// <param name="value">The stage.</param>
            public void Report(RoundTripStage value)
            {
                _stages.Add(value);
            }
        }
    }
}
