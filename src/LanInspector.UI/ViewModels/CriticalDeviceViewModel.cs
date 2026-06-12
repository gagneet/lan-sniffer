using CommunityToolkit.Mvvm.ComponentModel;
using LanInspector.Core.Configuration;

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

    public bool HasSsh => Definition.Ssh?.Enabled == true && !string.IsNullOrWhiteSpace(SshCommand);

    public void Update(string status, string currentIp, string routeSummary)
    {
        Status = status;
        CurrentIp = currentIp;
        RouteSummary = routeSummary;
        LastChecked = DateTime.Now.ToString("HH:mm:ss");
        SshCommand = BuildSshCommand(Definition, currentIp);
    }

    private static string BuildSshCommand(KnownDeviceDefinition definition, string ipAddress)
    {
        if (definition.Ssh?.Enabled != true || string.IsNullOrWhiteSpace(definition.Ssh.User) || string.IsNullOrWhiteSpace(ipAddress))
        {
            return string.Empty;
        }

        var port = definition.Ssh.Port == 22 ? string.Empty : $" -p {definition.Ssh.Port}";
        return $"ssh{port} {definition.Ssh.User}@{ipAddress}";
    }
}
