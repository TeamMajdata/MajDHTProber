using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using MajDHTNetwork;
using MajDHTProber.Models;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Reports every invalid field of a settings object at once.</summary>
    public sealed class SettingsValidationException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="errors">The user-facing validation errors.</param>
        public SettingsValidationException(IReadOnlyList<string> errors)
            : base(string.Join(Environment.NewLine, errors))
        {
            Errors = errors;
        }

        /// <summary>Gets the user-facing validation errors.</summary>
        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>
    /// Converts <see cref="ProberSettings"/> into validated <see cref="DHTNodeOptions"/>, reporting problems as user-facing
    /// messages before the library rejects them with a generic error.
    /// </summary>
    public static class NodeOptionsBuilder
    {
        /// <summary>The maximum number of bootstrap endpoints accepted by the library.</summary>
        public const int MaxBootstrapEndpoints = 16;

        /// <summary>Validates settings without building options.</summary>
        /// <param name="settings">The settings.</param>
        /// <returns>The user-facing errors; empty when the settings are valid.</returns>
        public static IReadOnlyList<string> Validate(ProberSettings settings)
        {
            var errors = new List<string>();
            CreateOptions(settings, null, errors);
            return errors;
        }

        /// <summary>Builds node options from settings.</summary>
        /// <param name="settings">The settings.</param>
        /// <param name="logger">The logger the node writes its diagnostics to.</param>
        /// <returns>The options.</returns>
        /// <exception cref="SettingsValidationException">At least one field is invalid.</exception>
        public static DHTNodeOptions Build(ProberSettings settings, ILogger logger)
        {
            var errors = new List<string>();
            var options = CreateOptions(settings, logger, errors);
            if (errors.Count != 0)
            {
                throw new SettingsValidationException(errors);
            }
            return options;
        }

        /// <summary>Parses a comma, semicolon or whitespace separated list of IP addresses.</summary>
        /// <param name="text">The list text.</param>
        /// <param name="field">The user-facing field name used in errors.</param>
        /// <param name="allowWildcard">Whether 0.0.0.0 and :: are accepted.</param>
        /// <param name="errors">Receives the errors.</param>
        /// <returns>The parsed addresses in input order.</returns>
        public static List<IPAddress> ParseAddressList(string? text, string field, bool allowWildcard, List<string> errors)
        {
            var result = new List<IPAddress>();
            var parts = (text ?? string.Empty).Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!IPAddress.TryParse(part, out var address)
                    || (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6))
                {
                    errors.Add($"{field}中的“{part}”不是有效的 IP 地址。");
                    continue;
                }
                var wildcard = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
                if ((wildcard && !allowWildcard) || (!wildcard && !EndpointParser.IsUnicast(address)))
                {
                    errors.Add($"{field}中的“{part}”必须是单播地址。");
                    continue;
                }
                if (result.Contains(address))
                {
                    errors.Add($"{field}中的“{part}”重复。");
                    continue;
                }
                result.Add(address);
            }
            return result;
        }

        /// <summary>Validates every field and creates the options.</summary>
        /// <param name="settings">The settings.</param>
        /// <param name="logger">The node logger, or null while only validating.</param>
        /// <param name="errors">Receives the errors.</param>
        /// <returns>The options, meaningful only when no error was added.</returns>
        private static DHTNodeOptions CreateOptions(ProberSettings settings, ILogger? logger, List<string> errors)
        {
            var listen = ParseAddressList(settings.ListenAddresses, "监听地址", true, errors);
            if (listen.Count < 1 || listen.Count > 2)
            {
                errors.Add("监听地址需填写 1 个，或 IPv4 与 IPv6 地址各 1 个。");
            }
            else if (listen.Count == 2 && listen[0].AddressFamily == listen[1].AddressFamily)
            {
                errors.Add("每种地址族（IPv4 / IPv6）最多只能有 1 个监听地址。");
            }
            if (!settings.TcpEnabled && !settings.UdpEnabled)
            {
                errors.Add("TCP 与 UDP 至少需要启用一个。");
            }
            CheckRange(settings.TcpPort, 0, 65535, "TCP 端口", errors);
            CheckRange(settings.UdpPort, 0, 65535, "UDP 端口", errors);
            var bootstrap = new List<IPEndPoint>();
            foreach (var text in settings.BootstrapEndpoints)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                if (EndpointParser.TryParse(text, out var endpoint, out var error))
                {
                    bootstrap.Add(endpoint);
                }
                else
                {
                    errors.Add($"引导端点“{text.Trim()}”无效：{error}");
                }
            }
            if (bootstrap.Count > MaxBootstrapEndpoints)
            {
                errors.Add($"引导端点最多 {MaxBootstrapEndpoints} 个。");
            }
            CheckRange(settings.IdentityWorkBits, 0, 20, "身份工作量", errors);
            CheckRange(settings.UdpAttempts, 1, 5, "UDP 发送次数", errors);
            CheckRange(settings.BlockSize, 1024, 32768, "块大小", errors);
            CheckRange(settings.RequestTimeoutSeconds, 0.05, 120, "请求超时（秒）", errors);
            CheckRange(settings.MaintenanceIntervalMinutes, 1.0 / 60, 60, "维护间隔（分钟）", errors);
            CheckRange(settings.RecordLifetimeHours, 1.0 / 3600, 24 * 7, "记录租期（小时）", errors);
            return new DHTNodeOptions
            {
                ListenAddresses = listen,
                PubliclyReachable = settings.PubliclyReachable,
                BootstrapEndpoints = bootstrap,
                TcpPort = settings.TcpEnabled ? settings.TcpPort : null,
                UdpPort = settings.UdpEnabled ? settings.UdpPort : null,
                PreferredTransport = settings.PreferredTransport,
                Capabilities = settings.Capabilities,
                IdentityWorkBits = Math.Clamp(settings.IdentityWorkBits, 0, 20),
                EnableNatTraversal = settings.EnableNatTraversal,
                RequestTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 0.05, 120)),
                UdpAttempts = Math.Clamp(settings.UdpAttempts, 1, 5),
                BlockSize = Math.Clamp(settings.BlockSize, 1024, 32768),
                MaintenanceInterval = TimeSpan.FromMinutes(Math.Clamp(settings.MaintenanceIntervalMinutes, 1.0 / 60, 60)),
                RecordLifetime = TimeSpan.FromHours(Math.Clamp(settings.RecordLifetimeHours, 1.0 / 3600, 24 * 7)),
                Logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            };
        }

        /// <summary>Adds an error when a number lies outside an inclusive range.</summary>
        /// <param name="value">The value.</param>
        /// <param name="minimum">The inclusive minimum.</param>
        /// <param name="maximum">The inclusive maximum.</param>
        /// <param name="field">The user-facing field name.</param>
        /// <param name="errors">Receives the error.</param>
        private static void CheckRange(double value, double minimum, double maximum, string field, List<string> errors)
        {
            if (double.IsNaN(value) || value < minimum || value > maximum)
            {
                errors.Add($"{field}必须介于 {minimum:0.###} 与 {maximum:0.###} 之间。");
            }
        }
    }
}
