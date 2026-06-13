using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Configuration;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Topology;

namespace LanInspector.UI.ViewModels;

public sealed partial class TopologyNodeViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Type { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Evidence { get; init; } = "";
}

public sealed partial class TopologyEdgeViewModel : ObservableObject
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string LinkType { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed partial class TopologyTabViewModel : ObservableObject
{
    private readonly ILocalNetworkProfileProvider _profileProvider;
    private readonly ITailscaleService _tailscale;
    private readonly KnownDevicesConfiguration _knownDevices;

    [ObservableProperty]
    private string _statusText = "No snapshot loaded. Click Refresh.";

    [ObservableProperty]
    private string _mermaidOutput = "";

    public ObservableCollection<TopologyNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<TopologyEdgeViewModel> Edges { get; } = [];

    public TopologyTabViewModel(
        ILocalNetworkProfileProvider profileProvider,
        ITailscaleService tailscale,
        KnownDevicesConfiguration knownDevices)
    {
        _profileProvider = profileProvider;
        _tailscale = tailscale;
        _knownDevices = knownDevices;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusText = "Building topology snapshot...";
        Nodes.Clear();
        Edges.Clear();
        MermaidOutput = "";

        try
        {
            var profile = _profileProvider.GetCurrentProfile();
            var ts = await _tailscale.GetStatusAsync();

            var builder = new TopologyBuilder()
                .AddLocalProfile(profile)
                .AddKnownDevices(_knownDevices.KnownDevices, [])
                .AddTailscaleStatus(ts);

            var snapshot = builder.Build();

            foreach (var node in snapshot.Nodes)
            {
                Nodes.Add(new TopologyNodeViewModel
                {
                    Id = node.Id,
                    DisplayName = node.DisplayName,
                    Type = node.Type.ToString(),
                    Confidence = node.Confidence.ToString(),
                    Ip = node.PrimaryIp?.ToString() ?? "",
                    Evidence = string.Join("; ", node.Evidence)
                });
            }

            foreach (var edge in snapshot.Edges)
            {
                var fromName = snapshot.FindNode(edge.FromId)?.DisplayName ?? edge.FromId;
                var toName = snapshot.FindNode(edge.ToId)?.DisplayName ?? edge.ToId;
                Edges.Add(new TopologyEdgeViewModel
                {
                    From = fromName,
                    To = toName,
                    LinkType = edge.LinkType.ToString(),
                    Confidence = edge.Confidence.ToString(),
                    Label = edge.Label ?? ""
                });
            }

            MermaidOutput = snapshot.ToMermaid();
            StatusText = $"Snapshot at {snapshot.CapturedAt:HH:mm:ss} — {snapshot.Nodes.Count} nodes, {snapshot.Edges.Count} edges";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyMermaid()
    {
        if (!string.IsNullOrWhiteSpace(MermaidOutput))
            Clipboard.SetText(MermaidOutput);
    }
}
