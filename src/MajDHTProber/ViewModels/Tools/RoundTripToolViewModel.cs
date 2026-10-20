using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels.Tools
{
    /// <summary>One downloader choice of the end-to-end test.</summary>
    public sealed class RoundTripTargetViewModel
    {
        /// <summary>Creates a choice.</summary>
        /// <param name="session">The downloading session, or null for a short-lived client.</param>
        /// <param name="label">The display text.</param>
        public RoundTripTargetViewModel(NodeSession? session, string label)
        {
            Session = session;
            Label = label;
        }

        /// <summary>Gets the downloading session, or null for a short-lived client.</summary>
        public NodeSession? Session { get; }

        /// <summary>Gets the display text.</summary>
        public string Label { get; }
    }

    /// <summary>One executed step of the end-to-end test.</summary>
    public sealed class StageRowViewModel : TonedViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="stage">The step.</param>
        public StageRowViewModel(RoundTripStage stage)
        {
            Name = stage.Name;
            ElapsedText = Display.Duration(stage.Elapsed);
            Detail = stage.Detail;
            Tone = stage.Succeeded ? StatusTone.Success : StatusTone.Danger;
        }

        /// <summary>Gets the step name.</summary>
        public string Name { get; }

        /// <summary>Gets the step duration.</summary>
        public string ElapsedText { get; }

        /// <summary>Gets the step detail.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Tests communication between nodes end to end: the active node publishes a random payload, another hosted node or a
    /// short-lived client resolves and downloads it through the DHT, and the bytes are compared.
    /// </summary>
    public sealed partial class RoundTripToolViewModel : OperationViewModel
    {
        /// <summary>Creates the tool.</summary>
        /// <param name="context">The shared services.</param>
        public RoundTripToolViewModel(ProberContext context)
            : base(context)
        {
            RefreshTargets();
        }

        /// <summary>Gets the downloader choices.</summary>
        public ObservableCollection<RoundTripTargetViewModel> Targets { get; } = new ObservableCollection<RoundTripTargetViewModel>();

        /// <summary>Gets the executed steps.</summary>
        public ObservableCollection<StageRowViewModel> Stages { get; } = new ObservableCollection<StageRowViewModel>();

        /// <summary>Gets the result banner.</summary>
        public BannerViewModel Result { get; } = new BannerViewModel();

        /// <summary>Gets or sets the selected downloader.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsesClient))]
        public partial RoundTripTargetViewModel? SelectedTarget { get; set; }

        /// <summary>Gets or sets the endpoint the short-lived client joins through.</summary>
        [ObservableProperty]
        public partial string ClientEndpointText { get; set; } = string.Empty;

        /// <summary>Gets or sets the payload size in KiB.</summary>
        [ObservableProperty]
        public partial decimal? PayloadKilobytes { get; set; } = 256;

        /// <summary>Gets or sets whether the test key is withdrawn afterwards.</summary>
        [ObservableProperty]
        public partial bool WithdrawAfterwards { get; set; } = true;

        /// <summary>Gets whether the selected downloader is a short-lived client.</summary>
        public bool UsesClient => SelectedTarget?.Session == null;

        /// <summary>Reloads the downloader choices from the hosted sessions.</summary>
        [RelayCommand]
        public void RefreshTargets()
        {
            var keep = SelectedTarget?.Session;
            Targets.Clear();
            Targets.Add(new RoundTripTargetViewModel(null, "临时客户端（经指定端点接入网络）"));
            foreach (var session in Context.Manager.Sessions)
            {
                if (!ReferenceEquals(session, Session))
                {
                    Targets.Add(new RoundTripTargetViewModel(session, session.Label + (session.IsRunning ? string.Empty : "（未运行）")));
                }
            }
            SelectedTarget = Targets[0];
            foreach (var target in Targets)
            {
                if (target.Session != null && ReferenceEquals(target.Session, keep))
                {
                    SelectedTarget = target;
                }
            }
            if (string.IsNullOrWhiteSpace(ClientEndpointText))
            {
                var endpoints = Session.GetContactEndpoints();
                ClientEndpointText = endpoints.Count == 0 ? string.Empty : Display.IPEndPoint(endpoints[0]);
            }
        }

        /// <summary>Runs the test.</summary>
        /// <param name="cancellationToken">Cancels the test.</param>
        /// <returns>Completion after the test.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task StartTestAsync(CancellationToken cancellationToken)
        {
            Result.Hide();
            Stages.Clear();
            var bytes = (int)Math.Clamp((PayloadKilobytes ?? 256) * 1024, 1, RoundTripTester.MaxPayloadBytes);
            var target = SelectedTarget;
            RoundTripResult? result = null;
            var progress = new Progress<RoundTripStage>(stage => Stages.Add(new StageRowViewModel(stage)));
            await RunAsync("通信测试", async () =>
            {
                if (target?.Session != null)
                {
                    result = await Context.Manager.RoundTrip.RunAsync(Session, target.Session, bytes, WithdrawAfterwards, progress, cancellationToken);
                }
                else
                {
                    var endpoint = await EndpointParser.ResolveAsync(ClientEndpointText, cancellationToken);
                    result = await Context.Manager.RoundTrip.RunAsync(Session, endpoint, bytes, WithdrawAfterwards, progress, cancellationToken);
                }
            });
            if (result == null)
            {
                return;
            }
            Result.Show(result.Succeeded
                    ? "通信正常：" + Display.Bytes(result.PayloadBytes) + " 经 DHT 解析、下载并逐字节一致，下载耗时 " + Display.Duration(result.DownloadTime)
                      + "，吞吐 " + Display.Bytes((long)(result.Throughput ?? 0)) + "/s。"
                    : "通信测试未通过，请查看失败的步骤。",
                result.Succeeded ? StatusTone.Success : StatusTone.Danger);
        }
    }
}
