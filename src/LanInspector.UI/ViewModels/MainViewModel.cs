using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Analysis;
using LanInspector.Core.Capture;

namespace LanInspector.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureProvider _captureProvider;
    private readonly ArpAnalyzer _arpAnalyzer;
    private readonly Action<Action> _dispatchToUi;
    private bool _disposed;

    public MainViewModel(
        ICaptureProvider captureProvider,
        ArpAnalyzer arpAnalyzer,
        Action<Action> dispatchToUi)
    {
        _captureProvider = captureProvider;
        _arpAnalyzer = arpAnalyzer;
        _dispatchToUi = dispatchToUi;

        _captureProvider.PacketCaptured += OnPacketCaptured;
        _arpAnalyzer.DeviceObserved += OnDeviceObserved;

        CaptureFilter = "ip or arp";
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _captureProvider.PacketCaptured -= OnPacketCaptured;
        _arpAnalyzer.DeviceObserved -= OnDeviceObserved;
        _captureProvider.Dispose();
        _disposed = true;
    }

    private void OnPacketCaptured(object? sender, PacketCapturedEventArgs e)
    {
        _arpAnalyzer.Analyze(e.ParsedPacket);
        _dispatchToUi(() => PacketCount++);
    }

    private void OnDeviceObserved(object? sender, DeviceObservedEventArgs e)
    {
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
}
