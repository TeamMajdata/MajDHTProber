using System;
using System.Net;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>The latest probe result remembered for a peer.</summary>
    public sealed record PeerProbeRecord
    {
        /// <summary>Gets when the probe finished.</summary>
        public required DateTimeOffset At { get; init; }

        /// <summary>Gets whether the peer answered.</summary>
        public required bool Succeeded { get; init; }

        /// <summary>Gets the probed endpoint.</summary>
        public IPEndPoint? Endpoint { get; init; }

        /// <summary>Gets the probed transport.</summary>
        public DHTTransport Transport { get; init; }

        /// <summary>Gets the measured Hello round trip.</summary>
        public TimeSpan? RoundTrip { get; init; }

        /// <summary>Gets a short user-facing summary.</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>Creates a record from an endpoint probe result.</summary>
        /// <param name="result">The probe result.</param>
        /// <param name="finishedAt">The time the probe finished.</param>
        /// <returns>The remembered probe record.</returns>
        public static PeerProbeRecord From(EndpointProbeResult result, DateTimeOffset finishedAt)
        {
            string summary;
            if (!result.Succeeded)
            {
                summary = result.Error ?? "无响应";
            }
            else if (result.PingSucceeded)
            {
                summary = "Hello 与加密 Ping 均成功";
            }
            else
            {
                summary = result.Error ?? "Hello 成功，加密 Ping 失败";
            }
            return new PeerProbeRecord
            {
                At = finishedAt,
                Succeeded = result.Succeeded,
                Endpoint = result.Endpoint,
                Transport = result.Transport,
                RoundTrip = result.HelloRoundTrip,
                Summary = summary,
            };
        }
    }
}
