using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Flipper;
using LanInspector.Core.Flipper.Ble;
using LanInspector.Core.Flipper.SubGhz;

namespace LanInspector.UI.ViewModels;

public sealed class FlipperPortRowViewModel
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Likely { get; init; } = "";
}

public sealed class SubGhzSignalRowViewModel
{
    public string Frequency { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Rssi { get; init; } = "";
    public string Data { get; init; } = "";
}

public sealed class BleChannelRowViewModel
{
    public string AdvertisingChannel { get; init; } = "";
    public string Frequency { get; init; } = "";
    public string Samples { get; init; } = "";
    public string Median { get; init; } = "";
    public string Peak { get; init; } = "";
    public string Overlap { get; init; } = "";
}

/// <summary>
/// Drives the Flipper Zero over its USB serial CLI. The radio work runs off the UI
/// thread and every command is guarded, because the device can be unplugged mid-scan
/// and the port can be held by qFlipper or the Flipper mobile app.
/// </summary>
public sealed partial class FlipperTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IFlipperConnectionService _flipper;

    public FlipperTabViewModel(IFlipperConnectionService? flipper = null)
    {
        _flipper = flipper ?? new FlipperSerialService();
        RefreshPorts();
    }

    [ObservableProperty] private string _statusText = "Not connected. Click Refresh ports, then Connect.";
    [ObservableProperty] private string _deviceInfoText = "";
    [ObservableProperty] private FlipperPortRowViewModel? _selectedPort;
    [ObservableProperty] private int _dwellSeconds = 5;
    [ObservableProperty] private string _bleNotice = "";

    // NotifyCanExecuteChangedFor is what actually disables the buttons. A radio scan holds
    // the serial port for the whole dwell, and a second command queued behind it would sit
    // on FlipperSerialService's semaphore with the UI showing nothing happening at all.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSubGhzCommand))]
    [NotifyCanExecuteChangedFor(nameof(SurveyBleCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshPortsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSubGhzCommand))]
    [NotifyCanExecuteChangedFor(nameof(SurveyBleCommand))]
    private bool _isBusy;

    private bool CanRefresh() => !IsBusy;
    private bool CanConnect() => !IsBusy && !IsConnected;
    private bool CanUseRadio() => !IsBusy && IsConnected;

    public ObservableCollection<FlipperPortRowViewModel> Ports { get; } = [];
    public ObservableCollection<SubGhzSignalRowViewModel> SubGhzSignals { get; } = [];
    public ObservableCollection<BleChannelRowViewModel> BleChannels { get; } = [];

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void RefreshPorts()
    {
        Ports.Clear();
        try
        {
            foreach (var port in _flipper.DetectPorts())
            {
                Ports.Add(new FlipperPortRowViewModel
                {
                    Name = port.Name,
                    Description = port.Description,
                    Likely = port.LooksLikeFlipper ? "yes" : ""
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not list serial ports: {ex.Message}";
            return;
        }

        // Ports.Clear() above dropped the previously selected row and `??=` kept the stale
        // object: the ComboBox then showed blank while Connect still used the old port name.
        // Re-select by NAME so a refresh keeps the user's choice when that port is still there.
        var previous = SelectedPort?.Name;
        SelectedPort = Ports.FirstOrDefault(p => p.Name == previous)
                    ?? Ports.FirstOrDefault(p => p.Likely == "yes")
                    ?? Ports.FirstOrDefault();
        StatusText = Ports.Count == 0
            ? "No serial ports found. Connect the Flipper over USB."
            : $"{Ports.Count} serial port(s). Select one and click Connect, or leave it to auto-detect.";
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusText = "Connecting...";
        try
        {
            var connected = await _flipper.ConnectAsync(SelectedPort?.Name);
            IsConnected = connected;
            if (!connected)
            {
                StatusText = "Connection failed. Close qFlipper or the Flipper mobile app, then retry.";
                return;
            }

            var info = _flipper.DeviceInfo;
            DeviceInfoText = info is null
                ? ""
                : $"Port {info.PortName}    firmware {Blank(info.FirmwareVersion)}    "
                + $"hardware {Blank(info.HardwareVersion)}    target {Blank(info.Target)}    built {Blank(info.BuildDate)}";
            StatusText = $"Connected on {info?.PortName ?? "(unknown port)"}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection error: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseRadio))]
    private async Task DisconnectAsync()
    {
        try
        {
            await _flipper.DisconnectAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Disconnect error: {ex.Message}";
        }
        IsConnected = false;
        DeviceInfoText = "";
        StatusText = "Disconnected.";
    }

    [RelayCommand(CanExecute = nameof(CanUseRadio))]
    private async Task ScanSubGhzAsync()
    {
        if (!Require()) return;

        IsBusy = true;
        SubGhzSignals.Clear();
        var dwell = TimeSpan.FromSeconds(Math.Clamp(DwellSeconds, 1, 60));
        StatusText = $"Sub-GHz scan: 315, 433.92, 868.35 and 915 MHz at {dwell.TotalSeconds:0}s each...";

        try
        {
            var result = await new FlipperSubGhzService(_flipper).ScanAsync(null, dwell);

            foreach (var signal in result.Signals)
            {
                SubGhzSignals.Add(new SubGhzSignalRowViewModel
                {
                    Frequency = signal.FrequencyLabel,
                    Protocol = signal.ProtocolLabel,
                    Rssi = $"{signal.RssiDbm:F1} dBm",
                    Data = signal.DecodedData ?? ""
                });
            }

            StatusText = !result.Succeeded
                ? $"Sub-GHz scan error: {result.Error}"
                : result.Signals.Count == 0
                    ? $"No sub-GHz signals in {(int)result.Duration.TotalSeconds}s. Trigger a remote nearby, or raise the dwell."
                    : $"{result.Signals.Count} sub-GHz signal(s) in {(int)result.Duration.TotalSeconds}s.";
        }
        catch (Exception ex)
        {
            StatusText = $"Sub-GHz scan error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseRadio))]
    private async Task SurveyBleAsync()
    {
        if (!Require()) return;

        IsBusy = true;
        BleChannels.Clear();
        BleNotice = "";
        var dwell = TimeSpan.FromSeconds(Math.Clamp(DwellSeconds, 1, 60));
        StatusText = $"Sampling BLE advertising channels 37, 38 and 39 at {dwell.TotalSeconds:0}s each...";

        try
        {
            var result = await new FlipperBleService(_flipper).SurveyAsync(null, dwell);

            foreach (var channel in result.Channels)
            {
                BleChannels.Add(new BleChannelRowViewModel
                {
                    AdvertisingChannel = channel.AdvertisingChannel?.ToString() ?? "-",
                    Frequency = $"{channel.FrequencyMhz:F0} MHz",
                    Samples = channel.Samples.Count.ToString(),
                    Median = double.IsNaN(channel.MedianDbm) ? "n/a" : $"{channel.MedianDbm:F1} dBm",
                    Peak = double.IsNaN(channel.PeakDbm) ? "n/a" : $"{channel.PeakDbm:F1} dBm",
                    Overlap = channel.WifiOverlapLabel
                });
            }

            if (result.DebugModeRequired)
            {
                BleNotice = "The Flipper refused the RF test command. Turn Debug mode on: "
                          + "Settings → System → Debug = ON, then survey again.";
            }

            StatusText = !result.Succeeded
                ? $"BLE survey error: {result.Error}"
                : $"Sampled {result.Channels.Count} channel(s) in {(int)result.Duration.TotalSeconds}s.";
        }
        catch (Exception ex)
        {
            StatusText = $"BLE survey error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A second line of defence behind <see cref="CanUseRadio"/>: a command can still be
    /// invoked directly (a key binding, a test), and every radio call needs a live port.
    /// </summary>
    private bool Require()
    {
        if (IsConnected) return true;
        StatusText = "Connect to the Flipper first.";
        return false;
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;

    public async ValueTask DisposeAsync() => await _flipper.DisposeAsync();
}
