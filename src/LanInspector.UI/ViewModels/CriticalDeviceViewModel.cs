using CommunityToolkit.Mvvm.ComponentModel;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
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

    /// <summary>The device's state: Online, Probable, Not reachable, Not found.</summary>
    [ObservableProperty]
    private string _status;

    /// <summary>Drives the status pill's colour; kept separate from the text so the two agree.</summary>
    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private string _currentIp;

    /// <summary>Confirmed / High / Medium / Low, shown under the address.</summary>
    [ObservableProperty]
    private string _confidence = string.Empty;

    [ObservableProperty]
    private bool _isConfirmed;

    [ObservableProperty]
    private string _routeSummary = string.Empty;

    [ObservableProperty]
    private string _lastChecked = "Not checked";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSsh))]
    private string _sshCommand = string.Empty;

    /// <summary>How the current address was determined, e.g. "ARP cache, matched by MAC".</summary>
    [ObservableProperty]
    private string _foundVia = string.Empty;

    [ObservableProperty]
    private string _tailscaleAddress = string.Empty;

    [ObservableProperty]
    private string _tailscaleName = string.Empty;

    /// <summary>
    /// Set when the LAN address is unreachable but Tailscale is not — the one case where the
    /// overlay address is the answer rather than a footnote.
    /// </summary>
    [ObservableProperty]
    private bool _tailscaleIsTheWayIn;

    /// <summary>Other addresses this device answered on, for a machine with more than one interface.</summary>
    [ObservableProperty]
    private string _alsoAt = string.Empty;

    /// <summary>Non-empty only when the device's address changed since the last check.</summary>
    [ObservableProperty]
    private string _addressChange = string.Empty;

    /// <summary>Two or three words naming why it is unreachable, sized for the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosis))]
    private string _cause = string.Empty;

    /// <summary>The full explanation, shown only when the row is expanded.</summary>
    [ObservableProperty]
    private string _diagnosis = string.Empty;

    [ObservableProperty]
    private string _remedy = string.Empty;

    /// <summary>Whether this row's explanation is showing. Only one row expands at a time.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public bool HasDiagnosis => !string.IsNullOrWhiteSpace(Cause);

    public bool HasSsh => Definition.Ssh?.Enabled == true && !string.IsNullOrWhiteSpace(SshCommand);

    /// <summary>
    /// The check itself threw, so nothing was learned about the device. The last known address is
    /// kept — it is still the best guess — but the previous diagnosis is replaced, since leaving
    /// "different subnet" under a failed check would state a cause nothing established this time.
    /// </summary>
    public void ApplyCheckFailure(string message)
    {
        Status = "Check failed";
        IsOnline = false;
        LastChecked = DateTime.Now.ToString("HH:mm:ss");

        Cause = "check failed";
        Diagnosis = string.IsNullOrWhiteSpace(message)
            ? "The check did not complete, so this device's state is unknown."
            : $"The check did not complete: {message}";
        Remedy = "Try again; if it keeps failing, the locator itself is not running correctly.";
    }

    /// <summary>
    /// Applies a locator result and its diagnosis. The SSH command is rebuilt against whichever
    /// address was actually found, so a copied command still works after the lease changed.
    /// </summary>
    public void ApplyLocation(
        DeviceLocation location,
        string status,
        bool isOnline,
        string routeSummary,
        ReachabilityDiagnosis diagnosis)
    {
        var address = location.CurrentAddress?.ToString() ?? string.Empty;

        Status = status;
        IsOnline = isOnline;
        CurrentIp = address;
        RouteSummary = routeSummary;
        LastChecked = DateTime.Now.ToString("HH:mm:ss");

        Confidence = location.Confidence == LocationConfidence.None ? string.Empty : location.Confidence.ToString();
        IsConfirmed = location.Confidence == LocationConfidence.Confirmed;
        FoundVia = location.Source is null ? "not located" : DeviceLocation.Describe(location.Source.Value);

        TailscaleAddress = location.TailscaleAddress?.ToString() ?? string.Empty;
        TailscaleName = location.TailscaleName ?? string.Empty;
        TailscaleIsTheWayIn = !isOnline && location.TailscaleAddress is not null;

        AlsoAt = location.IsMultiHomed ? string.Join(", ", location.AdditionalAddresses) : string.Empty;
        AddressChange = location.HasMoved
            ? $"was {location.PreviousAddress}" + (location.AddressChangedAt is null ? "" : $" until {location.AddressChangedAt.Value.ToLocalTime():HH:mm:ss}")
            : string.Empty;

        Cause = diagnosis.ShortCause;
        Diagnosis = diagnosis.Detail;
        Remedy = diagnosis.Remedy;

        // Nothing to expand once a device comes back; leaving it open would show a stale account.
        if (!diagnosis.HasDiagnosis)
        {
            IsExpanded = false;
        }

        // Prefer Tailscale whenever the LAN address is unconfirmed, not only when none was found.
        // An unverified address is usually one this machine has no route to, so handing out
        // "ssh user@192.168.0.148" gives a command that cannot work while a working one exists.
        var overlayHost = location.TailscaleName ?? location.TailscaleAddress?.ToString();
        var lanIsTrustworthy = !string.IsNullOrWhiteSpace(address)
            && location.Confidence is LocationConfidence.Confirmed or LocationConfidence.High;

        SshCommand = BuildSshCommand(Definition, lanIsTrustworthy ? address : overlayHost ?? address);
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
