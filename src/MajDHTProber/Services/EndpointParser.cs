using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Parses, resolves and formats node endpoints entered by the user.</summary>
    public static class EndpointParser
    {
        /// <summary>Parses a literal <c>IPv4:port</c> or <c>[IPv6]:port</c> endpoint.</summary>
        /// <param name="text">The endpoint text.</param>
        /// <param name="endpoint">The parsed unicast endpoint when successful.</param>
        /// <param name="error">A user-facing reason when parsing fails; empty on success.</param>
        /// <returns>Whether the text is a literal unicast endpoint with a non-zero port.</returns>
        public static bool TryParse(string? text, [NotNullWhen(true)] out IPEndPoint? endpoint, out string error)
        {
            endpoint = null;
            var trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                error = "请输入端点。";
                return false;
            }
            if (!IPEndPoint.TryParse(trimmed, out var parsed))
            {
                error = "无法解析端点，格式应为 IP:端口 或 [IPv6]:端口。";
                return false;
            }
            if (parsed.Port == 0)
            {
                error = parsed.AddressFamily == AddressFamily.InterNetworkV6 && !trimmed.StartsWith('[')
                    ? "IPv6 端点请使用 [地址]:端口 格式。"
                    : "端点缺少端口，或端口为 0。";
                return false;
            }
            if (!IsUnicast(parsed.Address))
            {
                error = "端点必须是单播地址（不能是 0.0.0.0、广播或组播地址）。";
                return false;
            }
            endpoint = parsed;
            error = string.Empty;
            return true;
        }

        /// <summary>Parses a literal endpoint, or resolves <c>host:port</c> through DNS, preferring IPv4 addresses.</summary>
        /// <param name="text">The endpoint text.</param>
        /// <param name="cancellationToken">Cancels name resolution.</param>
        /// <returns>The resolved unicast endpoint.</returns>
        /// <exception cref="FormatException">The text is not an endpoint or the host has no unicast address.</exception>
        /// <exception cref="SocketException">Name resolution failed.</exception>
        /// <exception cref="OperationCanceledException">Resolution was cancelled.</exception>
        public static async Task<IPEndPoint> ResolveAsync(string text, CancellationToken cancellationToken = default)
        {
            if (TryParse(text, out var literal, out var literalError))
            {
                return literal;
            }
            if (!TrySplitHostPort(text.Trim(), out var host, out var port))
            {
                throw new FormatException(literalError);
            }
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            IPAddress? fallback = null;
            foreach (var address in addresses)
            {
                if (!IsUnicast(address))
                {
                    continue;
                }
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return new IPEndPoint(address, port);
                }
                fallback ??= address;
            }
            if (fallback == null)
            {
                throw new FormatException("主机名没有可用的单播地址。");
            }
            return new IPEndPoint(fallback, port);
        }

        /// <summary>Formats an endpoint, bracketing IPv6 addresses.</summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <returns>The <c>address:port</c> text.</returns>
        public static string Format(IPEndPoint endpoint)
        {
            return endpoint.AddressFamily == AddressFamily.InterNetworkV6
                ? "[" + endpoint.Address + "]:" + endpoint.Port.ToString(CultureInfo.InvariantCulture)
                : endpoint.Address + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Checks whether an address can identify a single remote host.</summary>
        /// <param name="address">The address.</param>
        /// <returns>Whether the address is neither unspecified, broadcast nor multicast.</returns>
        public static bool IsUnicast(IPAddress address)
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
                || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
            {
                return false;
            }
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var first = address.GetAddressBytes()[0];
                return first < 224 || first > 239;
            }
            return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6Multicast;
        }

        /// <summary>Splits <c>host:port</c> text whose host is a DNS name.</summary>
        /// <param name="text">The trimmed text.</param>
        /// <param name="host">The host name.</param>
        /// <param name="port">The port.</param>
        /// <returns>Whether the text has exactly one separator and a valid non-zero port.</returns>
        private static bool TrySplitHostPort(string text, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            var separator = text.LastIndexOf(':');
            if (separator <= 0 || separator != text.IndexOf(':') || separator == text.Length - 1)
            {
                return false;
            }
            host = text.Substring(0, separator);
            return int.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port > 0 && port <= 65535 && Uri.CheckHostName(host) == UriHostNameType.Dns;
        }
    }
}
