using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Covers the display helpers, previews, routing math, collection synchronization and the progress stream.</summary>
    public sealed class FormattingTests
    {
        /// <summary>Byte counts use binary units.</summary>
        /// <param name="bytes">The byte count.</param>
        /// <param name="expected">The expected text.</param>
        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1536L, "1.50 KiB")]
        [InlineData(16L * 1024 * 1024, "16.0 MiB")]
        public void FormatsBytes(long bytes, string expected)
        {
            Assert.Equal(expected, Display.Bytes(bytes));
        }

        /// <summary>Durations pick a readable unit.</summary>
        [Fact]
        public void FormatsDurations()
        {
            Assert.Equal(Display.None, Display.Duration(null));
            Assert.Equal("500 µs", Display.Duration(TimeSpan.FromMilliseconds(0.5)));
            Assert.Equal("12.3 ms", Display.Duration(TimeSpan.FromMilliseconds(12.34)));
            Assert.Equal("1.50 s", Display.Duration(TimeSpan.FromSeconds(1.5)));
        }

        /// <summary>Long identifiers are shortened to head and tail.</summary>
        [Fact]
        public void ShortensIdentifiers()
        {
            var id = new string('a', 30) + new string('b', 34);

            Assert.Equal("aaaaaaaaaa…bbbbbb", Display.ShortId(id));
            Assert.Equal("short", Display.ShortId("short"));
            Assert.Equal(Display.None, Display.ShortId(null));
        }

        /// <summary>Capabilities, reachability and peer sources are listed in Chinese.</summary>
        [Fact]
        public void FormatsProtocolFlags()
        {
            Assert.Equal("下载 · 提供 · Traversal · Forwarder",
                Display.Capabilities(DHTCapabilities.Download | DHTCapabilities.Provide | DHTCapabilities.Traversal | DHTCapabilities.Forwarder));
            Assert.Equal("无", Display.Capabilities(0));
            Assert.Equal("私有（经 Forwarder）", Display.Reachability(DHTReachability.Private));
            Assert.Equal("路由 · 提示", Display.Sources(MajDHTProber.Models.PeerSource.Routing | MajDHTProber.Models.PeerSource.Hint));
        }

        /// <summary>Observed endpoints are listed with their ports, and the newest observation leads the address summary.</summary>
        [Fact]
        public void FormatsObservedEndpoints()
        {
            var contact = DHTContact.Create(new DHTIdentity(), DHTCapabilities.Download);
            var older = new DHTEndpoint(System.Net.IPAddress.Parse("203.0.113.5"), 42000, 0);
            var newer = new DHTEndpoint(System.Net.IPAddress.Parse("198.51.100.7"), 0, 42001);

            Assert.Equal("203.0.113.5 · TCP 42000", Display.Endpoint(older));
            Assert.Equal("198.51.100.7 · UDP 42001（+1）", Display.PrimaryAddress(new DHTPeer(contact, new[] { older, newer })));
            Assert.Equal(Display.None, Display.PrimaryAddress(new DHTPeer(contact, Array.Empty<DHTEndpoint>())));
        }

        /// <summary>Relative times are formatted against the given now.</summary>
        [Fact]
        public void FormatsRelativeTimes()
        {
            var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

            Assert.Equal("刚刚", Display.Relative(now.AddSeconds(-2), now));
            Assert.Equal("30 秒前", Display.Relative(now.AddSeconds(-30), now));
            Assert.Equal("5 分钟前", Display.Relative(now.AddMinutes(-5), now));
            Assert.Equal("已过期", Display.Future(now.AddSeconds(-1), now));
            Assert.Equal("3 小时后", Display.Future(now.AddHours(3.5), now));
        }

        /// <summary>The hex dump shows offsets, bytes and printable characters, and reports omitted bytes.</summary>
        [Fact]
        public void DumpsHex()
        {
            var data = Encoding.ASCII.GetBytes("Hello, MajDHT!\n\u0001\u0002ABC");

            var dump = HexDump.Format(data, 16);

            Assert.StartsWith("00000000  48 65 6c 6c 6f 2c 20 4d  61 6a 44 48 54 21 0a 01  |Hello, MajDHT!..|", dump, StringComparison.Ordinal);
            Assert.Contains("其余 4 字节未显示", dump, StringComparison.Ordinal);
        }

        /// <summary>Text previews accept UTF-8 text, cut multi-byte sequences cleanly and reject binary content.</summary>
        [Fact]
        public void DecodesTextPreviews()
        {
            var text = Encoding.UTF8.GetBytes("你好，世界\r\n");

            Assert.True(ContentPreview.TryDecodeText(text, 1024, out var decoded));
            Assert.Equal("你好，世界\r\n", decoded);
            Assert.True(ContentPreview.TryDecodeText(text, 4, out var cut));
            Assert.Equal("你", cut);
            Assert.False(ContentPreview.TryDecodeText(new byte[] { 0x00, 0x01, 0x02 }, 1024, out _));
            Assert.False(ContentPreview.TryDecodeText(new byte[] { 0xff, 0xfe }, 1024, out _));
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentPreview.Sha256(Encoding.ASCII.GetBytes("abc")));
        }

        /// <summary>Shared prefix lengths follow the XOR distance.</summary>
        [Fact]
        public void ComputesCommonPrefix()
        {
            var zero = new string('0', 64);
            var top = "8" + new string('0', 63);
            var low = new string('0', 63) + "1";

            Assert.Equal(256, RoutingMath.CommonPrefixBits(zero, zero));
            Assert.Equal(0, RoutingMath.CommonPrefixBits(zero, top));
            Assert.Equal(255, RoutingMath.CommonPrefixBits(zero, low));
            Assert.True(RoutingMath.IsRoutingId(RoutingMath.RandomId()));
            Assert.False(RoutingMath.IsRoutingId("ABC"));
            Assert.Throws<FormatException>(() => RoutingMath.CommonPrefixBits(zero, "xyz"));
        }

        /// <summary>Synchronization reorders, inserts and removes items in place.</summary>
        [Fact]
        public void SynchronizesCollectionsInPlace()
        {
            var a = new object();
            var b = new object();
            var c = new object();
            var d = new object();
            var target = new ObservableCollection<object> { a, b, c };

            Assert.True(CollectionSync.Sync(target, new[] { c, a, d }));
            Assert.Equal(new[] { c, a, d }, target);
            Assert.False(CollectionSync.Sync(target, new[] { c, a, d }));
            Assert.True(CollectionSync.Sync(target, Array.Empty<object>()));
            Assert.Empty(target);
            _ = b;
        }

        /// <summary>The progress stream forwards writes and reports running totals.</summary>
        [Fact]
        public void ReportsWriteProgress()
        {
            using var destination = new MemoryStream();
            var reports = new System.Collections.Generic.List<long>();
            using (var stream = new ProgressStream(destination, new InlineProgress(reports)))
            {
                stream.Write(new byte[10], 0, 10);
                stream.Write(new byte[5]);
                Assert.Equal(15, stream.BytesWritten);
                Assert.False(stream.CanRead);
                Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
            }

            Assert.Equal(new long[] { 10, 15 }, reports);
            Assert.Equal(15, destination.Length);
            Assert.True(destination.CanWrite);
        }

        /// <summary>Records progress reports synchronously.</summary>
        private sealed class InlineProgress : IProgress<long>
        {
            /// <summary>The recorded reports.</summary>
            private readonly System.Collections.Generic.List<long> _reports;

            /// <summary>Creates the recorder.</summary>
            /// <param name="reports">Receives the reports.</param>
            internal InlineProgress(System.Collections.Generic.List<long> reports)
            {
                _reports = reports;
            }

            /// <summary>Records one report.</summary>
            /// <param name="value">The reported value.</param>
            public void Report(long value)
            {
                _reports.Add(value);
            }
        }
    }
}
