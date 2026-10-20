using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Logging;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>One step of an end-to-end transfer test.</summary>
    /// <param name="Name">The user-facing step name.</param>
    /// <param name="Succeeded">Whether the step succeeded.</param>
    /// <param name="Elapsed">The step duration.</param>
    /// <param name="Detail">A user-facing detail or failure description.</param>
    public sealed record RoundTripStage(string Name, bool Succeeded, TimeSpan Elapsed, string Detail);

    /// <summary>The outcome of an end-to-end transfer test.</summary>
    public sealed record RoundTripResult
    {
        /// <summary>Gets the canonical key of the test publication, or empty when publishing failed.</summary>
        public required string KeyText { get; init; }

        /// <summary>Gets the payload size in bytes.</summary>
        public required int PayloadBytes { get; init; }

        /// <summary>Gets the executed steps in order.</summary>
        public required IReadOnlyList<RoundTripStage> Stages { get; init; }

        /// <summary>Gets whether the downloaded content matched the published payload.</summary>
        public bool Succeeded { get; init; }

        /// <summary>Gets the download duration, when the download succeeded.</summary>
        public TimeSpan? DownloadTime { get; init; }

        /// <summary>Gets the download throughput in bytes per second, when the download succeeded.</summary>
        public double? Throughput { get; init; }
    }

    /// <summary>
    /// Tests communication between nodes end to end: one hosted node publishes a random payload, a second node resolves the
    /// key through the DHT, downloads the content from the providers and the payload is compared byte by byte.
    /// </summary>
    public sealed class RoundTripTester
    {
        /// <summary>The largest accepted payload.</summary>
        public const int MaxPayloadBytes = 64 * 1024 * 1024;

        /// <summary>The endpoint prober creating short-lived clients.</summary>
        private readonly EndpointProber _prober;
        /// <summary>The diagnostics logger.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a tester.</summary>
        /// <param name="prober">The endpoint prober creating short-lived clients.</param>
        /// <param name="sink">The log sink.</param>
        public RoundTripTester(EndpointProber prober, LogSink sink)
        {
            _prober = prober;
            _logger = sink.CreateLogger("通信测试");
        }

        /// <summary>Runs the test between two hosted nodes.</summary>
        /// <param name="publisher">The publishing session.</param>
        /// <param name="downloader">The downloading session.</param>
        /// <param name="payloadBytes">The payload size.</param>
        /// <param name="withdrawAfterwards">Whether the publisher withdraws the test key at the end.</param>
        /// <param name="progress">Receives every finished step, or null.</param>
        /// <param name="cancellationToken">Cancels the test.</param>
        /// <returns>The test outcome; step failures are reported in the result.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The payload size is not between 1 byte and <see cref="MaxPayloadBytes"/>.</exception>
        /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
        public Task<RoundTripResult> RunAsync(NodeSession publisher, NodeSession downloader, int payloadBytes, bool withdrawAfterwards,
            IProgress<RoundTripStage>? progress = null, CancellationToken cancellationToken = default)
        {
            return RunCoreAsync(publisher, _ => Task.FromResult<IRoundTripPeer>(new SessionPeer(downloader)), payloadBytes, withdrawAfterwards,
                progress, cancellationToken);
        }

        /// <summary>Runs the test between a hosted node and a short-lived client joining through an endpoint.</summary>
        /// <param name="publisher">The publishing session.</param>
        /// <param name="bootstrap">The endpoint the client joins through.</param>
        /// <param name="payloadBytes">The payload size.</param>
        /// <param name="withdrawAfterwards">Whether the publisher withdraws the test key at the end.</param>
        /// <param name="progress">Receives every finished step, or null.</param>
        /// <param name="cancellationToken">Cancels the test.</param>
        /// <returns>The test outcome; step failures are reported in the result.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The payload size is not between 1 byte and <see cref="MaxPayloadBytes"/>.</exception>
        /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
        public Task<RoundTripResult> RunAsync(NodeSession publisher, IPEndPoint bootstrap, int payloadBytes, bool withdrawAfterwards,
            IProgress<RoundTripStage>? progress = null, CancellationToken cancellationToken = default)
        {
            var settings = publisher.Settings;
            return RunCoreAsync(publisher, async token =>
            {
                var client = await _prober.StartClientAsync(bootstrap, settings.PreferredTransport, ProbeOptions.From(settings), token)
                    .ConfigureAwait(false);
                return new ClientPeer(client);
            }, payloadBytes, withdrawAfterwards, progress, cancellationToken);
        }

        /// <summary>Runs the shared test steps.</summary>
        /// <param name="publisher">The publishing session.</param>
        /// <param name="createDownloader">Creates the downloading peer.</param>
        /// <param name="payloadBytes">The payload size.</param>
        /// <param name="withdrawAfterwards">Whether the publisher withdraws the test key at the end.</param>
        /// <param name="progress">Receives every finished step.</param>
        /// <param name="cancellationToken">Cancels the test.</param>
        /// <returns>The test outcome.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The payload size is invalid.</exception>
        /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
        private async Task<RoundTripResult> RunCoreAsync(NodeSession publisher, Func<CancellationToken, Task<IRoundTripPeer>> createDownloader,
            int payloadBytes, bool withdrawAfterwards, IProgress<RoundTripStage>? progress, CancellationToken cancellationToken)
        {
            if (payloadBytes < 1 || payloadBytes > MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            }
            var stages = new List<RoundTripStage>();
            var payload = new byte[payloadBytes];
            RandomNumberGenerator.Fill(payload);
            var name = "prober/roundtrip/" + Guid.NewGuid().ToString("N");
            _logger.LogInformation("开始通信测试：负载 {Size}。", Display.Bytes(payloadBytes));

            var publishing = Stopwatch.StartNew();
            DHTRecord record;
            try
            {
                using var source = new MemoryStream(payload, false);
                record = await publisher.PublishAsync(name, source, 1, cancellationToken).ConfigureAwait(false);
                Add(stages, progress, new RoundTripStage("发布", true, publishing.Elapsed, record.BlockCount + " 块，" + Display.Bytes(record.Length)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Add(stages, progress, new RoundTripStage("发布", false, publishing.Elapsed, Display.Error(exception)));
                return Finish(string.Empty, payloadBytes, stages, false, null);
            }

            var succeeded = false;
            TimeSpan? downloadTime = null;
            IRoundTripPeer? downloader = null;
            try
            {
                var connecting = Stopwatch.StartNew();
                try
                {
                    downloader = await createDownloader(cancellationToken).ConfigureAwait(false);
                    Add(stages, progress, new RoundTripStage("准备下载方", true, connecting.Elapsed, downloader.Description));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Add(stages, progress, new RoundTripStage("准备下载方", false, connecting.Elapsed, Display.Error(exception)));
                    return Finish(record.Key.ToString(), payloadBytes, stages, false, null);
                }

                var resolving = Stopwatch.StartNew();
                DHTRecord? resolved;
                try
                {
                    resolved = await downloader.ResolveAsync(record.Key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Add(stages, progress, new RoundTripStage("解析键", false, resolving.Elapsed, Display.Error(exception)));
                    return Finish(record.Key.ToString(), payloadBytes, stages, false, null);
                }
                var resolvedOk = resolved != null && resolved.FileHash == record.FileHash;
                Add(stages, progress, new RoundTripStage("解析键", resolvedOk, resolving.Elapsed,
                    resolved == null ? "下载方未能通过 DHT 找到该记录（可能尚无副本可达）" : resolvedOk ? "修订 " + resolved.Revision : "解析到的内容哈希不一致"));
                if (!resolvedOk)
                {
                    return Finish(record.Key.ToString(), payloadBytes, stages, false, null);
                }

                var downloading = Stopwatch.StartNew();
                using var destination = new MemoryStream(payloadBytes);
                try
                {
                    await downloader.DownloadAsync(record, destination, cancellationToken).ConfigureAwait(false);
                    downloadTime = downloading.Elapsed;
                    Add(stages, progress, new RoundTripStage("下载", true, downloading.Elapsed, Display.Bytes(destination.Length) + "，"
                        + Display.Bytes((long)(payloadBytes / Math.Max(downloading.Elapsed.TotalSeconds, 1e-6))) + "/s"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Add(stages, progress, new RoundTripStage("下载", false, downloading.Elapsed, Display.Error(exception)));
                    return Finish(record.Key.ToString(), payloadBytes, stages, false, null);
                }

                var verifying = Stopwatch.StartNew();
                succeeded = destination.TryGetBuffer(out var received) && received.AsSpan().SequenceEqual(payload);
                Add(stages, progress, new RoundTripStage("逐字节比对", succeeded, verifying.Elapsed, succeeded ? "内容一致" : "内容不一致"));
            }
            finally
            {
                if (downloader != null)
                {
                    await downloader.DisposeAsync().ConfigureAwait(false);
                }
                if (withdrawAfterwards)
                {
                    await WithdrawAsync(publisher, name, stages, progress).ConfigureAwait(false);
                }
            }
            return Finish(record.Key.ToString(), payloadBytes, stages, succeeded, downloadTime);
        }

        /// <summary>Withdraws the test key, reporting failures as a step.</summary>
        /// <param name="publisher">The publishing session.</param>
        /// <param name="name">The test name.</param>
        /// <param name="stages">The step list.</param>
        /// <param name="progress">Receives the step.</param>
        /// <returns>Completion after the attempt.</returns>
        private static async Task WithdrawAsync(NodeSession publisher, string name, List<RoundTripStage> stages, IProgress<RoundTripStage>? progress)
        {
            var withdrawing = Stopwatch.StartNew();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await publisher.UnpublishAsync(name, 0, timeout.Token).ConfigureAwait(false);
                Add(stages, progress, new RoundTripStage("撤回测试记录", true, withdrawing.Elapsed, "已签名并复制撤回记录"));
            }
            catch (Exception exception)
            {
                Add(stages, progress, new RoundTripStage("撤回测试记录", false, withdrawing.Elapsed, Display.Error(exception)));
            }
        }

        /// <summary>Records a finished step.</summary>
        /// <param name="stages">The step list.</param>
        /// <param name="progress">Receives the step.</param>
        /// <param name="stage">The step.</param>
        private static void Add(List<RoundTripStage> stages, IProgress<RoundTripStage>? progress, RoundTripStage stage)
        {
            stages.Add(stage);
            progress?.Report(stage);
        }

        /// <summary>Creates the result and logs the summary.</summary>
        /// <param name="keyText">The test key.</param>
        /// <param name="payloadBytes">The payload size.</param>
        /// <param name="stages">The executed steps.</param>
        /// <param name="succeeded">Whether the content matched.</param>
        /// <param name="downloadTime">The download duration.</param>
        /// <returns>The result.</returns>
        private RoundTripResult Finish(string keyText, int payloadBytes, List<RoundTripStage> stages, bool succeeded, TimeSpan? downloadTime)
        {
            if (succeeded)
            {
                _logger.LogInformation("通信测试成功：{Size}，下载耗时 {Elapsed}。", Display.Bytes(payloadBytes), Display.Duration(downloadTime));
            }
            else
            {
                _logger.LogWarning("通信测试失败，共执行 {Count} 个步骤。", stages.Count);
            }
            return new RoundTripResult
            {
                KeyText = keyText,
                PayloadBytes = payloadBytes,
                Stages = stages,
                Succeeded = succeeded,
                DownloadTime = downloadTime,
                Throughput = downloadTime.HasValue ? payloadBytes / Math.Max(downloadTime.Value.TotalSeconds, 1e-6) : null,
            };
        }

        /// <summary>The downloading side of a test.</summary>
        private interface IRoundTripPeer : IAsyncDisposable
        {
            /// <summary>Gets a user-facing description of the downloader.</summary>
            string Description { get; }

            /// <summary>Resolves a key through the DHT.</summary>
            /// <param name="key">The key.</param>
            /// <param name="cancellationToken">Cancels the lookup.</param>
            /// <returns>The record, or null.</returns>
            Task<DHTRecord?> ResolveAsync(DHTKey key, CancellationToken cancellationToken);

            /// <summary>Downloads a record's content.</summary>
            /// <param name="record">The record.</param>
            /// <param name="destination">The destination.</param>
            /// <param name="cancellationToken">Cancels the download.</param>
            /// <returns>Completion after verification.</returns>
            Task DownloadAsync(DHTRecord record, Stream destination, CancellationToken cancellationToken);
        }

        /// <summary>A hosted session acting as downloader.</summary>
        private sealed class SessionPeer : IRoundTripPeer
        {
            /// <summary>The downloading session.</summary>
            private readonly NodeSession _session;

            /// <summary>Wraps a session.</summary>
            /// <param name="session">The downloading session.</param>
            internal SessionPeer(NodeSession session)
            {
                _session = session;
            }

            /// <summary>Gets the session label.</summary>
            public string Description => _session.Label;

            /// <summary>Resolves a key through the session.</summary>
            /// <param name="key">The key.</param>
            /// <param name="cancellationToken">Cancels the lookup.</param>
            /// <returns>The record, or null.</returns>
            public Task<DHTRecord?> ResolveAsync(DHTKey key, CancellationToken cancellationToken)
            {
                return _session.ResolveAsync(key, cancellationToken);
            }

            /// <summary>Downloads through the session.</summary>
            /// <param name="record">The record.</param>
            /// <param name="destination">The destination.</param>
            /// <param name="cancellationToken">Cancels the download.</param>
            /// <returns>Completion after verification.</returns>
            public Task DownloadAsync(DHTRecord record, Stream destination, CancellationToken cancellationToken)
            {
                return _session.DownloadAsync(record, destination, record.FileHash, null, cancellationToken);
            }

            /// <summary>Leaves the session running.</summary>
            /// <returns>A completed task.</returns>
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>A short-lived client acting as downloader.</summary>
        private sealed class ClientPeer : IRoundTripPeer
        {
            /// <summary>The started client.</summary>
            private readonly EphemeralNode _client;

            /// <summary>Wraps a client.</summary>
            /// <param name="client">The started client.</param>
            internal ClientPeer(EphemeralNode client)
            {
                _client = client;
            }

            /// <summary>Gets a description naming the client's routing table size.</summary>
            public string Description => "临时客户端，已知 " + _client.Node.KnownPeers.Count + " 个节点";

            /// <summary>Resolves a key through the client.</summary>
            /// <param name="key">The key.</param>
            /// <param name="cancellationToken">Cancels the lookup.</param>
            /// <returns>The record, or null.</returns>
            public Task<DHTRecord?> ResolveAsync(DHTKey key, CancellationToken cancellationToken)
            {
                return _client.Node.ResolveAsync(key, cancellationToken);
            }

            /// <summary>Downloads through the client.</summary>
            /// <param name="record">The record.</param>
            /// <param name="destination">The destination.</param>
            /// <param name="cancellationToken">Cancels the download.</param>
            /// <returns>Completion after verification.</returns>
            public Task DownloadAsync(DHTRecord record, Stream destination, CancellationToken cancellationToken)
            {
                return _client.Node.DownloadAsync(record, destination, record.FileHash, cancellationToken);
            }

            /// <summary>Stops the client.</summary>
            /// <returns>Completion after shutdown.</returns>
            public ValueTask DisposeAsync()
            {
                return _client.DisposeAsync();
            }
        }
    }
}
