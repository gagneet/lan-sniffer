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

    /// <summary>Other addresses this device answered on, for a machine with more than one interface.</summary>
    [ObservableProperty]
    private string _alsoAt = string.Empty;

    /// <summary>Why the device could not be reached, and what to do about it.</summary>
    [ObservableProperty]
    private string _diagnosis = string.Empty;

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
    public void ApplyLocation(DeviceLocation location, string status, string routeSummary, string diagnosis = "")
    {
        Diagnosis = diagnosis;

        var address = location.CurrentAddress?.ToString() ?? string.Empty;

        Status = status;
        CurrentIp = address;
        RouteSummary = routeSummary;
        LastChecked = DateTime.Now.ToString("HH:mm:ss");
        FoundVia = location.Source is null
            ? "not located"
            : $"{DeviceLocation.Describe(location.Source.Value)} ({location.Confidence})";
        TailscaleAddress = location.TailscaleAddress?.ToString() ?? string.Empty;
        AlsoAt = location.IsMultiHomed ? $"also at {string.Join(", ", location.AdditionalAddresses)}" : string.Empty;
        AddressChange = location.HasMoved
            ? $"was {location.PreviousAddress}" + (location.AddressChangedAt is null ? "" : $" until {location.AddressChangedAt.Value.ToLocalTime():HH:mm:ss}")
            : string.Empty;

        // Prefer Tailscale whenever the LAN address is unconfirmed, not only when none was found.
        // An unverified address is usually one this machine has no route to, so handing out
        // "ssh user@192.168.0.148" gives a command that cannot work while a working one exists.
        var overlayHost = location.TailscaleName ?? location.TailscaleAddress?.ToString();
        var lanIsTrustworthy = !string.IsNullOrWhiteSpace(address)
            && location.Confidence is LocationConfidence.Confirmed or LocationConfidence.High;

        var sshHost = lanIsTrustworthy
            ? address
            : overlayHost ?? address;
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
