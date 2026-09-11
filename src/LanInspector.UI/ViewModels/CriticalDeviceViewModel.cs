using CommunityToolkit.Mvvm.ComponentModel;
using LanInspector.Core.Configuration;
using LanInspector.Core.Locator;

namespace LanInspector.UI.ViewModels;

public sealed partial class CriticalDeviceViewModel : ObservableObject
{
    public CriticalDeviceViewModel(KnownDeviceDefinition definition)
    {
        Definition = definition;
        DisplayName = definition.DisplayName;
        DeviceType = definition.DeviceType;
        Tags = string.Join(", ", definition.Tags);
        Status = "Unknown";
        CurrentIp = definition.KnownIps.FirstOrDefault() ?? string.Empty;
        SshCommand = BuildSshCommand(definition, CurrentIp);
    }

    public KnownDeviceDefinition Definition { get; }

    public string DisplayName { get; }

    public string DeviceType { get; }

    public string Tags { get; }

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _currentIp;

    [ObservableProperty]
    private string _routeSummary = string.Empty;

    [ObservableProperty]
    private string _lastChecked = "Not checked";

    [ObservableProperty]
    private string _sshCommand = string.Empty;

    /// <summary>How the current address was determined, e.g. "ARP cache, matched by MAC".</summary>
    [ObservableProperty]
    private string _foundVia = string.Empty;

    /// <summary>The stable Tailscale address, shown because it survives DHCP changes.</summary>
    [ObservableProperty]
    private string _tailscaleAddress = string.Empty;

    /// <summary>Non-empty only when the device's address changed since the last check.</summary>
    [ObservableProperty]
    private string _addressChange = string.Empty;

    public bool HasSsh => Definition.Ssh?.Enabled == true && !string.IsNullOrWhiteSpace(SshCommand);

    public void Update(string status, string currentIp, string routeSummary)
    {
        Status = status;
        CurrentIp = currentIp;
        RouteSummary = routeSummary;
        LastChecked = DateTime.Now.ToString("HH:mm:ss");
        SshCommand = BuildSshCommand(Definition, currentIp);
    }

    /// <summary>
    /// Applies a locator result. The SSH command is rebuilt against whichever address was actually
    /// found, so a copied command still works after the server's lease changed.
    /// </summary>
    public void ApplyLocation(DeviceLocation location, string status, string routeSummary)
    {
        var address = location.CurrentAddress?.ToString() ?? string.Empty;

        Status = status;
        CurrentIp = address;
        RouteSummary = routeSummary;
        LastChecked = DateTime.Now.ToString("HH:mm:ss");
        FoundVia = location.Source is null
            ? "not located"
            : $"{DeviceLocation.Describe(location.Source.Value)} ({location.Confidence})";
        TailscaleAddress = location.TailscaleAddress?.ToString() ?? string.Empty;
        AddressChange = location.HasMoved
            ? $"was {location.PreviousAddress}" + (location.AddressChangedAt is null ? "" : $" until {location.AddressChangedAt.Value.ToLocalTime():HH:mm:ss}")
            : string.Empty;

        // Fall back to the Tailscale name/address when no LAN address was found, so the action
        // buttons stay usable rather than going blank exactly when the device is hardest to reach.
        var sshHost = !string.IsNullOrWhiteSpace(address)
            ? address
            : location.TailscaleName ?? location.TailscaleAddress?.ToString() ?? string.Empty;
        SshCommand = BuildSshCommand(Definition, sshHost);
    }

    private static string BuildSshCommand(KnownDeviceDefinition definition, string host)
    {
        if (definition.Ssh?.Enabled != true || string.IsNullOrWhiteSpace(definition.Ssh.User) || string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var port = definition.Ssh.Port == 22 ? string.Empty : $" -p {definition.Ssh.Port}";
        return $"ssh{port} {definition.Ssh.User}@{host}";
    }
}
