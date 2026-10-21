using System;
using System.Net;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#nullable enable

namespace MajDHTProber.Tests
{
    /// <summary>Covers endpoint parsing and the conversion of settings into node options.</summary>
    public sealed class ParsingTests
    {
        /// <summary>Literal IPv4 and bracketed IPv6 endpoints parse; malformed ones report a reason.</summary>
        /// <param name="text">The input.</param>
        /// <param name="valid">Whether the input is a valid endpoint.</param>
        [Theory]
        [InlineData("127.0.0.1:42000", true)]
        [InlineData(" 192.168.1.20:1 ", true)]
        [InlineData("[::1]:42000", true)]
        [InlineData("127.0.0.1", false)]
        [InlineData("127.0.0.1:0", false)]
        [InlineData("127.0.0.1:70000", false)]
        [InlineData("0.0.0.0:42000", false)]
        [InlineData("224.0.0.1:42000", false)]
        [InlineData("::1:42000", false)]
        [InlineData("", false)]
        [InlineData("not an endpoint", false)]
        public void ParsesLiteralEndpoints(string text, bool valid)
        {
            var parsed = EndpointParser.TryParse(text, out var endpoint, out var error);

            Assert.Equal(valid, parsed);
            Assert.Equal(valid, endpoint != null);
            Assert.Equal(valid, error.Length == 0);
        }

        /// <summary>IPv6 endpoints are formatted with brackets so they can be parsed back.</summary>
        [Fact]
        public void FormatsIPv6WithBrackets()
        {
            Assert.Equal("[::1]:5", EndpointParser.Format(new IPEndPoint(IPAddress.IPv6Loopback, 5)));
            Assert.Equal("127.0.0.1:5", EndpointParser.Format(new IPEndPoint(IPAddress.Loopback, 5)));
        }

        /// <summary>Host names are resolved through DNS.</summary>
        /// <returns>Completion after resolution.</returns>
        [Fact]
        public async Task ResolvesHostNames()
        {
            var endpoint = await EndpointParser.ResolveAsync("localhost:42000", TestContext.Current.CancellationToken);

            Assert.True(IPAddress.IsLoopback(endpoint.Address));
            Assert.Equal(42000, endpoint.Port);
            await Assert.ThrowsAsync<FormatException>(() => EndpointParser.ResolveAsync("localhost", TestContext.Current.CancellationToken));
        }

        /// <summary>Default settings build valid options with the documented defaults.</summary>
        [Fact]
        public void BuildsOptionsFromDefaults()
        {
            var settings = new ProberSettings { BootstrapEndpoints = { "203.0.113.5:42000", "[2001:db8::1]:42000" } };

            var options = NodeOptionsBuilder.Build(settings, NullLogger.Instance);

            Assert.Empty(NodeOptionsBuilder.Validate(settings));
            Assert.Equal(IPAddress.Any, Assert.Single(options.ListenAddresses));
            Assert.Equal(42000, options.TcpPort);
            Assert.Equal(42000, options.UdpPort);
            Assert.Equal(2, options.BootstrapEndpoints.Count);
            Assert.Equal(DHTCapabilities.Download | DHTCapabilities.Provide, options.Capabilities);
            Assert.Equal(12, options.IdentityWorkBits);
            Assert.Equal(TimeSpan.FromSeconds(5), options.RequestTimeout);
        }

        /// <summary>Disabled listeners map to null ports and capability flags combine.</summary>
        [Fact]
        public void MapsListenersAndCapabilities()
        {
            var settings = new ProberSettings
            {
                UdpEnabled = false,
                CapabilityTraversal = true,
                CapabilityForwarder = true,
                CapabilityDownload = false,
                PubliclyReachable = true,
            };

            var options = NodeOptionsBuilder.Build(settings, NullLogger.Instance);

            Assert.Null(options.UdpPort);
            Assert.Equal(DHTCapabilities.Provide | DHTCapabilities.Traversal | DHTCapabilities.Forwarder, options.Capabilities);
            Assert.True(options.PubliclyReachable);
            Assert.False(NodeOptionsBuilder.Build(new ProberSettings(), NullLogger.Instance).PubliclyReachable);
        }

        /// <summary>Every invalid field is reported at once.</summary>
        [Fact]
        public void ReportsEveryInvalidField()
        {
            var settings = new ProberSettings
            {
                ListenAddresses = "0.0.0.0, 10.0.0.1",
                TcpEnabled = false,
                UdpEnabled = false,
                IdentityWorkBits = 21,
                BlockSize = 100,
                BootstrapEndpoints = { "bad" },
            };

            var errors = NodeOptionsBuilder.Validate(settings);
            var exception = Assert.Throws<SettingsValidationException>(() => NodeOptionsBuilder.Build(settings, NullLogger.Instance));

            Assert.Equal(errors.Count, exception.Errors.Count);
            Assert.Contains(errors, error => error.Contains("每种地址族", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("至少需要启用一个", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("身份工作量", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("块大小", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("引导端点", StringComparison.Ordinal));
        }

        /// <summary>Local node settings listen on loopback with assigned ports and keep the protocol parameters.</summary>
        [Fact]
        public void DerivesLocalNodeSettings()
        {
            var primary = new ProberSettings { IdentityWorkBits = 8, RequestTimeoutSeconds = 3, BlockSize = 4096, PubliclyReachable = true };
            var bootstrap = new[] { new IPEndPoint(IPAddress.Loopback, 42000) };

            var local = NodeSessionManager.DeriveLocalSettings(primary, bootstrap, new LocalNodeRequest { Capabilities = DHTCapabilities.Forwarder });

            Assert.Equal("127.0.0.1", local.ListenAddresses);
            Assert.False(local.PubliclyReachable);
            Assert.Equal(0, local.TcpPort);
            Assert.Equal(0, local.UdpPort);
            Assert.Equal(8, local.IdentityWorkBits);
            Assert.Equal(4096, local.BlockSize);
            Assert.Equal(DHTCapabilities.Forwarder, local.Capabilities);
            Assert.Equal(new[] { "127.0.0.1:42000" }, local.BootstrapEndpoints);
            Assert.False(local.PersistRoutingTable);
            Assert.Empty(NodeOptionsBuilder.Validate(local));
            Assert.Empty(primary.BootstrapEndpoints);
        }
    }
}
