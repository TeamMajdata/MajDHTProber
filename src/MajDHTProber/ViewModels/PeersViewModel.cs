using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One state filter chip of the peer list.</summary>
    public sealed partial class PeerFilterOption : ViewModelBase
    {
        /// <summary>Creates a filter.</summary>
        /// <param name="label">The chip label.</param>
        /// <param name="state">The matched state, or null to match every peer.</param>
        public PeerFilterOption(string label, PeerConnectionState? state)
        {
            Label = label;
            State = state;
        }

        /// <summary>Gets the chip label.</summary>
        public string Label { get; }

        /// <summary>Gets the matched state, or null to match every peer.</summary>
        public PeerConnectionState? State { get; }

        /// <summary>Gets or sets the number of matching peers.</summary>
        [ObservableProperty]
        public partial int Count { get; set; }
    }

    /// <summary>The peer page: lists every known node, adds nodes by endpoint and shows details of the selected node.</summary>
    public sealed partial class PeersViewModel : PageViewModel
    {
        /// <summary>The rows of the active session keyed by node identifier.</summary>
        private readonly Dictionary<string, PeerItemViewModel> _items = new Dictionary<string, PeerItemViewModel>(StringComparer.Ordinal);
        /// <summary>The session whose peer events are subscribed.</summary>
        private NodeSession? _subscribed;
        /// <summary>Counts ticks to reconcile routing state periodically.</summary>
        private int _ticks;

        /// <summary>Creates the page.</summary>
        /// <param name="context">The shared services.</param>
        public PeersViewModel(ProberContext context)
            : base(context)
        {
            Filters = new[]
            {
                new PeerFilterOption("全部", null),
                new PeerFilterOption("已连接", PeerConnectionState.Connected),
                new PeerFilterOption("可达", PeerConnectionState.Reachable),
                new PeerFilterOption("已发现", PeerConnectionState.Discovered),
                new PeerFilterOption("已断开", PeerConnectionState.Disconnected),
                new PeerFilterOption("已过期", PeerConnectionState.Expired),
            };
            SelectedFilter = Filters[0];
            Subscribe();
        }

        /// <summary>Gets the state filter chips.</summary>
        public IReadOnlyList<PeerFilterOption> Filters { get; }

        /// <summary>Gets the rows matching the search and filter, in display order.</summary>
        public ObservableCollection<PeerItemViewModel> VisiblePeers { get; } = new ObservableCollection<PeerItemViewModel>();

        /// <summary>Gets the transport names offered when adding a node.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "TCP 优先", "UDP 优先" };

        /// <summary>Gets the result of the latest add attempt.</summary>
        public BannerViewModel AddResult { get; } = new BannerViewModel();

        /// <summary>Gets or sets the search text.</summary>
        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        /// <summary>Gets or sets the selected state filter.</summary>
        [ObservableProperty]
        public partial PeerFilterOption SelectedFilter { get; set; }

        /// <summary>Gets or sets the selected row.</summary>
        [ObservableProperty]
        public partial PeerItemViewModel? SelectedPeer { get; set; }

        /// <summary>Gets or sets the detail panel of the selected row.</summary>
        [ObservableProperty]
        public partial PeerDetailViewModel? Detail { get; set; }

        /// <summary>Gets or sets the endpoint entered in the add form.</summary>
        [ObservableProperty]
        public partial string AddEndpointText { get; set; } = string.Empty;

        /// <summary>Gets or sets the transport tried first when adding: 0 for TCP, 1 for UDP.</summary>
        [ObservableProperty]
        public partial int AddTransportIndex { get; set; }

        /// <summary>Gets or sets whether the add form is expanded.</summary>
        [ObservableProperty]
        public partial bool IsAddFormOpen { get; set; }

        /// <summary>Gets or sets whether the active session knows no peer at all.</summary>
        [ObservableProperty]
        public partial bool IsEmpty { get; set; } = true;

        /// <summary>Reloads the rows after the active session changed.</summary>
        public override void OnActiveSessionChanged()
        {
            Subscribe();
        }

        /// <summary>Refreshes states, counts and the selected detail.</summary>
        /// <param name="now">The current time.</param>
        public override void OnTick(DateTimeOffset now)
        {
            if (++_ticks % 5 == 0)
            {
                Session.RefreshRoutingState();
            }
            foreach (var item in _items.Values)
            {
                item.Update(item.Snapshot, now, Session.GetStatistics(item.NodeId));
            }
            ApplyFilter();
            Detail?.Refresh(now);
        }

        /// <summary>Called by the generated property when the search text changed.</summary>
        /// <param name="value">The new search text.</param>
        partial void OnSearchTextChanged(string value)
        {
            ApplyFilter();
        }

        /// <summary>Called by the generated property when the filter changed.</summary>
        /// <param name="value">The new filter.</param>
        partial void OnSelectedFilterChanged(PeerFilterOption value)
        {
            ApplyFilter();
        }

        /// <summary>Called by the generated property when the selection changed.</summary>
        /// <param name="value">The new selection.</param>
        partial void OnSelectedPeerChanged(PeerItemViewModel? value)
        {
            if (value == null)
            {
                return;
            }
            if (Detail?.Item != value)
            {
                Detail = new PeerDetailViewModel(Context, value, RelayCandidates, OnForgotten);
            }
        }

        /// <summary>Adds a node by endpoint.</summary>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>Completion after the attempt.</returns>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task AddPeerAsync(CancellationToken cancellationToken)
        {
            AddResult.Hide();
            var text = AddEndpointText.Trim();
            if (text.Length == 0)
            {
                AddResult.Show("请输入要添加节点的端点，例如 192.168.1.20:42000。", StatusTone.Warning);
                return;
            }
            AddPeerResult? result = null;
            var completed = await RunAsync("添加节点", async () =>
            {
                var endpoint = await EndpointParser.ResolveAsync(text, cancellationToken);
                result = await Session.AddPeerAsync(endpoint, new AddPeerOptions
                {
                    Transport = AddTransportIndex == 1 ? DHTTransport.Udp : DHTTransport.Tcp,
                }, cancellationToken);
            });
            if (!completed || result == null)
            {
                AddResult.Show("添加失败，详情见通知与日志。", StatusTone.Danger);
                return;
            }
            if (result.Peer == null)
            {
                AddResult.Show(result.Note ?? "添加失败。", StatusTone.Danger);
                return;
            }
            var summary = "已添加 " + Display.ShortId(result.Peer.NodeId) + "，耗时 " + Display.Duration(result.Elapsed)
                + (result.AdmittedToRoutingTable ? "，已进入路由表" : string.Empty)
                + "，邻居查找后路由表共 " + result.RoutingTableSize + " 个节点。"
                + (result.Note == null ? string.Empty : " " + result.Note);
            AddResult.Show(summary, result.Note == null ? StatusTone.Success : StatusTone.Warning);
            AddEndpointText = string.Empty;
            if (_items.TryGetValue(result.Peer.NodeId, out var item))
            {
                SelectedPeer = item;
            }
        }

        /// <summary>Refreshes the neighborhood of the active node.</summary>
        /// <returns>Completion after the lookup.</returns>
        [RelayCommand]
        private async Task RefreshNeighborsAsync()
        {
            var found = 0;
            if (await RunAsync("刷新邻居", async () => found = (await Session.RefreshNeighborsAsync()).Count))
            {
                Notify("邻居查找完成：找到 " + found + " 个可响应节点。", StatusTone.Success);
            }
        }

        /// <summary>Forgets every disconnected or expired peer.</summary>
        [RelayCommand]
        private void ClearInactive()
        {
            var now = DateTimeOffset.UtcNow;
            var removed = 0;
            foreach (var item in new List<PeerItemViewModel>(_items.Values))
            {
                var state = item.Snapshot.GetState(now);
                if (state == PeerConnectionState.Disconnected || state == PeerConnectionState.Expired)
                {
                    Session.ForgetPeer(item.NodeId, false);
                    removed++;
                }
            }
            Notify("已清除 " + removed + " 个已断开或已过期的节点。", StatusTone.Neutral);
        }

        /// <summary>Toggles the add form.</summary>
        [RelayCommand]
        private void ToggleAddForm()
        {
            IsAddFormOpen = !IsAddFormOpen;
        }

        /// <summary>Returns the rows that offer relay assistance.</summary>
        /// <returns>The relay rows.</returns>
        private IReadOnlyList<PeerItemViewModel> RelayCandidates()
        {
            var result = new List<PeerItemViewModel>();
            foreach (var item in _items.Values)
            {
                if (item.IsRelay && !item.Contact.IsExpired)
                {
                    result.Add(item);
                }
            }
            result.Sort((left, right) => string.CompareOrdinal(left.Title, right.Title));
            return result;
        }

        /// <summary>Clears the selection after the detail panel forgot its peer.</summary>
        /// <param name="nodeId">The forgotten node.</param>
        private void OnForgotten(string nodeId)
        {
            if (Detail?.Item.NodeId == nodeId)
            {
                Detail = null;
                SelectedPeer = null;
            }
        }

        /// <summary>Subscribes to the active session's peer events and loads its peers.</summary>
        private void Subscribe()
        {
            if (_subscribed != null)
            {
                _subscribed.Peers.PeerChanged -= OnPeerChanged;
                _subscribed.Peers.PeerForgotten -= OnPeerForgotten;
            }
            _subscribed = Session;
            _subscribed.Peers.PeerChanged += OnPeerChanged;
            _subscribed.Peers.PeerForgotten += OnPeerForgotten;
            _items.Clear();
            Detail = null;
            SelectedPeer = null;
            var now = DateTimeOffset.UtcNow;
            foreach (var snapshot in _subscribed.Peers.Snapshot())
            {
                _items[snapshot.NodeId] = new PeerItemViewModel(snapshot, now);
            }
            VisiblePeers.Clear();
            ApplyFilter();
        }

        /// <summary>Queues a row update from a peer event raised on any thread.</summary>
        /// <param name="sender">The directory.</param>
        /// <param name="snapshot">The changed peer.</param>
        private void OnPeerChanged(object? sender, PeerSnapshot snapshot)
        {
            Context.Dispatcher.Post(() =>
            {
                if (!ReferenceEquals(sender, _subscribed?.Peers))
                {
                    return;
                }
                var now = DateTimeOffset.UtcNow;
                if (_items.TryGetValue(snapshot.NodeId, out var item))
                {
                    item.Update(snapshot, now, Session.GetStatistics(snapshot.NodeId));
                }
                else
                {
                    _items[snapshot.NodeId] = new PeerItemViewModel(snapshot, now);
                }
                ApplyFilter();
            });
        }

        /// <summary>Queues a row removal from a peer event raised on any thread.</summary>
        /// <param name="sender">The directory.</param>
        /// <param name="nodeId">The forgotten node.</param>
        private void OnPeerForgotten(object? sender, string nodeId)
        {
            Context.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(sender, _subscribed?.Peers) && _items.Remove(nodeId))
                {
                    OnForgotten(nodeId);
                    ApplyFilter();
                }
            });
        }

        /// <summary>Recomputes the filter counts and the visible rows.</summary>
        private void ApplyFilter()
        {
            var counts = new int[Filters.Count];
            var visible = new List<PeerItemViewModel>();
            foreach (var item in _items.Values)
            {
                counts[0]++;
                for (var index = 1; index < Filters.Count; index++)
                {
                    if (Filters[index].State == item.State)
                    {
                        counts[index]++;
                    }
                }
                if ((SelectedFilter?.State == null || SelectedFilter.State == item.State) && item.Matches(SearchText))
                {
                    visible.Add(item);
                }
            }
            for (var index = 0; index < Filters.Count; index++)
            {
                Filters[index].Count = counts[index];
            }
            visible.Sort(CompareRows);
            CollectionSync.Sync(VisiblePeers, visible);
            IsEmpty = _items.Count == 0;
        }

        /// <summary>Orders rows by connection health, then by title.</summary>
        /// <param name="left">The first row.</param>
        /// <param name="right">The second row.</param>
        /// <returns>The sort order.</returns>
        private static int CompareRows(PeerItemViewModel left, PeerItemViewModel right)
        {
            var byState = Rank(left.State).CompareTo(Rank(right.State));
            return byState != 0 ? byState : string.CompareOrdinal(left.Title, right.Title);
        }

        /// <summary>Ranks connection states for sorting.</summary>
        /// <param name="state">The state.</param>
        /// <returns>The rank, healthiest first.</returns>
        private static int Rank(PeerConnectionState state)
        {
            return state switch
            {
                PeerConnectionState.Connected => 0,
                PeerConnectionState.Reachable => 1,
                PeerConnectionState.Discovered => 2,
                PeerConnectionState.Disconnected => 3,
                _ => 4,
            };
        }
    }
}
