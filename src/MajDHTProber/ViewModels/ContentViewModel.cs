using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>
    /// The content page: publishes text or files, lists the records the active node handled, pulls content by key or record
    /// code, resolves keys, finds providers and imports local copies of other publishers' content.
    /// </summary>
    public sealed partial class ContentViewModel : PageViewModel
    {
        /// <summary>The largest download kept in memory for preview.</summary>
        public const long MaxPreviewDownload = 64L * 1024 * 1024;
        /// <summary>The number of leading bytes shown in the hexadecimal preview.</summary>
        private const int HexPreviewBytes = 4096;
        /// <summary>The number of leading bytes decoded for the text preview.</summary>
        private const int TextPreviewBytes = 64 * 1024;

        /// <summary>The session whose record events are subscribed.</summary>
        private NodeSession? _subscribed;

        /// <summary>Creates the page.</summary>
        /// <param name="context">The shared services.</param>
        public ContentViewModel(ProberContext context)
            : base(context)
        {
            Subscribe();
        }

        /// <summary>Gets the remembered records of the active session.</summary>
        public ObservableCollection<RecordItemViewModel> Records { get; } = new ObservableCollection<RecordItemViewModel>();

        /// <summary>Gets the providers found by the latest provider lookup.</summary>
        public ObservableCollection<string> Providers { get; } = new ObservableCollection<string>();

        /// <summary>Gets the publication source options.</summary>
        public IReadOnlyList<string> PublishModes { get; } = new[] { "文本", "文件" };

        /// <summary>Gets the download destination options.</summary>
        public IReadOnlyList<string> DownloadModes { get; } = new[] { "内存预览", "保存到文件" };

        /// <summary>Gets the result of the latest publication.</summary>
        public BannerViewModel PublishResult { get; } = new BannerViewModel();

        /// <summary>Gets the result of the latest record action.</summary>
        public BannerViewModel RecordResult { get; } = new BannerViewModel();

        /// <summary>Gets the result of the latest download.</summary>
        public BannerViewModel DownloadResult { get; } = new BannerViewModel();

        /// <summary>Gets the result of the latest lookup.</summary>
        public BannerViewModel LookupResult { get; } = new BannerViewModel();

        /// <summary>Gets the result of the latest import.</summary>
        public BannerViewModel ImportResult { get; } = new BannerViewModel();

        /// <summary>Gets or sets the name of a new publication.</summary>
        [ObservableProperty]
        public partial string PublishName { get; set; } = "prober/hello.txt";

        /// <summary>Gets or sets the revision of a new publication.</summary>
        [ObservableProperty]
        public partial decimal? PublishRevision { get; set; } = 1;

        /// <summary>Gets or sets the publication source: 0 for text, 1 for a file.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTextMode), nameof(IsFileMode))]
        public partial int PublishModeIndex { get; set; }

        /// <summary>Gets or sets the text to publish.</summary>
        [ObservableProperty]
        public partial string PublishText { get; set; } = "Hello from MajDHT Prober!";

        /// <summary>Gets or sets the file to publish.</summary>
        [ObservableProperty]
        public partial string PublishFilePath { get; set; } = string.Empty;

        /// <summary>Gets or sets the selected record.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedRecord), nameof(SelectedRecordIsOwn))]
        public partial RecordItemViewModel? SelectedRecord { get; set; }

        /// <summary>Gets or sets the name of a new alias.</summary>
        [ObservableProperty]
        public partial string AliasName { get; set; } = string.Empty;

        /// <summary>Gets or sets the key or record code to pull.</summary>
        [ObservableProperty]
        public partial string DownloadInput { get; set; } = string.Empty;

        /// <summary>Gets or sets the optional trusted complete-file hash.</summary>
        [ObservableProperty]
        public partial string ExpectedHash { get; set; } = string.Empty;

        /// <summary>Gets or sets the download destination: 0 for memory preview, 1 for a file.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSaveMode))]
        public partial int DownloadModeIndex { get; set; }

        /// <summary>Gets or sets the destination file.</summary>
        [ObservableProperty]
        public partial string DownloadFilePath { get; set; } = string.Empty;

        /// <summary>Gets or sets the download progress percentage.</summary>
        [ObservableProperty]
        public partial double DownloadProgress { get; set; }

        /// <summary>Gets or sets the download progress text.</summary>
        [ObservableProperty]
        public partial string DownloadProgressText { get; set; } = string.Empty;

        /// <summary>Gets or sets whether a download is running.</summary>
        [ObservableProperty]
        public partial bool IsDownloading { get; set; }

        /// <summary>Gets or sets the text preview of the downloaded content.</summary>
        [ObservableProperty]
        public partial string PreviewText { get; set; } = string.Empty;

        /// <summary>Gets or sets the hexadecimal preview of the downloaded content.</summary>
        [ObservableProperty]
        public partial string HexPreview { get; set; } = string.Empty;

        /// <summary>Gets or sets whether a preview is available.</summary>
        [ObservableProperty]
        public partial bool HasPreview { get; set; }

        /// <summary>Gets or sets whether the preview decoded as text.</summary>
        [ObservableProperty]
        public partial bool HasTextPreview { get; set; }

        /// <summary>Gets or sets the key or record code to resolve.</summary>
        [ObservableProperty]
        public partial string ResolveInput { get; set; } = string.Empty;

        /// <summary>Gets or sets the details of the resolved record.</summary>
        [ObservableProperty]
        public partial string ResolvedText { get; set; } = string.Empty;

        /// <summary>Gets or sets the complete-file hash whose providers are looked up.</summary>
        [ObservableProperty]
        public partial string ProvidersHash { get; set; } = string.Empty;

        /// <summary>Gets or sets the record code of the content to import.</summary>
        [ObservableProperty]
        public partial string ImportRecordCode { get; set; } = string.Empty;

        /// <summary>Gets or sets the local copy to import.</summary>
        [ObservableProperty]
        public partial string ImportFilePath { get; set; } = string.Empty;

        /// <summary>Gets whether text is published.</summary>
        public bool IsTextMode => PublishModeIndex == 0;

        /// <summary>Gets whether a file is published.</summary>
        public bool IsFileMode => PublishModeIndex == 1;

        /// <summary>Gets whether downloads are saved to a file.</summary>
        public bool IsSaveMode => DownloadModeIndex == 1;

        /// <summary>Gets whether a record is selected.</summary>
        public bool HasSelectedRecord => SelectedRecord != null;

        /// <summary>Gets whether the selected record belongs to the active node's publisher namespace and can be withdrawn.</summary>
        public bool SelectedRecordIsOwn => SelectedRecord?.IsOwn == true;

        /// <summary>Reloads the records after the active session changed.</summary>
        public override void OnActiveSessionChanged()
        {
            Subscribe();
        }

        /// <summary>Refreshes relative times.</summary>
        /// <param name="now">The current time.</param>
        public override void OnTick(DateTimeOffset now)
        {
            if (now.Second % 10 == 0)
            {
                ReloadRecords();
            }
        }

        /// <summary>Chooses the file to publish.</summary>
        /// <returns>Completion after the picker closed.</returns>
        [RelayCommand]
        private async Task BrowsePublishFileAsync()
        {
            var path = await Context.Interaction.PickOpenFileAsync("选择要发布的文件");
            if (path != null)
            {
                PublishFilePath = path;
                if (string.IsNullOrWhiteSpace(PublishName) || PublishName == "prober/hello.txt")
                {
                    PublishName = "prober/" + Path.GetFileName(path);
                }
            }
        }

        /// <summary>Publishes the text or file.</summary>
        /// <param name="cancellationToken">Cancels publication.</param>
        /// <returns>Completion after publication.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task PublishAsync(CancellationToken cancellationToken)
        {
            PublishResult.Hide();
            var name = PublishName.Trim();
            var revision = (long)(PublishRevision ?? 1);
            DHTRecord? record = null;
            if (!await RunAsync("发布", async () =>
            {
                if (IsFileMode)
                {
                    if (!File.Exists(PublishFilePath))
                    {
                        throw new FileNotFoundException("请选择存在的文件。");
                    }
                    await using var file = File.OpenRead(PublishFilePath);
                    record = await Session.PublishAsync(name, file, revision, cancellationToken);
                }
                else
                {
                    using var text = new MemoryStream(Encoding.UTF8.GetBytes(PublishText));
                    record = await Session.PublishAsync(name, text, revision, cancellationToken);
                }
            }) || record == null)
            {
                PublishResult.Show("发布失败，详情见通知与日志。", StatusTone.Danger);
                return;
            }
            PublishResult.Show("已发布：" + Display.Bytes(record.Length) + "，" + record.BlockCount + " 块，修订 " + record.Revision
                + "。键已可复制，记录码可在记录列表中复制。", StatusTone.Success);
            PublishRevision = revision + 1;
            ReloadRecords(record.Key.ToString());
        }

        /// <summary>Copies the selected record's key.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyKeyAsync()
        {
            return CopyAsync(SelectedRecord?.KeyText, "键");
        }

        /// <summary>Copies the selected record's complete-file hash.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyHashAsync()
        {
            var record = SelectedRecord?.Entry.Record;
            return CopyAsync(record == null || record.IsWithdrawn ? null : record.FileHash, "文件哈希");
        }

        /// <summary>Copies the selected record as a record code.</summary>
        /// <returns>Completion after copying.</returns>
        [RelayCommand]
        private Task CopyRecordCodeAsync()
        {
            var record = SelectedRecord?.Entry.Record;
            return CopyAsync(record == null ? null : RecordInput.ToCode(record), "记录码");
        }

        /// <summary>Reannounces the selected record.</summary>
        /// <param name="cancellationToken">Cancels the announcement.</param>
        /// <returns>Completion after the announcement.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task AnnounceAsync(CancellationToken cancellationToken)
        {
            var record = SelectedRecord?.Entry.Record;
            if (record == null)
            {
                return;
            }
            var replicas = 0;
            if (await RunAsync("公告", async () => replicas = await Session.AnnounceAsync(record, cancellationToken)))
            {
                RecordResult.Show("公告完成：" + replicas + " 个远端节点接受了副本。", replicas == 0 ? StatusTone.Warning : StatusTone.Success);
                ReloadRecords(record.Key.ToString());
            }
        }

        /// <summary>Publishes an alias of the selected record.</summary>
        /// <param name="cancellationToken">Cancels the publication.</param>
        /// <returns>Completion after the publication.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task PublishAliasAsync(CancellationToken cancellationToken)
        {
            var record = SelectedRecord?.Entry.Record;
            if (record == null || string.IsNullOrWhiteSpace(AliasName))
            {
                RecordResult.Show("请选择一条记录并填写别名。", StatusTone.Warning);
                return;
            }
            DHTRecord? alias = null;
            if (await RunAsync("发布别名", async () => alias = await Session.PublishAliasAsync(AliasName.Trim(), record, 1, cancellationToken)) && alias != null)
            {
                RecordResult.Show("已发布别名 " + alias.Key.Name + "。", StatusTone.Success);
                AliasName = string.Empty;
                ReloadRecords(alias.Key.ToString());
            }
        }

        /// <summary>Withdraws the selected record's key.</summary>
        /// <param name="cancellationToken">Cancels the withdrawal.</param>
        /// <returns>Completion after the withdrawal.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task UnpublishAsync(CancellationToken cancellationToken)
        {
            var item = SelectedRecord;
            if (item == null)
            {
                return;
            }
            if (!item.IsOwn)
            {
                RecordResult.Show("只能撤回本节点发布者命名空间下的键。", StatusTone.Warning);
                return;
            }
            DHTRecord? withdrawal = null;
            if (await RunAsync("撤回", async () => withdrawal = await Session.UnpublishAsync(item.Name, 0, cancellationToken)) && withdrawal != null)
            {
                RecordResult.Show("已签名并复制撤回记录（修订 " + withdrawal.Revision + "）。", StatusTone.Success);
                ReloadRecords(withdrawal.Key.ToString());
            }
        }

        /// <summary>Stops serving the selected record's content locally.</summary>
        /// <returns>Completion after the change.</returns>
        [RelayCommand]
        private async Task StopProvidingAsync()
        {
            var record = SelectedRecord?.Entry.Record;
            if (record == null || record.IsWithdrawn)
            {
                return;
            }
            var stopped = false;
            if (await RunAsync("停止提供", () =>
            {
                stopped = Session.StopProviding(record.FileHash);
                return Task.CompletedTask;
            }))
            {
                RecordResult.Show(stopped ? "本节点已停止提供该文件。" : "本节点并未提供该文件。", stopped ? StatusTone.Success : StatusTone.Neutral);
                ReloadRecords(record.Key.ToString());
            }
        }

        /// <summary>Fills the pull form with the selected record.</summary>
        [RelayCommand]
        private void UseForDownload()
        {
            var record = SelectedRecord?.Entry.Record;
            if (record != null)
            {
                DownloadInput = RecordInput.ToCode(record);
                ExpectedHash = record.IsWithdrawn ? string.Empty : record.FileHash;
            }
        }

        /// <summary>Fills the provider lookup with the selected record and runs it.</summary>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task FindSelectedProvidersAsync(CancellationToken cancellationToken)
        {
            var record = SelectedRecord?.Entry.Record;
            if (record != null && !record.IsWithdrawn)
            {
                ProvidersHash = record.FileHash;
                await FindProvidersAsync(cancellationToken);
            }
        }

        /// <summary>Chooses the download destination file.</summary>
        /// <returns>Completion after the picker closed.</returns>
        [RelayCommand]
        private async Task BrowseDownloadFileAsync()
        {
            var suggested = "download.bin";
            if (RecordInput.TryParse(DownloadInput, out var key, out _, out _))
            {
                suggested = Path.GetFileName(key.Name.Replace('/', Path.DirectorySeparatorChar));
            }
            var path = await Context.Interaction.PickSaveFileAsync("保存拉取的文件", suggested);
            if (path != null)
            {
                DownloadFilePath = path;
            }
        }

        /// <summary>Pulls content by key or record code into memory or a file.</summary>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>Completion after the download.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task DownloadAsync(CancellationToken cancellationToken)
        {
            DownloadResult.Hide();
            HasPreview = false;
            if (!RecordInput.TryParse(DownloadInput, out var key, out var record, out var error))
            {
                DownloadResult.Show(error, StatusTone.Warning);
                return;
            }
            var expected = string.IsNullOrWhiteSpace(ExpectedHash) ? null : ExpectedHash.Trim().ToLowerInvariant();
            if (IsSaveMode && string.IsNullOrWhiteSpace(DownloadFilePath))
            {
                DownloadResult.Show("请先选择保存位置。", StatusTone.Warning);
                return;
            }
            IsDownloading = true;
            DownloadProgress = 0;
            DownloadProgressText = "正在解析键…";
            var started = DateTimeOffset.UtcNow;
            DHTRecord? verified = null;
            byte[]? content = null;
            var completed = await RunAsync("拉取", async () =>
            {
                var target = record ?? await Session.ResolveAsync(key, cancellationToken)
                    ?? throw new FileNotFoundException("没有可达节点持有该键的签名记录。");
                if (target.IsWithdrawn)
                {
                    throw new FileNotFoundException("发布者已撤回该键。");
                }
                var total = Math.Max(1, target.Length);
                var totalText = Display.Bytes(target.Length);
                var progress = new Progress<long>(written =>
                {
                    DownloadProgress = Math.Min(100, written * 100.0 / total);
                    DownloadProgressText = Display.Bytes(written) + " / " + totalText;
                });
                DownloadProgressText = "正在下载 " + totalText + "…";
                if (IsSaveMode)
                {
                    verified = await DownloadToFileAsync(target, DownloadFilePath, expected, progress, cancellationToken);
                }
                else
                {
                    if (target.Length > MaxPreviewDownload)
                    {
                        throw new InvalidOperationException("文件超过 " + Display.Bytes(MaxPreviewDownload) + "，请改为保存到文件。");
                    }
                    using var memory = new MemoryStream((int)target.Length);
                    verified = await Session.DownloadAsync(target, memory, expected, progress, cancellationToken);
                    content = memory.ToArray();
                }
            });
            IsDownloading = false;
            if (!completed || verified == null)
            {
                DownloadProgressText = string.Empty;
                DownloadResult.Show("拉取失败，详情见通知与日志。", StatusTone.Danger);
                return;
            }
            var elapsed = DateTimeOffset.UtcNow - started;
            DownloadProgress = 100;
            DownloadProgressText = Display.Bytes(verified.Length) + "，耗时 " + Display.Duration(elapsed) + "，"
                + Display.Bytes((long)(verified.Length / Math.Max(elapsed.TotalSeconds, 0.001))) + "/s";
            DownloadResult.Show("已拉取并通过完整校验（Merkle 块证明 + 整文件 SHA-256）" + (IsSaveMode ? "，已保存到所选文件。" : "。"), StatusTone.Success);
            if (content != null)
            {
                ShowPreview(content);
            }
            ReloadRecords(verified.Key.ToString());
        }

        /// <summary>Resolves a key or record code and shows the record details.</summary>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task ResolveAsync(CancellationToken cancellationToken)
        {
            LookupResult.Hide();
            if (!RecordInput.TryParse(ResolveInput, out var key, out _, out var error))
            {
                LookupResult.Show(error, StatusTone.Warning);
                return;
            }
            DHTRecord? record = null;
            if (!await RunAsync("解析", async () => record = await Session.ResolveAsync(key, cancellationToken)))
            {
                return;
            }
            if (record == null)
            {
                ResolvedText = string.Empty;
                LookupResult.Show("没有可达节点持有该键的签名记录。", StatusTone.Warning);
                return;
            }
            ResolvedText = Describe(record);
            LookupResult.Show(record.IsWithdrawn ? "该键已被发布者撤回。" : "已解析到签名记录。", record.IsWithdrawn ? StatusTone.Warning : StatusTone.Success);
            if (!record.IsWithdrawn)
            {
                ProvidersHash = record.FileHash;
            }
            ReloadRecords(record.Key.ToString());
        }

        /// <summary>Finds the providers of a complete-file hash.</summary>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task FindProvidersAsync(CancellationToken cancellationToken)
        {
            LookupResult.Hide();
            var hash = ProvidersHash.Trim().ToLowerInvariant();
            IReadOnlyList<DHTPeer>? providers = null;
            if (!await RunAsync("查找提供者", async () => providers = await Session.FindProvidersAsync(hash, cancellationToken)) || providers == null)
            {
                return;
            }
            Providers.Clear();
            foreach (var provider in providers)
            {
                var self = provider.NodeId == Session.NodeId ? "（本节点）" : string.Empty;
                Providers.Add(Display.ShortId(provider.NodeId) + self + "  ·  " + Display.PrimaryAddress(provider) + "  ·  " + Display.Capabilities(provider.Capabilities));
            }
            LookupResult.Show("找到 " + providers.Count + " 个提供者。", providers.Count == 0 ? StatusTone.Warning : StatusTone.Success);
        }

        /// <summary>Chooses the local copy to import.</summary>
        /// <returns>Completion after the picker closed.</returns>
        [RelayCommand]
        private async Task BrowseImportFileAsync()
        {
            var path = await Context.Interaction.PickOpenFileAsync("选择本地副本");
            if (path != null)
            {
                ImportFilePath = path;
            }
        }

        /// <summary>Imports a verified local copy of existing content and announces this node as provider.</summary>
        /// <param name="cancellationToken">Cancels the import.</param>
        /// <returns>Completion after the import.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task ImportAsync(CancellationToken cancellationToken)
        {
            ImportResult.Hide();
            if (!RecordInput.TryParseCode(ImportRecordCode, out var record))
            {
                ImportResult.Show("请输入有效的记录码（可在记录列表中复制）。", StatusTone.Warning);
                return;
            }
            var replicas = 0;
            if (await RunAsync("导入", async () =>
            {
                await using var file = File.OpenRead(ImportFilePath);
                replicas = await Session.ImportContentAsync(record, file, cancellationToken);
            }))
            {
                ImportResult.Show("内容校验通过并已登记，" + replicas + " 个远端节点接受了提供者公告。", StatusTone.Success);
                ReloadRecords(record.Key.ToString());
            }
        }

        /// <summary>Downloads into a staging file that replaces the destination only after full verification.</summary>
        /// <param name="record">The record.</param>
        /// <param name="path">The destination path.</param>
        /// <param name="expected">The optional trusted hash.</param>
        /// <param name="progress">Receives the written byte count.</param>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>The verified record.</returns>
        private async Task<DHTRecord> DownloadToFileAsync(DHTRecord record, string path, string? expected, IProgress<long> progress,
            CancellationToken cancellationToken)
        {
            var staging = path + ".part";
            try
            {
                DHTRecord verified;
                await using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    verified = await Session.DownloadAsync(record, file, expected, progress, cancellationToken);
                }
                File.Move(staging, path, true);
                return verified;
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
        }

        /// <summary>Deletes a file, ignoring failures.</summary>
        /// <param name="path">The file path.</param>
        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover staging file is harmless; the next download overwrites it.
            }
        }

        /// <summary>Formats the details of a record.</summary>
        /// <param name="record">The record.</param>
        /// <returns>Multi-line details.</returns>
        private static string Describe(DHTRecord record)
        {
            var builder = new StringBuilder();
            builder.Append("键：").AppendLine(record.Key.ToString());
            builder.Append("修订：").Append(record.Revision).Append("    到期：").AppendLine(Display.Timestamp(record.ExpiresAt));
            if (record.IsWithdrawn)
            {
                builder.Append("状态：已撤回");
                return builder.ToString();
            }
            builder.Append("大小：").Append(Display.Bytes(record.Length)).Append("（").Append(record.Length).Append(" 字节）    块：")
                .Append(record.BlockCount).Append(" × ").AppendLine(Display.Bytes(record.BlockSize));
            builder.Append("文件 SHA-256：").AppendLine(record.FileHash);
            builder.Append("Merkle 根：").Append(record.MerkleRoot);
            return builder.ToString();
        }

        /// <summary>Shows the text and hexadecimal previews of downloaded content.</summary>
        /// <param name="content">The content.</param>
        private void ShowPreview(byte[] content)
        {
            HasTextPreview = ContentPreview.TryDecodeText(content, TextPreviewBytes, out var text);
            PreviewText = HasTextPreview ? text : string.Empty;
            HexPreview = HexDump.Format(content, HexPreviewBytes);
            HasPreview = true;
        }

        /// <summary>Subscribes to the active session's record events and loads its records.</summary>
        private void Subscribe()
        {
            if (_subscribed != null)
            {
                _subscribed.RecordsChanged -= OnRecordsChanged;
            }
            _subscribed = Session;
            _subscribed.RecordsChanged += OnRecordsChanged;
            ReloadRecords();
        }

        /// <summary>Queues a reload from a record event raised on any thread.</summary>
        /// <param name="sender">The session.</param>
        /// <param name="args">The ignored event data.</param>
        private void OnRecordsChanged(object? sender, EventArgs args)
        {
            Context.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(sender, _subscribed))
                {
                    ReloadRecords();
                }
            });
        }

        /// <summary>Reloads the records, keeping or moving the selection.</summary>
        /// <param name="selectKey">The key to select afterwards, or null to keep the current selection.</param>
        private void ReloadRecords(string? selectKey = null)
        {
            var keep = selectKey ?? SelectedRecord?.KeyText;
            var now = DateTimeOffset.UtcNow;
            var nodeId = Session.NodeId;
            Records.Clear();
            RecordItemViewModel? selected = null;
            foreach (var entry in Session.Records)
            {
                var item = new RecordItemViewModel(entry, nodeId, now);
                Records.Add(item);
                if (item.KeyText == keep)
                {
                    selected = item;
                }
            }
            SelectedRecord = selected;
        }
    }
}
