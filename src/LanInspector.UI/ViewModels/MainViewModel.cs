using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Analysis;
using LanInspector.Core.Capture;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Dns;
using LanInspector.Core.Identity;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Scanning;
using LanInspector.Core.Traffic;

namespace LanInspector.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string DiscoveryFilter = "ip or arp or udp port 53 or udp port 5353 or udp port 67 or udp port 68";

    private readonly ICaptureProvider _captureProvider;
    private readonly IReadOnlyList<IDeviceObservingAnalyzer> _analyzers;
    private readonly IReadOnlyList<KnownDeviceDefinition> _knownDevices;
    private readonly OuiVendorLookup _vendorLookup;
    private readonly HostnameResolver _hostnameResolver;
    private readonly PortScanner _portScanner;
    private readonly IRouteDiagnosticsService _routeDiagnostics;
    private readonly ReachabilityClassifier _reachabilityClassifier;
    private readonly ITerminalLauncher _terminalLauncher;
    private readonly Action _clearDeviceStore;
    private readonly Action<Action> _dispatchToUi;
    private readonly TrafficFlowService _trafficFlowService = new();
    private readonly TrafficFlowAnalyzer _trafficFlowAnalyzer;
    private readonly LldpAnalyzer _lldpAnalyzer = new();
    private readonly HashSet<string> _reverseDnsAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRouteRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reverseDnsAttemptsLock = new();
    private readonly object _routeRefreshLock = new();
    private readonly SemaphoreSlim _routeDiagnosticsGate = new(2);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private int _runNumber;
    private bool _currentRunArchived;
    private bool _disposed;

    public MainViewModel(
        ICaptureProvider captureProvider,
        IReadOnlyList<IDeviceObservingAnalyzer> analyzers,
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        OuiVendorLookup vendorLookup,
        HostnameResolver hostnameResolver,
        PortScanner portScanner,
        IRouteDiagnosticsService routeDiagnostics,
        ReachabilityClassifier reachabilityClassifier,
        ITerminalLauncher terminalLauncher,
        Action clearDeviceStore,
        Action<Action> dispatchToUi,
        ITailscaleService tailscale,
        KnownDevicesConfiguration knownDevicesConfig,
        IDnsFilterService? dnsFilterService = null)
    {
        _captureProvider = captureProvider;
        _analyzers = analyzers;
        _knownDevices = knownDevices;
        _vendorLookup = vendorLookup;
        _hostnameResolver = hostnameResolver;
        _portScanner = portScanner;
        _routeDiagnostics = routeDiagnostics;
        _reachabilityClassifier = reachabilityClassifier;
        _terminalLauncher = terminalLauncher;
        _clearDeviceStore = clearDeviceStore;
        _dispatchToUi = dispatchToUi;
        _trafficFlowAnalyzer = new TrafficFlowAnalyzer(_trafficFlowService);

        TrafficTab = new TrafficTabViewModel(_trafficFlowService);
        TopologyTab = new TopologyTabViewModel(new LocalNetworkProfileProvider(), tailscale, knownDevicesConfig);
        DnsTab = new DnsTabViewModel(dnsFilterService);

        _captureProvider.PacketCaptured += OnPacketCaptured;
        foreach (var analyzer in _analyzers)
        {
            analyzer.DeviceObserved += OnDeviceObserved;
        }

        foreach (var knownDevice in _knownDevices.Where(device => device.IsCritical || device.Ssh?.Enabled == true))
        {
            CriticalDevices.Add(new CriticalDeviceViewModel(knownDevice));
        }

        CriticalDevicesStatusText = CriticalDevices.Count == 0
            ? "No critical devices configured. Create Data/known-devices.local.json from known-devices.example.json."
            : $"Loaded {CriticalDevices.Count} critical device(s).";

        CaptureFilter = DiscoveryFilter;
        SelectedBpfFilterPreset = BpfFilterPresets.First();
        RefreshNetworkSummary();
        StatusText = _vendorLookup.Count > 0
            ? $"Loaded {_vendorLookup.Count} OUI vendor prefix(es)."
            : "Ready. Add more vendor prefixes to Data/oui.csv for richer vendor names.";
        RefreshInterfaces();
        _ = RefreshCriticalDevicesAsync();
    }

    public TopologyTabViewModel TopologyTab { get; }
    public TrafficTabViewModel TrafficTab { get; }
    public DnsTabViewModel DnsTab { get; }

    public ObservableCollection<CaptureDeviceInfo> Interfaces { get; } = [];

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    public ObservableCollection<CriticalDeviceViewModel> CriticalDevices { get; } = [];

    public ObservableCollection<CaptureRunHistoryItemViewModel> RunHistory { get; } = [];

    public ObservableCollection<BpfFilterPresetViewModel> BpfFilterPresets { get; } =
    [
        new("Discovery", DiscoveryFilter, "ARP, DNS, mDNS, DHCP, IP"),
        new("ARP only", "arp", "Local MAC/IP mapping"),
        new("DNS + mDNS", "udp port 53 or udp port 5353", "Names and service hints"),
        new("DHCP", "udp port 67 or udp port 68", "Lease hostnames and vendor class"),
        new("Web", "tcp port 80 or tcp port 443", "HTTP and HTTPS traffic"),
        new("All IP", "ip", "IPv4/IPv6 traffic only"),
        new("All traffic", string.Empty, "No BPF filter")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotCapturing))]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshInterfacesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearResultsCommand))]
    private bool _isCapturing;

    public bool IsNotCapturing => !IsCapturing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    private CaptureDeviceInfo? _selectedInterface;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedPortsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedSshCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedSshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSelectedRouteCommand))]
    private DeviceRowViewModel? _selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCriticalSshCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCriticalSshCommand))]
    private CriticalDeviceViewModel? _selectedCriticalDevice;

    [ObservableProperty]
    private BpfFilterPresetViewModel? _selectedBpfFilterPreset;

    [ObservableProperty]
    private string _captureFilter = string.Empty;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private string _networkSummary = "Network profile not loaded.";

    [ObservableProperty]
    private string _criticalDevicesStatusText = string.Empty;

    [ObservableProperty]
    private long _packetCount;

    partial void OnSelectedBpfFilterPresetChanged(BpfFilterPresetViewModel? value)
    {
        if (value is null || IsCapturing)
        {
            return;
        }

        CaptureFilter = value.Filter;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshInterfaces))]
    private void RefreshInterfaces()
    {
        Interfaces.Clear();

        try
        {
            foreach (var device in _captureProvider.GetDevices())
            {
                Interfaces.Add(device);
            }

            SelectedInterface ??= Interfaces.FirstOrDefault();
            RefreshNetworkSummary();
            StatusText = Interfaces.Count == 0
                ? "No capture interfaces were found. Check Npcap/libpcap installation and permissions."
                : $"Found {Interfaces.Count} capture interface(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not enumerate capture interfaces: {ex.Message}";
        }
    }

    private bool CanRefreshInterfaces() => !IsCapturing;

    [RelayCommand(CanExecute = nameof(CanStartCapture))]
    private void StartCapture()
    {
        if (SelectedInterface is null)
        {
            StatusText = "Select a capture interface first.";
            return;
        }

        try
        {
            if (HasCurrentRunData())
            {
                if (!_currentRunArchived)
                {
                    ArchiveCurrentRun();
                }

                ClearCurrentResults();
            }

            PacketCount = 0;
            _currentRunArchived = false;
            _captureProvider.Start(SelectedInterface.Name, CaptureFilter);
            IsCapturing = true;
            StatusText = $"Capturing on {SelectedInterface.Description}.";
        }
        catch (Exception ex)
        {
            IsCapturing = false;
            StatusText = $"Capture failed: {ex.Message}";
        }
    }

    private bool CanStartCapture() => !IsCapturing && SelectedInterface is not null;

    [RelayCommand(CanExecute = nameof(CanStopCapture))]
    private void StopCapture()
    {
        try
        {
            _captureProvider.Stop();
            var archived = ArchiveCurrentRun();
            StatusText = archived ? $"Capture stopped and saved to history as run {_runNumber}." : "Capture stopped.";
        }
        catch (Exception ex)
        {
            StatusText = $"Stop failed: {ex.Message}";
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private bool CanStopCapture() => IsCapturing;

    [RelayCommand(CanExecute = nameof(CanClearResults))]
    private void ClearResults()
    {
        var archived = !_currentRunArchived && ArchiveCurrentRun();
        ClearCurrentResults();
        StatusText = archived
            ? $"Results cleared and saved to history as run {_runNumber}."
            : "Results cleared.";
    }

    private bool CanClearResults() => !IsCapturing;

    private bool HasCurrentRunData() => Devices.Count > 0 || PacketCount > 0;

    private bool ArchiveCurrentRun()
    {
        if (!HasCurrentRunData())
        {
            return false;
        }

        if (_currentRunArchived)
        {
            return false;
        }

        RunHistory.Insert(0, new CaptureRunHistoryItemViewModel(
            ++_runNumber,
            DateTime.UtcNow,
            PacketCount,
            Devices.Count,
            string.IsNullOrWhiteSpace(CaptureFilter) ? "All traffic" : CaptureFilter,
            Devices.ToArray()));
        _currentRunArchived = true;
        return true;
    }

    private void ClearCurrentResults()
    {
        Devices.Clear();
        SelectedDevice = null;
        PacketCount = 0;
        lock (_reverseDnsAttemptsLock)
        {
            _reverseDnsAttempts.Clear();
        }

        lock (_routeRefreshLock)
        {
            _pendingRouteRefreshes.Clear();
        }

        _clearDeviceStore();
        _currentRunArchived = false;
    }

    [RelayCommand]
    private async Task RefreshCriticalDevicesAsync()
    {
        if (CriticalDevices.Count == 0)
        {
            CriticalDevicesStatusText = "No critical devices configured. Create Data/known-devices.local.json from known-devices.example.json.";
            return;
        }

        CriticalDevicesStatusText = "Checking critical devices...";
        var onlineCount = 0;

        foreach (var criticalDevice in CriticalDevices)
        {
            try
            {
                if (await RefreshCriticalDeviceAsync(criticalDevice))
                {
                    onlineCount++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Critical device refresh failed for {criticalDevice.DisplayName}: {ex}");
                criticalDevice.Update("Check failed", criticalDevice.CurrentIp, ex.Message);
            }
        }

        CriticalDevicesStatusText = $"Checked {CriticalDevices.Count} critical device(s); {onlineCount} online.";
    }

    [RelayCommand(CanExecute = nameof(CanScanSelectedPorts))]
    private async Task ScanSelectedPortsAsync()
    {
        var selectedDevice = SelectedDevice;
        if (selectedDevice is null)
        {
            return;
        }

        var parsedAddress = GetFirstIpAddress(selectedDevice.Device);
        if (parsedAddress is null)
        {
            StatusText = "Selected device has no valid IP address to scan.";
            return;
        }

        StatusText = $"Scanning common ports on {parsedAddress}...";
        var results = await _portScanner.ScanCommonPortsAsync(
            parsedAddress,
            TimeSpan.FromMilliseconds(750),
            _shutdownCancellation.Token);

        lock (selectedDevice.Device)
        {
            foreach (var result in results)
            {
                selectedDevice.Device.OpenPorts.Add(result.Port);
                selectedDevice.Device.PortServices[result.Port] = result.ServiceName;
            }

            selectedDevice.Device.SeenVia.Add("TCP scan");
            selectedDevice.Device.LastSeen = DateTime.UtcNow;
        }

        selectedDevice.Update(selectedDevice.Device);
        RefreshSelectedDeviceCommands();
        await RefreshRouteForRowAsync(selectedDevice);
        StatusText = results.Count == 0
            ? $"No common ports responded on {parsedAddress}."
            : $"Found {results.Count} open common port(s) on {parsedAddress}.";
    }

    private bool CanScanSelectedPorts() => SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshSelectedRoute))]
    private async Task RefreshSelectedRouteAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        await RefreshRouteForRowAsync(SelectedDevice);
    }

    private bool CanRefreshSelectedRoute() => SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanCopySelectedSshCommand))]
    private void CopySelectedSshCommand()
    {
        CopyCommand(SelectedDevice?.SshCommand);
    }

    private bool CanCopySelectedSshCommand() => !string.IsNullOrWhiteSpace(SelectedDevice?.SshCommand);

    [RelayCommand(CanExecute = nameof(CanOpenSelectedSsh))]
    private async Task OpenSelectedSshAsync()
    {
        await OpenCommandAsync(SelectedDevice?.SshCommand);
    }

    private bool CanOpenSelectedSsh() => !string.IsNullOrWhiteSpace(SelectedDevice?.SshCommand);

    [RelayCommand(CanExecute = nameof(CanCopyCriticalSshCommand))]
    private void CopyCriticalSshCommand()
    {
        CopyCommand(SelectedCriticalDevice?.SshCommand);
    }

    private bool CanCopyCriticalSshCommand() => !string.IsNullOrWhiteSpace(SelectedCriticalDevice?.SshCommand);

    [RelayCommand(CanExecute = nameof(CanOpenCriticalSsh))]
    private async Task OpenCriticalSshAsync()
    {
        await OpenCommandAsync(SelectedCriticalDevice?.SshCommand);
    }

    private bool CanOpenCriticalSsh() => !string.IsNullOrWhiteSpace(SelectedCriticalDevice?.SshCommand);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _shutdownCancellation.Cancel();
        _captureProvider.PacketCaptured -= OnPacketCaptured;
        foreach (var analyzer in _analyzers)
        {
            analyzer.DeviceObserved -= OnDeviceObserved;
        }

        _captureProvider.Dispose();
        _shutdownCancellation.Dispose();
        _routeDiagnosticsGate.Dispose();
        TrafficTab.Dispose();
        _disposed = true;
    }

    private void OnPacketCaptured(object? sender, PacketCapturedEventArgs e)
    {
        foreach (var analyzer in _analyzers)
        {
            try
            {
                analyzer.Analyze(e.ParsedPacket);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Analyzer {analyzer.GetType().Name} failed: {ex}");
            }
        }

        try { _trafficFlowAnalyzer.Analyze(e.ParsedPacket); } catch { }
        try { _lldpAnalyzer.Analyze(e.ParsedPacket); } catch { }

        _dispatchToUi(() => PacketCount++);
    }

    private void OnDeviceObserved(object? sender, DeviceObservedEventArgs e)
    {
        EnrichDevice(e.Device);

        _dispatchToUi(() =>
        {
            var existing = Devices.FirstOrDefault(device => device.MacAddress == e.Device.MacAddress);
            if (existing is null)
            {
                var row = new DeviceRowViewModel(e.Device);
                Devices.Add(row);
                QueueRouteRefresh(row);
                return;
            }

            existing.Update(e.Device);
            if (ReferenceEquals(existing, SelectedDevice))
            {
                RefreshSelectedDeviceCommands();
            }

            QueueRouteRefresh(existing);
        });
    }

    private void EnrichDevice(Device device)
    {
        lock (device)
        {
            device.Vendor ??= _vendorLookup.LookupVendor(device.MacAddress);
            ApplyKnownDeviceIdentity(device);
        }

        _ = TryReverseDnsAsync(device);
    }

    private void ApplyKnownDeviceIdentity(Device device)
    {
        var ips = device.IpAddresses.ToArray();
        var knownDevice = _knownDevices.FirstOrDefault(candidate =>
            candidate.KnownIps.Any(ip => ips.Contains(ip, StringComparer.OrdinalIgnoreCase)));

        if (knownDevice is null)
        {
            return;
        }

        device.Hostname ??= knownDevice.DisplayName;
        device.ObservedNames.Add(knownDevice.DisplayName);
        device.SeenVia.Add("Known device");
    }

    private async Task TryReverseDnsAsync(Device device)
    {
        string[] addresses;
        lock (device)
        {
            if (!string.IsNullOrWhiteSpace(device.Hostname))
            {
                return;
            }

            addresses = device.IpAddresses.ToArray();
        }

        foreach (var address in addresses)
        {
            lock (_reverseDnsAttemptsLock)
            {
                if (!_reverseDnsAttempts.Add(address))
                {
                    continue;
                }
            }

            var hostname = await _hostnameResolver.TryReverseDnsAsync(address, TimeSpan.FromSeconds(1));
            if (string.IsNullOrWhiteSpace(hostname))
            {
                continue;
            }

            lock (device)
            {
                device.Hostname ??= hostname;
                device.ObservedNames.Add(hostname);
                device.SeenVia.Add("Reverse DNS");
                device.LastSeen = DateTime.UtcNow;
            }

            _dispatchToUi(() =>
            {
                var existing = Devices.FirstOrDefault(row => row.MacAddress == device.MacAddress);
                existing?.Update(device);
            });
            return;
        }
    }

    private async Task<bool> RefreshCriticalDeviceAsync(CriticalDeviceViewModel criticalDevice)
    {
        var candidates = criticalDevice.Definition.KnownIps
            .Select(ip => IPAddress.TryParse(ip, out var parsed) ? parsed : null)
            .Where(ip => ip is not null)
            .Cast<IPAddress>()
            .ToArray();

        foreach (var candidate in candidates)
        {
            var route = await _routeDiagnostics.GetRouteToAsync(candidate, _shutdownCancellation.Token);
            var port = criticalDevice.Definition.Ssh?.Enabled == true
                ? await _routeDiagnostics.TestPortAsync(candidate, criticalDevice.Definition.Ssh.Port, "SSH", _shutdownCancellation.Token)
                : new PortReachability(candidate, 0, false, string.Empty);

            if (port.IsOpen)
            {
                criticalDevice.Update("Online", candidate.ToString(), route.RouteSummary);
                return true;
            }
        }

        var fallback = candidates.FirstOrDefault();
        if (fallback is not null)
        {
            var route = await _routeDiagnostics.GetRouteToAsync(fallback, _shutdownCancellation.Token);
            criticalDevice.Update("Not reachable", fallback.ToString(), route.RouteSummary);
        }

        return false;
    }

    private void QueueRouteRefresh(DeviceRowViewModel row)
    {
        var key = row.MacAddress;
        lock (_routeRefreshLock)
        {
            if (!_pendingRouteRefreshes.Add(key))
            {
                return;
            }
        }

        _ = RefreshRouteForRowQueuedAsync(row, key);
    }

    private async Task RefreshRouteForRowQueuedAsync(DeviceRowViewModel row, string key)
    {
        try
        {
            await Task.Delay(500, _shutdownCancellation.Token);
            await _routeDiagnosticsGate.WaitAsync(_shutdownCancellation.Token);
            try
            {
                await RefreshRouteForRowAsync(row);
            }
            finally
            {
                _routeDiagnosticsGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or clear cancelled this queued refresh.
        }
        finally
        {
            lock (_routeRefreshLock)
            {
                _pendingRouteRefreshes.Remove(key);
            }
        }
    }

    private async Task RefreshRouteForRowAsync(DeviceRowViewModel row)
    {
        var address = GetFirstIpAddress(row.Device);
        if (address is null)
        {
            return;
        }

        RouteDecision? route = null;
        try
        {
            route = await _routeDiagnostics.GetRouteToAsync(address, _shutdownCancellation.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Route lookup failed for {address}: {ex}");
        }

        bool hasOpenTcpPort;
        lock (row.Device)
        {
            hasOpenTcpPort = row.Device.OpenPorts.Count > 0;
        }

        var classification = _reachabilityClassifier.Classify(address, hasOpenTcpPort, route);
        _dispatchToUi(() =>
        {
            row.ApplyNetworkClassification(classification);
            if (ReferenceEquals(row, SelectedDevice))
            {
                RefreshSelectedDeviceCommands();
            }
        });
    }

    private void RefreshSelectedDeviceCommands()
    {
        CopySelectedSshCommandCommand.NotifyCanExecuteChanged();
        OpenSelectedSshCommand.NotifyCanExecuteChanged();
    }

    private void RefreshNetworkSummary()
    {
        try
        {
            var networkProfile = _reachabilityClassifier.GetCurrentProfile();
            NetworkSummary = networkProfile.Interfaces.Count == 0
                ? "No active IPv4 interface detected."
                : string.Join(" | ", networkProfile.Interfaces.Select(item => $"{item.Name}: {item.Address} on {item.Network} via {item.GatewayAddress}"));
        }
        catch
        {
            NetworkSummary = "Network profile unavailable.";
        }
    }

    private static IPAddress? GetFirstIpAddress(Device device)
    {
        lock (device)
        {
            return device.IpAddresses
                .Select(ip => IPAddress.TryParse(ip, out var parsed) ? parsed : null)
                .FirstOrDefault(ip => ip is not null);
        }
    }

    private void CopyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        Clipboard.SetText(command);
        StatusText = $"Copied: {command}";
    }

    private async Task OpenCommandAsync(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var launched = await _terminalLauncher.LaunchSshAsync(command, _shutdownCancellation.Token);
        StatusText = launched
            ? $"Opened: {command}"
            : $"Could not launch terminal. Run manually: {command}";
    }
}
