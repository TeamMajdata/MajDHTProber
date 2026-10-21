using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using MajDHTNetwork;
using MajDHTProber.Models;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Formats protocol values as concise, user-facing Chinese text.</summary>
    public static class Display
    {
        /// <summary>The placeholder shown for absent values.</summary>
        public const string None = "—";

        /// <summary>Shortens a long hexadecimal identifier to its head and tail.</summary>
        /// <param name="id">The identifier.</param>
        /// <param name="head">The number of leading characters kept.</param>
        /// <param name="tail">The number of trailing characters kept.</param>
        /// <returns>The shortened identifier, or the placeholder when absent.</returns>
        public static string ShortId(string? id, int head = 10, int tail = 6)
        {
            if (string.IsNullOrEmpty(id))
            {
                return None;
            }
            return id.Length <= head + tail + 1 ? id : id.Substring(0, head) + "…" + id.Substring(id.Length - tail);
        }

        /// <summary>Formats a byte count with binary units.</summary>
        /// <param name="bytes">The byte count.</param>
        /// <returns>Text such as <c>1.5 MiB</c>.</returns>
        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double value = bytes;
            var unit = 0;
            while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0
                ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
                : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        /// <summary>Formats a duration with an appropriate unit.</summary>
        /// <param name="duration">The duration, or null.</param>
        /// <returns>Text such as <c>850 µs</c>, <c>12.3 ms</c> or <c>1.24 s</c>, or the placeholder.</returns>
        public static string Duration(TimeSpan? duration)
        {
            if (!duration.HasValue)
            {
                return None;
            }
            var value = duration.Value;
            if (value.TotalMilliseconds < 1)
            {
                return Math.Max(0, value.TotalMilliseconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " µs";
            }
            if (value.TotalSeconds < 1)
            {
                return value.TotalMilliseconds.ToString(value.TotalMilliseconds >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " ms";
            }
            if (value.TotalMinutes < 1)
            {
                return value.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
            }
            return value.TotalHours >= 1
                ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>Formats a past time relative to now.</summary>
        /// <param name="time">The time, or null.</param>
        /// <param name="now">The current time.</param>
        /// <returns>Text such as <c>12 秒前</c>, or the placeholder.</returns>
        public static string Relative(DateTimeOffset? time, DateTimeOffset now)
        {
            if (!time.HasValue)
            {
                return None;
            }
            var age = now - time.Value;
            if (age < TimeSpan.Zero)
            {
                return Future(time.Value, now);
            }
            if (age.TotalSeconds < 5)
            {
                return "刚刚";
            }
            if (age.TotalMinutes < 1)
            {
                return ((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " 秒前";
            }
            if (age.TotalHours < 1)
            {
                return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
            }
            if (age.TotalDays < 1)
            {
                return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时前";
            }
            return time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>Formats a future time relative to now.</summary>
        /// <param name="time">The future time.</param>
        /// <param name="now">The current time.</param>
        /// <returns>Text such as <c>23 小时后</c>, or <c>已过期</c> when the time has passed.</returns>
        public static string Future(DateTimeOffset time, DateTimeOffset now)
        {
            var remaining = time - now;
            if (remaining <= TimeSpan.Zero)
            {
                return "已过期";
            }
            if (remaining.TotalMinutes < 1)
            {
                return ((int)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " 秒后";
            }
            if (remaining.TotalHours < 1)
            {
                return ((int)remaining.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟后";
            }
            if (remaining.TotalDays < 2)
            {
                return ((int)remaining.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时后";
            }
            return ((int)remaining.TotalDays).ToString(CultureInfo.InvariantCulture) + " 天后";
        }

        /// <summary>Formats a local timestamp.</summary>
        /// <param name="time">The time, or null.</param>
        /// <returns>Text such as <c>2026-10-20 14:03:12</c>, or the placeholder.</returns>
        public static string Timestamp(DateTimeOffset? time)
        {
            return time.HasValue ? time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : None;
        }

        /// <summary>Formats node capabilities.</summary>
        /// <param name="capabilities">The capabilities.</param>
        /// <returns>Text such as <c>下载 · 提供 · Traversal · Forwarder</c>.</returns>
        public static string Capabilities(DHTCapabilities capabilities)
        {
            var parts = new List<string>(4);
            if ((capabilities & DHTCapabilities.Download) != 0)
            {
                parts.Add("下载");
            }
            if ((capabilities & DHTCapabilities.Provide) != 0)
            {
                parts.Add("提供");
            }
            if ((capabilities & DHTCapabilities.Traversal) != 0)
            {
                parts.Add("Traversal");
            }
            if ((capabilities & DHTCapabilities.Forwarder) != 0)
            {
                parts.Add("Forwarder");
            }
            return parts.Count == 0 ? "无" : string.Join(" · ", parts);
        }

        /// <summary>Formats a reachability state.</summary>
        /// <param name="reachability">The reachability.</param>
        /// <returns>The user-facing text.</returns>
        public static string Reachability(DHTReachability reachability)
        {
            return reachability switch
            {
                DHTReachability.Public => "公网可达",
                DHTReachability.Private => "私有（经 Forwarder）",
                _ => "未知",
            };
        }

        /// <summary>Formats a transport.</summary>
        /// <param name="transport">The transport.</param>
        /// <returns><c>TCP</c> or <c>UDP</c>.</returns>
        public static string Transport(DHTTransport transport)
        {
            return transport == DHTTransport.Udp ? "UDP" : "TCP";
        }

        /// <summary>Formats an observed endpoint with its ports.</summary>
        /// <param name="endpoint">The observed endpoint.</param>
        /// <returns>Text such as <c>203.0.113.5 · TCP 42000 · UDP 42000</c>.</returns>
        public static string Endpoint(DHTEndpoint endpoint)
        {
            var text = endpoint.Address.ToString();
            if (endpoint.TcpPort != 0)
            {
                text += " · TCP " + endpoint.TcpPort.ToString(CultureInfo.InvariantCulture);
            }
            if (endpoint.UdpPort != 0)
            {
                text += " · UDP " + endpoint.UdpPort.ToString(CultureInfo.InvariantCulture);
            }
            return text;
        }

        /// <summary>Summarizes where a node can be reached, for list rows.</summary>
        /// <param name="peer">The node at its observed endpoints.</param>
        /// <returns>The most recently observed endpoint, a hint note for nodes without endpoints, or the placeholder.</returns>
        public static string PrimaryAddress(DHTPeer peer)
        {
            if (peer.Endpoints.Count != 0)
            {
                var latest = Endpoint(peer.Endpoints[peer.Endpoints.Count - 1]);
                return peer.Endpoints.Count == 1 ? latest : latest + "（+" + (peer.Endpoints.Count - 1).ToString(CultureInfo.InvariantCulture) + "）";
            }
            return peer.Hints.Count != 0
                ? "经 " + peer.Hints.Count.ToString(CultureInfo.InvariantCulture) + " 个提示节点"
                : None;
        }

        /// <summary>Summarizes a hinted Traversal node or Forwarder for detail lists.</summary>
        /// <param name="hint">The hinted node at the endpoints its referrer observed.</param>
        /// <returns>Text such as <c>ab12…cd34 · Traversal · 203.0.113.5 · TCP 42000</c>.</returns>
        public static string Hint(DHTPeer hint)
        {
            return ShortId(hint.NodeId, 8, 4) + " · " + Capabilities(hint.Capabilities & (DHTCapabilities.Traversal | DHTCapabilities.Forwarder))
                + " · " + PrimaryAddress(hint);
        }

        /// <summary>Formats a connection state.</summary>
        /// <param name="state">The state.</param>
        /// <returns>The user-facing text.</returns>
        public static string State(PeerConnectionState state)
        {
            return state switch
            {
                PeerConnectionState.Connected => "已连接",
                PeerConnectionState.Reachable => "可达",
                PeerConnectionState.Disconnected => "已断开",
                PeerConnectionState.Expired => "已过期",
                _ => "已发现",
            };
        }

        /// <summary>Explains a connection state in one sentence.</summary>
        /// <param name="state">The state.</param>
        /// <returns>The user-facing explanation.</returns>
        public static string StateDescription(PeerConnectionState state)
        {
            return state switch
            {
                PeerConnectionState.Connected => "该节点在本节点的路由表中，可作为查找跳点。",
                PeerConnectionState.Reachable => "最近 5 分钟内认证交互或探测成功，但不在路由表中（如私有节点或路由表未接纳）。",
                PeerConnectionState.Disconnected => "上次成功之后发生过请求失败或探测失败。",
                PeerConnectionState.Expired => "该节点签名联系人的租期已过期。",
                _ => "仅通过查找结果或引用得知，尚未与其直接交互。",
            };
        }

        /// <summary>Formats the sources a peer was learned from.</summary>
        /// <param name="sources">The sources.</param>
        /// <returns>Text such as <c>路由 · 手动</c>.</returns>
        public static string Sources(PeerSource sources)
        {
            var parts = new List<string>();
            if ((sources & PeerSource.LocalNode) != 0)
            {
                parts.Add("本地节点");
            }
            if ((sources & PeerSource.Manual) != 0)
            {
                parts.Add("手动");
            }
            if ((sources & PeerSource.Routing) != 0)
            {
                parts.Add("路由");
            }
            if ((sources & PeerSource.Lookup) != 0)
            {
                parts.Add("查找");
            }
            if ((sources & PeerSource.Provider) != 0)
            {
                parts.Add("提供者");
            }
            if ((sources & PeerSource.Probe) != 0)
            {
                parts.Add("探测");
            }
            if ((sources & PeerSource.Hint) != 0)
            {
                parts.Add("提示");
            }
            return parts.Count == 0 ? None : string.Join(" · ", parts);
        }

        /// <summary>Formats a ratio as a percentage.</summary>
        /// <param name="ratio">The ratio between zero and one.</param>
        /// <returns>Text such as <c>8.3%</c>.</returns>
        public static string Percent(double ratio)
        {
            return (ratio * 100).ToString(ratio == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>Formats a session lifecycle state.</summary>
        /// <param name="state">The state.</param>
        /// <returns>The user-facing text.</returns>
        public static string SessionState(NodeSessionState state)
        {
            return state switch
            {
                NodeSessionState.Running => "运行中",
                NodeSessionState.Starting => "启动中",
                NodeSessionState.Stopping => "停止中",
                NodeSessionState.Faulted => "启动失败",
                _ => "已停止",
            };
        }

        /// <summary>Formats an IP endpoint.</summary>
        /// <param name="endpoint">The endpoint, or null.</param>
        /// <returns>The <c>address:port</c> text, or the placeholder.</returns>
        public static string IPEndPoint(IPEndPoint? endpoint)
        {
            return endpoint == null ? None : EndpointParser.Format(endpoint);
        }

        /// <summary>Formats the origin of a record.</summary>
        /// <param name="origin">The origin.</param>
        /// <returns>The user-facing text.</returns>
        public static string Origin(RecordOrigin origin)
        {
            return origin switch
            {
                RecordOrigin.Published => "已发布",
                RecordOrigin.Alias => "别名",
                RecordOrigin.Imported => "已导入",
                RecordOrigin.Downloaded => "已下载",
                RecordOrigin.Resolved => "已解析",
                RecordOrigin.Withdrawal => "已撤回",
                _ => None,
            };
        }

        /// <summary>Describes an exception for the user without a stack trace.</summary>
        /// <param name="exception">The exception.</param>
        /// <returns>The exception's message, prefixed with a category for well-known failure types.</returns>
        public static string Error(Exception exception)
        {
            var category = exception switch
            {
                SettingsValidationException => "设置无效",
                OperationCanceledException => "已取消",
                TimeoutException => "超时",
                System.IO.FileNotFoundException => "未找到",
                System.IO.InvalidDataException => "数据校验失败",
                System.IO.IOException => "网络或 I/O 错误",
                System.Net.Sockets.SocketException => "套接字错误",
                InvalidOperationException => "操作无效",
                ArgumentException => "参数无效",
                FormatException => "格式错误",
                _ => exception.GetType().Name,
            };
            return exception is OperationCanceledException ? category : category + "：" + exception.Message;
        }
    }
}
