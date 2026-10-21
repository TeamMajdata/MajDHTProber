using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels.Tools
{
    /// <summary>
    /// Reads one verified block from a chosen provider, optionally through a chosen Forwarder, to debug individual
    /// transfers and forwarding paths.
    /// </summary>
    public sealed partial class BlockToolViewModel : OperationViewModel
    {
        /// <summary>The record of the last resolution.</summary>
        private DHTRecord? _record;

        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public BlockToolViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Gets the providers of the resolved record.</summary>
        public ObservableCollection<PeerChoiceViewModel> Providers { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets the Forwarder choices, starting with "no Forwarder".</summary>
        public ObservableCollection<PeerChoiceViewModel> Forwarders { get; } = new ObservableCollection<PeerChoiceViewModel>();

        /// <summary>Gets the result banner.</summary>
        public BannerViewModel Result { get; } = new BannerViewModel();

        /// <summary>Gets or sets the key or record code.</summary>
        [ObservableProperty]
        public partial string RecordText { get; set; } = string.Empty;

        /// <summary>Gets or sets the summary of the resolved record.</summary>
        [ObservableProperty]
        public partial string RecordSummary { get; set; } = "尚未解析";

        /// <summary>Gets or sets the selected provider.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedProvider { get; set; }

        /// <summary>Gets or sets the selected Forwarder.</summary>
        [ObservableProperty]
        public partial PeerChoiceViewModel? SelectedForwarder { get; set; }

        /// <summary>Gets or sets the block index.</summary>
        [ObservableProperty]
        public partial decimal? BlockIndex { get; set; } = 0;

        /// <summary>Gets or sets the maximum block index of the resolved record.</summary>
        [ObservableProperty]
        public partial decimal MaxBlockIndex { get; set; }

        /// <summary>Gets or sets the details of the read block.</summary>
        [ObservableProperty]
        public partial string BlockInfo { get; set; } = string.Empty;

        /// <summary>Gets or sets the hexadecimal dump of the read block.</summary>
        [ObservableProperty]
        public partial string HexText { get; set; } = string.Empty;

        /// <summary>Reloads the Forwarder choices from the active session's peers.</summary>
        public void RefreshForwarders()
        {
            var keep = SelectedForwarder?.Peer?.NodeId;
            Forwarders.Clear();
            Forwarders.Add(new PeerChoiceViewModel(null, "不指定（直连，失败时自动回退 Forwarder）"));
            foreach (var choice in PeerChoiceViewModel.FromSession(Session, DHTCapabilities.Forwarder))
            {
                Forwarders.Add(choice);
            }
            SelectedForwarder = Forwarders[0];
            foreach (var forwarder in Forwarders)
            {
                if (forwarder.Peer?.NodeId == keep && keep != null)
                {
                    SelectedForwarder = forwarder;
                }
            }
        }

        /// <summary>Resolves the record and finds its providers.</summary>
        /// <param name="cancellationToken">Cancels the lookups.</param>
        /// <returns>Completion after the lookups.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task ResolveAsync(CancellationToken cancellationToken)
        {
            Result.Hide();
            RefreshForwarders();
            if (!RecordInput.TryParse(RecordText, out var key, out var record, out var error))
            {
                Result.Show(error, StatusTone.Warning);
                return;
            }
            IReadOnlyList<DHTPeer>? providers = null;
            if (!await RunAsync("解析", async () =>
            {
                record ??= await Session.ResolveAsync(key, cancellationToken);
                if (record == null || record.IsWithdrawn)
                {
                    throw new System.IO.FileNotFoundException(record == null ? "没有可达节点持有该键的签名记录。" : "该键已被撤回。");
                }
                providers = await Session.FindProvidersAsync(record.FileHash, cancellationToken);
            }) || record == null || providers == null)
            {
                return;
            }
            _record = record;
            MaxBlockIndex = Math.Max(0, record.BlockCount - 1);
            BlockIndex = Math.Min(BlockIndex ?? 0, MaxBlockIndex);
            RecordSummary = record.Key.Name + " · " + Display.Bytes(record.Length) + " · " + record.BlockCount + " 块 × " + Display.Bytes(record.BlockSize);
            Providers.Clear();
            foreach (var provider in providers)
            {
                Providers.Add(PeerChoiceViewModel.For(provider, provider.NodeId == Session.NodeId ? "本节点" : null));
            }
            SelectedProvider = Providers.Count == 0 ? null : Providers[0];
            Result.Show("找到 " + providers.Count + " 个提供者。", providers.Count == 0 ? StatusTone.Warning : StatusTone.Success);
        }

        /// <summary>Reads and verifies the selected block.</summary>
        /// <param name="cancellationToken">Cancels the transfer.</param>
        /// <returns>Completion after the transfer.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            Result.Hide();
            var record = _record;
            var provider = SelectedProvider?.Peer;
            if (record == null || provider == null)
            {
                Result.Show("请先解析记录并选择提供者。", StatusTone.Warning);
                return;
            }
            var index = (int)Math.Clamp(BlockIndex ?? 0, 0, MaxBlockIndex);
            var forwarder = SelectedForwarder?.Peer;
            ReadOnlyMemory<byte> block = default;
            var stopwatch = Stopwatch.StartNew();
            if (!await RunAsync("读取块", async () => block = await Session.ReadBlockAsync(record, provider, index, forwarder, cancellationToken)))
            {
                BlockInfo = string.Empty;
                HexText = string.Empty;
                return;
            }
            stopwatch.Stop();
            BlockInfo = "块 " + index + " · " + Display.Bytes(block.Length) + " · 耗时 " + Display.Duration(stopwatch.Elapsed)
                + (forwarder == null ? string.Empty : " · 经 Forwarder " + Display.ShortId(forwarder.NodeId, 8, 4)) + Environment.NewLine
                + "块 SHA-256：" + ContentPreview.Sha256(block.Span);
            HexText = HexDump.Format(block.Span, 4096);
            Result.Show("块已通过长度与 Merkle 位置校验。", StatusTone.Success);
        }
    }
}
