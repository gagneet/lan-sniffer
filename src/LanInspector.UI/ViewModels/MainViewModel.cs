using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Analysis;
using LanInspector.Core.Capture;
using LanInspector.Core.Identity;
using LanInspector.Core.Model;
using LanInspector.Core.Scanning;

namespace LanInspector.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureProvider _captureProvider;
    private readonly IReadOnlyList<IDeviceObservingAnalyzer> _analyzers;
    private readonly OuiVendorLookup _vendorLookup;
    private readonly HostnameResolver _hostnameResolver;
    private readonly PortScanner _portScanner;
    private readonly Action<Action> _dispatchToUi;
    private readonly HashSet<string> _reverseDnsAttempts = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public MainViewModel(
        ICaptureProvider captureProvider,
        IReadOnlyList<IDeviceObservingAnalyzer> analyzers,
        OuiVendorLookup vendorLookup,
        HostnameResolver hostnameResolver,
        PortScanner portScanner,
        Action<Action> dispatchToUi)
    {
        _captureProvider = captureProvider;
        _analyzers = analyzers;
        _vendorLookup = vendorLookup;
        _hostnameResolver = hostnameResolver;
        _portScanner = portScanner;
        _dispatchToUi = dispatchToUi;

        _captureProvider.PacketCaptured += OnPacketCaptured;
        foreach (var analyzer in _analyzers)
        {
            analyzer.DeviceObserved += OnDeviceObserved;
        }

        CaptureFilter = "ip or arp or udp port 53 or udp port 5353 or udp port 67 or udp port 68";
        StatusText = _vendorLookup.Count > 0
            ? $"Loaded {_vendorLookup.Count} OUI vendor prefix(es)."
            : "Ready. Add more vendor prefixes to Data/oui.csv for richer vendor names.";
        RefreshInterfaces();
    }

    public ObservableCollection<CaptureDeviceInfo> Interfaces { get; } = [];

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotCapturing))]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshInterfacesCommand))]
    private bool _isCapturing;

    public bool IsNotCapturing => !IsCapturing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    private CaptureDeviceInfo? _selectedInterface;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedPortsCommand))]
    private DeviceRowViewModel? _selectedDevice;

    [ObservableProperty]
    private string _captureFilter = string.Empty;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private long _packetCount;

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
            StatusText = Interfaces.Count == 0
                ? "No capture interfaces were found. Check Npcap/libpcap installation and permissions."
                : $"Found {Interfaces.Count} capture interface(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not enumerate capture interfaces: {ex.Message}";
        }
    }

    private bool CanRefreshInterfaces()
    {
        return !IsCapturing;
    }

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
            PacketCount = 0;
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

    private bool CanStartCapture()
    {
        return !IsCapturing && SelectedInterface is not null;
    }

    [RelayCommand(CanExecute = nameof(CanStopCapture))]
    private void StopCapture()
    {
        try
        {
            _captureProvider.Stop();
            StatusText = "Capture stopped.";
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

    private bool CanStopCapture()
    {
        return IsCapturing;
    }

    [RelayCommand(CanExecute = nameof(CanScanSelectedPorts))]
    private async Task ScanSelectedPortsAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        string? ipAddress;
        lock (SelectedDevice.Device)
        {
            ipAddress = SelectedDevice.Device.IpAddresses.FirstOrDefault(value => IPAddress.TryParse(value, out _));
        }

        if (!IPAddress.TryParse(ipAddress, out var parsedAddress))
        {
            StatusText = "Selected device has no valid IP address to scan.";
            return;
        }

        StatusText = $"Scanning common ports on {parsedAddress}...";
        var results = await _portScanner.ScanCommonPortsAsync(parsedAddress, TimeSpan.FromMilliseconds(750));

        lock (SelectedDevice.Device)
        {
            foreach (var result in results)
            {
                SelectedDevice.Device.OpenPorts.Add(result.Port);
                SelectedDevice.Device.PortServices[result.Port] = result.ServiceName;
            }

            SelectedDevice.Device.SeenVia.Add("TCP scan");
            SelectedDevice.Device.LastSeen = DateTime.UtcNow;
        }

        SelectedDevice.Update(SelectedDevice.Device);
        StatusText = results.Count == 0
            ? $"No common ports responded on {parsedAddress}."
            : $"Found {results.Count} open common port(s) on {parsedAddress}.";
    }

    private bool CanScanSelectedPorts()
    {
        return SelectedDevice is not null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _captureProvider.PacketCaptured -= OnPacketCaptured;
        foreach (var analyzer in _analyzers)
        {
            analyzer.DeviceObserved -= OnDeviceObserved;
        }

        _captureProvider.Dispose();
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
            catch
            {
                // Keep packet processing resilient; individual analyzer failures should not stop capture.
            }
        }

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
                Devices.Add(new DeviceRowViewModel(e.Device));
                return;
            }

            existing.Update(e.Device);
        });
    }

    private void EnrichDevice(Device device)
    {
        lock (device)
        {
            device.Vendor ??= _vendorLookup.LookupVendor(device.MacAddress);
        }

        _ = TryReverseDnsAsync(device);
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
            if (!_reverseDnsAttempts.Add(address))
            {
                continue;
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
}
