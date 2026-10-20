using System.IO;
using System.Text;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Logging;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Microsoft.Extensions.Logging;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Covers the identity vault, settings persistence and record input parsing.</summary>
    public sealed class StorageTests
    {
        /// <summary>A persisted identity keeps its node identifier and can be deleted.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task PersistsIdentities()
        {
            using var directory = new TempDirectory();
            var sink = new LogSink();
            string nodeId;
            using (var vault = new IdentityVault(directory.Path, sink.CreateLogger("身份")))
            {
                var identity = await vault.GetOrCreateAsync("primary", 0, true, TestContext.Current.CancellationToken);
                nodeId = identity.NodeId;
                Assert.Same(identity, await vault.GetOrCreateAsync("primary", 0, true, TestContext.Current.CancellationToken));
                Assert.Equal(nodeId, vault.TryGetCachedNodeId("primary"));
            }
            Assert.True(File.Exists(Path.Combine(directory.Path, "primary.seed")));
            Assert.Equal(32, new FileInfo(Path.Combine(directory.Path, "primary.seed")).Length);

            using (var reloaded = new IdentityVault(directory.Path, sink.CreateLogger("身份")))
            {
                Assert.Equal(nodeId, (await reloaded.GetOrCreateAsync("primary", 0, true, TestContext.Current.CancellationToken)).NodeId);
                Assert.True(reloaded.Delete("primary"));
                Assert.False(File.Exists(Path.Combine(directory.Path, "primary.seed")));
                Assert.NotEqual(nodeId, (await reloaded.GetOrCreateAsync("primary", 0, true, TestContext.Current.CancellationToken)).NodeId);
            }
            Assert.DoesNotContain(sink.Snapshot(), entry => entry.Message.Contains("seed", System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Memory-only identities never touch the disk, and invalid names are refused.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task KeepsMemoryOnlyIdentities()
        {
            using var directory = new TempDirectory();
            using var vault = new IdentityVault(directory.Path, new LogSink().CreateLogger("身份"));

            await vault.GetOrCreateAsync("probe", 0, false, TestContext.Current.CancellationToken);

            Assert.Empty(Directory.GetFiles(directory.Path));
            await Assert.ThrowsAsync<System.ArgumentException>(() => vault.GetOrCreateAsync("../escape", 0, false, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() => vault.GetOrCreateAsync("probe", 21, false, TestContext.Current.CancellationToken));
        }

        /// <summary>Settings round-trip through JSON and unreadable files fall back to defaults.</summary>
        [Fact]
        public void RoundTripsSettings()
        {
            using var directory = new TempDirectory();
            var path = Path.Combine(directory.Path, "nested", "settings.json");
            var store = new SettingsStore(path, new LogSink().CreateLogger("设置"));
            var settings = new ProberSettings
            {
                ListenAddresses = "0.0.0.0, ::",
                PreferredTransport = DHTTransport.Udp,
                CapabilityRelay = true,
                BootstrapEndpoints = { "127.0.0.1:42000" },
                MinimumLogLevel = LogLevel.Debug,
                Theme = "Dark",
            };

            store.Save(settings);
            var loaded = store.Load();

            Assert.Equal("0.0.0.0, ::", loaded.ListenAddresses);
            Assert.Equal(DHTTransport.Udp, loaded.PreferredTransport);
            Assert.True(loaded.CapabilityRelay);
            Assert.Equal(new[] { "127.0.0.1:42000" }, loaded.BootstrapEndpoints);
            Assert.Equal(LogLevel.Debug, loaded.MinimumLogLevel);
            Assert.Equal("Dark", loaded.Theme);
            Assert.Contains("\"Udp\"", File.ReadAllText(path), System.StringComparison.Ordinal);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(new ProberSettings().ListenAddresses, store.Load().ListenAddresses);
        }

        /// <summary>Keys and record codes are recognized and invalid input is explained.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ParsesRecordInput()
        {
            await using var node = await TestNodes.StartAsync();
            using var source = new MemoryStream(Encoding.UTF8.GetBytes("record input"));
            var record = await node.PublishAsync("tests/input", source, cancellationToken: TestContext.Current.CancellationToken);
            var code = RecordInput.ToCode(record);

            Assert.True(RecordInput.TryParse(record.Key.ToString(), out var key, out var noRecord, out _));
            Assert.Equal(record.Key, key);
            Assert.Null(noRecord);

            Assert.True(RecordInput.TryParse(code, out var codeKey, out var parsed, out _));
            Assert.Equal(record.Key, codeKey);
            Assert.Equal(record.FileHash, parsed!.FileHash);

            Assert.False(RecordInput.TryParse("nonsense", out _, out _, out var error));
            Assert.NotEmpty(error);
            Assert.False(RecordInput.TryParseCode("AAAA", out _));
            Assert.False(RecordInput.TryParseKey(new string('z', 64) + ":name", out _));
        }
    }
}
