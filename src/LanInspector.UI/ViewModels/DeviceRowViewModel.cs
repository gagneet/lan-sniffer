using CommunityToolkit.Mvvm.ComponentModel;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Model;

namespace LanInspector.UI.ViewModels;

public sealed partial class DeviceRowViewModel : ObservableObject
{
    public DeviceRowViewModel(Device device)
    {
        Device = device;
        MacAddress = device.MacAddress;
        Update(device);
    }

    public Device Device { get; }

    public string MacAddress { get; }

    [ObservableProperty]
    private string _ipAddresses = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _hostname = string.Empty;

    [ObservableProperty]
    private string _vendor = string.Empty;

    [ObservableProperty]
    private string _dhcpVendorClass = string.Empty;

    [ObservableProperty]
    private string _openPorts = string.Empty;

    [ObservableProperty]
    private string _seenVia = string.Empty;

    [ObservableProperty]
    private string _segment = string.Empty;

    [ObservableProperty]
    private string _reachability = string.Empty;

    [ObservableProperty]
    private string _gateway = string.Empty;

    [ObservableProperty]
    private string _routeSummary = string.Empty;

    [ObservableProperty]
    private string _sshCommand = string.Empty;

    [ObservableProperty]
    private string _firstSeenLocal = string.Empty;

    [ObservableProperty]
    private string _lastSeenLocal = string.Empty;

    public bool HasSsh => !string.IsNullOrWhiteSpace(SshCommand);

    public void Update(Device device)
    {
        string[] ipAddresses;
        string[] observedNames;
        string[] seenVia;
        string[] openPorts;
        int[] openPortNumbers;
        string hostname;
        string vendor;
        string dhcpVendorClass;
        DateTime firstSeen;
        DateTime lastSeen;

        lock (device)
        {
            ipAddresses = device.IpAddresses.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            observedNames = device.ObservedNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            seenVia = device.SeenVia.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            openPortNumbers = device.OpenPorts.OrderBy(port => port).ToArray();
            openPorts = openPortNumbers
                .Select(port => device.PortServices.TryGetValue(port, out var service) ? $"{port} {service}" : port.ToString())
                .ToArray();
            hostname = device.Hostname ?? string.Empty;
            vendor = device.Vendor ?? string.Empty;
            dhcpVendorClass = device.DhcpVendorClass ?? string.Empty;
            firstSeen = device.FirstSeen;
            lastSeen = device.LastSeen;
            Segment = device.Segment ?? string.Empty;
            Reachability = device.Reachability ?? string.Empty;
            Gateway = device.Gateway ?? string.Empty;
            RouteSummary = device.RouteSummary ?? string.Empty;
        }

        IpAddresses = string.Join(", ", ipAddresses);
        Hostname = hostname;
        DisplayName = !string.IsNullOrWhiteSpace(hostname)
            ? hostname
            : observedNames.FirstOrDefault() ?? ipAddresses.FirstOrDefault() ?? MacAddress;
        Vendor = vendor;
        DhcpVendorClass = dhcpVendorClass;
        OpenPorts = string.Join(", ", openPorts);
        SeenVia = string.Join(", ", seenVia);
        SshCommand = BuildSshCommand(ipAddresses.FirstOrDefault(), openPortNumbers);
        FirstSeenLocal = firstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        LastSeenLocal = lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    public void ApplyNetworkClassification(DeviceNetworkClassification classification)
    {
        lock (Device)
        {
            Device.Segment = classification.Segment;
            Device.Reachability = classification.Reachability.ToString();
            Device.Gateway = classification.Gateway;
            Device.RouteSummary = classification.RouteSummary;
        }

        Segment = classification.Segment;
        Reachability = classification.Reachability.ToString();
        Gateway = classification.Gateway;
        RouteSummary = classification.RouteSummary;
    }

    private static string BuildSshCommand(string? ipAddress, IReadOnlyCollection<int> openPorts)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !openPorts.Contains(22))
        {
            return string.Empty;
        }

        return $"ssh {ipAddress}";
    }
}
