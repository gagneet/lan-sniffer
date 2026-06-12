using CommunityToolkit.Mvvm.ComponentModel;
using LanInspector.Core.Model;

namespace LanInspector.UI.ViewModels;

public sealed partial class DeviceRowViewModel : ObservableObject
{
    public DeviceRowViewModel(Device device)
    {
        MacAddress = device.MacAddress;
        Update(device);
    }

    public string MacAddress { get; }

    [ObservableProperty]
    private string _ipAddresses = string.Empty;

    [ObservableProperty]
    private string _hostname = string.Empty;

    [ObservableProperty]
    private string _vendor = string.Empty;

    [ObservableProperty]
    private string _firstSeenLocal = string.Empty;

    [ObservableProperty]
    private string _lastSeenLocal = string.Empty;

    public void Update(Device device)
    {
        string[] ipAddresses;
        string hostname;
        string vendor;
        DateTime firstSeen;
        DateTime lastSeen;

        lock (device)
        {
            ipAddresses = device.IpAddresses.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            hostname = device.Hostname ?? string.Empty;
            vendor = device.Vendor ?? string.Empty;
            firstSeen = device.FirstSeen;
            lastSeen = device.LastSeen;
        }

        IpAddresses = string.Join(", ", ipAddresses);
        Hostname = hostname;
        Vendor = vendor;
        FirstSeenLocal = firstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        LastSeenLocal = lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
