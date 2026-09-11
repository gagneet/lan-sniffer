using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Configuration;

namespace LanInspector.UI.ViewModels;

public sealed partial class SettingsTabViewModel : ObservableObject
{
    private readonly Action _onSaved;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _userConfigPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveDeviceCommand))]
    private KnownDeviceDraft? _selectedDevice;

    public ObservableCollection<KnownDeviceDraft> Devices { get; } = [];

    /// <summary>Where configuration is read from, least specific first — later files win.</summary>
    public ObservableCollection<string> SearchPaths { get; } = [];

    public SettingsTabViewModel(KnownDevicesConfiguration loaded, IEnumerable<string> searchPaths, Action onSaved)
    {
        _onSaved = onSaved;
        UserConfigPath = KnownDevicesConfiguration.GetUserConfigPath();

        foreach (var path in searchPaths)
        {
            SearchPaths.Add($"{(File.Exists(path) ? "[found]   " : "[absent]  ")}{path}");
        }

        foreach (var device in loaded.KnownDevices)
        {
            Devices.Add(KnownDeviceDraft.From(device));
        }

        StatusText = Devices.Count == 0
            ? "No devices configured yet. Add one, then Save."
            : $"{Devices.Count} device(s) loaded. Edits are saved to your user configuration, not the shipped file.";
    }

    [RelayCommand]
    private void AddDevice()
    {
        var device = new KnownDeviceDraft { DisplayName = "New device", DeviceType = "Device", IsCritical = true };
        Devices.Add(device);
        SelectedDevice = device;
        StatusText = "Added a row. Fill in a name and at least a MAC or an address, then Save.";
    }

    [RelayCommand(CanExecute = nameof(CanRemoveDevice))]
    private void RemoveDevice()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var name = SelectedDevice.DisplayName;
        Devices.Remove(SelectedDevice);
        SelectedDevice = null;
        StatusText = $"Removed '{name}'. Save to make it stick.";
    }

    private bool CanRemoveDevice() => SelectedDevice is not null;

    [RelayCommand]
    private void Save()
    {
        try
        {
            var configuration = new KnownDevicesConfiguration
            {
                KnownDevices = [.. Devices.Where(device => device.IsUsable).Select(device => device.ToDefinition())]
            };

            var skipped = Devices.Count - configuration.KnownDevices.Count;

            KnownDevicesConfiguration.Save(UserConfigPath, configuration);
            _onSaved();

            StatusText = $"Saved {configuration.KnownDevices.Count} device(s) to {UserConfigPath}." +
                         (skipped > 0 ? $" {skipped} row(s) with no name were skipped." : "") +
                         " Critical device checks have been re-run.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save to {UserConfigPath}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void WriteExampleConfig()
    {
        try
        {
            var directory = Path.GetDirectoryName(UserConfigPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var path = KnownDevicesConfiguration.WriteExampleTo(directory);
            StatusText = $"Example configuration is at {path}. It is never loaded — copy the parts you want.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not write the example: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(UserConfigPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the folder: {ex.Message}";
        }
    }
}
