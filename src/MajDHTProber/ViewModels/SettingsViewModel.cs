using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MajDHTNetwork;
using MajDHTProber.Models;
using MajDHTProber.Services;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One local interface address offered as advertised address.</summary>
    public sealed class LocalAddressViewModel
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="address">The address text.</param>
        /// <param name="interfaceName">The interface name.</param>
        /// <param name="advertise">Adds the address to the advertised addresses.</param>
        public LocalAddressViewModel(string address, string interfaceName, Action<LocalAddressViewModel> advertise)
        {
            Address = address;
            InterfaceName = interfaceName;
            AdvertiseCommand = new RelayCommand(() => advertise(this));
        }

        /// <summary>Gets the address text.</summary>
        public string Address { get; }

        /// <summary>Gets the interface name.</summary>
        public string InterfaceName { get; }

        /// <summary>Gets the command that adds the address to the advertised addresses.</summary>
        public IRelayCommand AdvertiseCommand { get; }
    }

    /// <summary>The settings page: edits, validates and applies the primary node configuration and prober preferences.</summary>
    public sealed partial class SettingsViewModel : PageViewModel
    {
        /// <summary>The properties that are not part of the edited settings.</summary>
        private static readonly HashSet<string> s_stateProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IsDirty),
            nameof(HasErrors),
            nameof(IsBusy),
            nameof(PrimaryNodeIdText),
            nameof(CanResetIdentity),
        };

        /// <summary>Whether fields are being loaded, which must not mark the page dirty.</summary>
        private bool _loading;

        /// <summary>Creates the page.</summary>
        /// <param name="context">The shared services.</param>
        public SettingsViewModel(ProberContext context)
            : base(context)
        {
            Load(context.Manager.Settings);
            ReloadLocalAddresses();
        }

        /// <summary>Gets the validation errors of the last save attempt.</summary>
        public ObservableCollection<string> Errors { get; } = new ObservableCollection<string>();

        /// <summary>Gets the local interface addresses.</summary>
        public ObservableCollection<LocalAddressViewModel> LocalAddresses { get; } = new ObservableCollection<LocalAddressViewModel>();

        /// <summary>Gets the transport options.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "TCP", "UDP" };

        /// <summary>Gets the theme options.</summary>
        public IReadOnlyList<string> ThemeOptions { get; } = new[] { "跟随系统", "浅色", "深色" };

        /// <summary>Gets the block size options in bytes.</summary>
        public IReadOnlyList<int> BlockSizeOptions { get; } = new[] { 1024, 2048, 4096, 8192, 16384, 32768 };

        /// <summary>Gets the capture severity options.</summary>
        public IReadOnlyList<LogLevelOption> LogLevelOptions { get; } = new[]
        {
            new LogLevelOption("调试 (Debug)", LogLevel.Debug),
            new LogLevelOption("信息 (Info)", LogLevel.Information),
            new LogLevelOption("警告 (Warning)", LogLevel.Warning),
            new LogLevelOption("错误 (Error)", LogLevel.Error),
        };

        /// <summary>Gets or sets the listen addresses.</summary>
        [ObservableProperty]
        public partial string ListenAddresses { get; set; } = string.Empty;

        /// <summary>Gets or sets the advertised addresses.</summary>
        [ObservableProperty]
        public partial string AdvertisedAddresses { get; set; } = string.Empty;

        /// <summary>Gets or sets whether TCP is enabled.</summary>
        [ObservableProperty]
        public partial bool TcpEnabled { get; set; }

        /// <summary>Gets or sets the TCP port.</summary>
        [ObservableProperty]
        public partial decimal? TcpPort { get; set; }

        /// <summary>Gets or sets whether UDP is enabled.</summary>
        [ObservableProperty]
        public partial bool UdpEnabled { get; set; }

        /// <summary>Gets or sets the UDP port.</summary>
        [ObservableProperty]
        public partial decimal? UdpPort { get; set; }

        /// <summary>Gets or sets the preferred transport index: 0 for TCP, 1 for UDP.</summary>
        [ObservableProperty]
        public partial int PreferredTransportIndex { get; set; }

        /// <summary>Gets or sets the download capability.</summary>
        [ObservableProperty]
        public partial bool CapabilityDownload { get; set; }

        /// <summary>Gets or sets the provide capability.</summary>
        [ObservableProperty]
        public partial bool CapabilityProvide { get; set; }

        /// <summary>Gets or sets the relay capability.</summary>
        [ObservableProperty]
        public partial bool CapabilityRelay { get; set; }

        /// <summary>Gets or sets the bootstrap endpoints, one per line.</summary>
        [ObservableProperty]
        public partial string BootstrapText { get; set; } = string.Empty;

        /// <summary>Gets or sets the identity work difficulty.</summary>
        [ObservableProperty]
        public partial decimal? IdentityWorkBits { get; set; }

        /// <summary>Gets or sets whether identities are stored on disk.</summary>
        [ObservableProperty]
        public partial bool PersistIdentity { get; set; }

        /// <summary>Gets or sets whether the primary node saves its routing table when it stops and restores it when it starts.</summary>
        [ObservableProperty]
        public partial bool PersistRoutingTable { get; set; }

        /// <summary>Gets or sets whether NAT traversal is enabled.</summary>
        [ObservableProperty]
        public partial bool EnableNatTraversal { get; set; }

        /// <summary>Gets or sets the request timeout in seconds.</summary>
        [ObservableProperty]
        public partial decimal? RequestTimeoutSeconds { get; set; }

        /// <summary>Gets or sets the UDP transmissions per request.</summary>
        [ObservableProperty]
        public partial decimal? UdpAttempts { get; set; }

        /// <summary>Gets or sets the block size.</summary>
        [ObservableProperty]
        public partial int BlockSize { get; set; }

        /// <summary>Gets or sets the maintenance interval in minutes.</summary>
        [ObservableProperty]
        public partial decimal? MaintenanceIntervalMinutes { get; set; }

        /// <summary>Gets or sets the publication lease in hours.</summary>
        [ObservableProperty]
        public partial decimal? RecordLifetimeHours { get; set; }

        /// <summary>Gets or sets the captured log severity.</summary>
        [ObservableProperty]
        public partial LogLevelOption? SelectedLogLevel { get; set; }

        /// <summary>Gets or sets the theme index: 0 system, 1 light, 2 dark.</summary>
        [ObservableProperty]
        public partial int ThemeIndex { get; set; }

        /// <summary>Gets or sets whether the primary node starts with the application.</summary>
        [ObservableProperty]
        public partial bool AutoStart { get; set; }

        /// <summary>Gets or sets whether there are unsaved changes.</summary>
        [ObservableProperty]
        public partial bool IsDirty { get; set; }

        /// <summary>Gets or sets whether the last save attempt failed validation.</summary>
        [ObservableProperty]
        public partial bool HasErrors { get; set; }

        /// <summary>Gets or sets the primary node identifier text.</summary>
        [ObservableProperty]
        public partial string PrimaryNodeIdText { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the primary identity can be reset, which requires a stopped primary node.</summary>
        [ObservableProperty]
        public partial bool CanResetIdentity { get; set; }

        /// <summary>Reloads the primary identity state when the page becomes visible.</summary>
        public override void OnActivated()
        {
            RefreshIdentity();
        }

        /// <summary>Refreshes the primary identity state.</summary>
        /// <param name="now">The current time.</param>
        public override void OnTick(DateTimeOffset now)
        {
            RefreshIdentity();
        }

        /// <summary>
        /// Applies a theme and persists only the theme, leaving other unsaved edits untouched and not marking the page dirty
        /// because of the theme.
        /// </summary>
        /// <param name="index">The theme index: 0 system, 1 light, 2 dark.</param>
        public void ApplyThemeChoice(int index)
        {
            var wasDirty = IsDirty;
            ThemeIndex = index;
            IsDirty = wasDirty;
            var saved = Context.Manager.Settings;
            saved.Theme = index switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "Default",
            };
            try
            {
                Context.Manager.SaveSettings(saved);
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or SettingsValidationException)
            {
                Notify("主题已切换，但未能保存：" + exception.Message, StatusTone.Warning);
            }
        }

        /// <summary>Builds settings from the edited fields.</summary>
        /// <returns>The settings.</returns>
        public ProberSettings ToSettings()
        {
            var bootstrap = new List<string>();
            foreach (var line in BootstrapText.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bootstrap.Add(line);
            }
            return new ProberSettings
            {
                ListenAddresses = ListenAddresses.Trim(),
                AdvertisedAddresses = AdvertisedAddresses.Trim(),
                TcpEnabled = TcpEnabled,
                TcpPort = (int)(TcpPort ?? 0),
                UdpEnabled = UdpEnabled,
                UdpPort = (int)(UdpPort ?? 0),
                PreferredTransport = PreferredTransportIndex == 1 ? DHTTransport.Udp : DHTTransport.Tcp,
                CapabilityDownload = CapabilityDownload,
                CapabilityProvide = CapabilityProvide,
                CapabilityRelay = CapabilityRelay,
                BootstrapEndpoints = bootstrap,
                IdentityWorkBits = (int)(IdentityWorkBits ?? 12),
                PersistIdentity = PersistIdentity,
                PersistRoutingTable = PersistRoutingTable,
                EnableNatTraversal = EnableNatTraversal,
                RequestTimeoutSeconds = (double)(RequestTimeoutSeconds ?? 5),
                UdpAttempts = (int)(UdpAttempts ?? 3),
                BlockSize = BlockSize,
                MaintenanceIntervalMinutes = (double)(MaintenanceIntervalMinutes ?? 5),
                RecordLifetimeHours = (double)(RecordLifetimeHours ?? 24),
                MinimumLogLevel = SelectedLogLevel?.Level ?? LogLevel.Information,
                Theme = ThemeIndex switch
                {
                    1 => "Light",
                    2 => "Dark",
                    _ => "Default",
                },
                AutoStart = AutoStart,
            };
        }

        /// <summary>Marks the page dirty when an edited field changed.</summary>
        /// <param name="e">The event data.</param>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (!_loading && e.PropertyName != null && !s_stateProperties.Contains(e.PropertyName))
            {
                IsDirty = true;
            }
        }

        /// <summary>Previews a theme immediately.</summary>
        /// <param name="value">The theme index.</param>
        partial void OnThemeIndexChanged(int value)
        {
            Context.Interaction.ApplyTheme(value switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "Default",
            });
        }

        /// <summary>Validates and saves the settings; the primary node uses them at its next start.</summary>
        [RelayCommand]
        private void Save()
        {
            if (TrySave())
            {
                Notify(Context.Manager.Primary.IsRunning ? "设置已保存，将在主节点下次启动时生效。" : "设置已保存。", StatusTone.Success);
            }
        }

        /// <summary>Validates and saves the settings, then restarts the primary node when it is running.</summary>
        /// <returns>Completion after the restart.</returns>
        [RelayCommand]
        private async Task SaveAndRestartAsync()
        {
            var settings = ToSettings();
            if (!Validate(settings))
            {
                return;
            }
            if (await RunAsync("应用设置", () => Context.Manager.ApplySettingsAsync(settings)))
            {
                IsDirty = false;
                Notify("设置已保存并应用到主节点。", StatusTone.Success);
            }
        }

        /// <summary>Discards unsaved changes.</summary>
        [RelayCommand]
        private void Revert()
        {
            Load(Context.Manager.Settings);
            Context.Interaction.ApplyTheme(Context.Manager.Settings.Theme);
        }

        /// <summary>Loads the default settings without saving them.</summary>
        [RelayCommand]
        private void ResetDefaults()
        {
            Load(new ProberSettings());
            IsDirty = true;
        }

        /// <summary>Appends a local address to the advertised addresses.</summary>
        /// <param name="address">The address.</param>
        private void AdvertiseAddress(LocalAddressViewModel address)
        {
            if (AdvertisedAddresses.Contains(address.Address, StringComparison.Ordinal))
            {
                return;
            }
            AdvertisedAddresses = string.IsNullOrWhiteSpace(AdvertisedAddresses) ? address.Address : AdvertisedAddresses.Trim() + ", " + address.Address;
        }

        /// <summary>Reloads the local interface addresses.</summary>
        [RelayCommand]
        private void ReloadLocalAddresses()
        {
            LocalAddresses.Clear();
            try
            {
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (IPAddress.IsLoopback(address) || (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal))
                        {
                            continue;
                        }
                        LocalAddresses.Add(new LocalAddressViewModel(address.ToString(), networkInterface.Name, AdvertiseAddress));
                    }
                }
            }
            catch (NetworkInformationException exception)
            {
                Notify("无法读取本机网卡地址：" + exception.Message, StatusTone.Warning);
            }
        }

        /// <summary>Deletes the stored primary identity so the next start generates a new node identifier.</summary>
        [RelayCommand]
        private void ResetIdentity()
        {
            if (Context.Manager.Primary.State != NodeSessionState.Stopped && Context.Manager.Primary.State != NodeSessionState.Faulted)
            {
                Notify("请先停止主节点再重置身份。", StatusTone.Warning);
                return;
            }
            try
            {
                Context.Manager.Vault.Delete(NodeSessionManager.PrimaryIdentityName);
                Notify("主节点身份已删除，下次启动将生成新的节点 ID。", StatusTone.Success);
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
            {
                Notify("删除身份失败：" + exception.Message, StatusTone.Danger);
            }
            RefreshIdentity();
        }

        /// <summary>Validates and saves the edited settings.</summary>
        /// <returns>Whether the settings were saved.</returns>
        private bool TrySave()
        {
            var settings = ToSettings();
            if (!Validate(settings))
            {
                return false;
            }
            try
            {
                Context.Manager.SaveSettings(settings);
                IsDirty = false;
                return true;
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or SettingsValidationException)
            {
                Notify("保存设置失败：" + exception.Message, StatusTone.Danger);
                return false;
            }
        }

        /// <summary>Shows the validation errors of settings.</summary>
        /// <param name="settings">The settings.</param>
        /// <returns>Whether the settings are valid.</returns>
        private bool Validate(ProberSettings settings)
        {
            Errors.Clear();
            foreach (var error in NodeOptionsBuilder.Validate(settings))
            {
                Errors.Add(error);
            }
            HasErrors = Errors.Count != 0;
            if (HasErrors)
            {
                Notify("设置未通过校验，请修正标出的问题。", StatusTone.Warning);
            }
            return !HasErrors;
        }

        /// <summary>Loads fields from settings without marking the page dirty.</summary>
        /// <param name="settings">The settings.</param>
        private void Load(ProberSettings settings)
        {
            _loading = true;
            try
            {
                ListenAddresses = settings.ListenAddresses;
                AdvertisedAddresses = settings.AdvertisedAddresses;
                TcpEnabled = settings.TcpEnabled;
                TcpPort = settings.TcpPort;
                UdpEnabled = settings.UdpEnabled;
                UdpPort = settings.UdpPort;
                PreferredTransportIndex = settings.PreferredTransport == DHTTransport.Udp ? 1 : 0;
                CapabilityDownload = settings.CapabilityDownload;
                CapabilityProvide = settings.CapabilityProvide;
                CapabilityRelay = settings.CapabilityRelay;
                BootstrapText = string.Join(Environment.NewLine, settings.BootstrapEndpoints);
                IdentityWorkBits = settings.IdentityWorkBits;
                PersistIdentity = settings.PersistIdentity;
                PersistRoutingTable = settings.PersistRoutingTable;
                EnableNatTraversal = settings.EnableNatTraversal;
                RequestTimeoutSeconds = (decimal)settings.RequestTimeoutSeconds;
                UdpAttempts = settings.UdpAttempts;
                BlockSize = settings.BlockSize;
                MaintenanceIntervalMinutes = (decimal)settings.MaintenanceIntervalMinutes;
                RecordLifetimeHours = (decimal)settings.RecordLifetimeHours;
                SelectedLogLevel = FindLevel(settings.MinimumLogLevel);
                ThemeIndex = settings.Theme switch
                {
                    "Light" => 1,
                    "Dark" => 2,
                    _ => 0,
                };
                AutoStart = settings.AutoStart;
                Errors.Clear();
                HasErrors = false;
                IsDirty = false;
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Finds the option of a severity.</summary>
        /// <param name="level">The severity.</param>
        /// <returns>The option, defaulting to information.</returns>
        private LogLevelOption FindLevel(LogLevel level)
        {
            foreach (var option in LogLevelOptions)
            {
                if (option.Level == level)
                {
                    return option;
                }
            }
            return LogLevelOptions[1];
        }

        /// <summary>Refreshes the primary identity state.</summary>
        private void RefreshIdentity()
        {
            var primary = Context.Manager.Primary;
            PrimaryNodeIdText = primary.NodeId ?? "尚未生成（主节点首次启动时生成）";
            CanResetIdentity = primary.State == NodeSessionState.Stopped || primary.State == NodeSessionState.Faulted;
        }
    }
}
