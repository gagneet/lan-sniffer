using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Traffic;

namespace LanInspector.UI.ViewModels;

public sealed class TrafficFlowViewModel
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Protocol { get; init; } = "";
    public long Packets { get; init; }
    public string Bytes { get; init; } = "";
    public string LastSeen { get; init; } = "";
}

public sealed class TrafficChartBar
{
    public double Height { get; init; }
    public string Tooltip { get; init; } = "";
}

public sealed partial class TrafficTabViewModel : ObservableObject, IDisposable
{
    private readonly ITrafficFlowService _trafficService;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty]
    private string _packetsPerSecond = "0";

    [ObservableProperty]
    private string _bytesPerSecond = "0 B/s";

    [ObservableProperty]
    private string _totalPackets = "0";

    [ObservableProperty]
    private string _totalBytes = "0 B";

    [ObservableProperty]
    private string _statusText = "Waiting for capture...";

    public ObservableCollection<TrafficFlowViewModel> TopFlows { get; } = [];
    public ObservableCollection<TrafficChartBar> ChartBars { get; } = [];

    public TrafficTabViewModel(ITrafficFlowService trafficService)
    {
        _trafficService = trafficService;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        var summary = _trafficService.GetSummary(topFlowsCount: 15, buckets: 60);

        PacketsPerSecond = $"{summary.PacketsPerSecond:F1} pkt/s";
        BytesPerSecond = FormatBytes(summary.BytesPerSecond) + "/s";
        TotalPackets = $"{summary.TotalPackets:N0}";
        TotalBytes = FormatBytes(summary.TotalBytes);
        StatusText = summary.TotalPackets > 0 ? "Live" : "Waiting for capture...";

        TopFlows.Clear();
        foreach (var flow in summary.TopFlows)
        {
            TopFlows.Add(new TrafficFlowViewModel
            {
                Source = $"{flow.Key.SourceIp}:{flow.Key.SourcePort}",
                Destination = $"{flow.Key.DestIp}:{flow.Key.DestPort}",
                Protocol = flow.Key.Protocol,
                Packets = flow.Packets,
                Bytes = FormatBytes(flow.Bytes),
                LastSeen = flow.LastSeen.ToLocalTime().ToString("HH:mm:ss")
            });
        }

        ChartBars.Clear();
        var maxBytes = summary.TimeSeries.Count > 0 ? summary.TimeSeries.Max(b => (double)b.Bytes) : 1;
        if (maxBytes <= 0) maxBytes = 1;

        foreach (var bucket in summary.TimeSeries.TakeLast(60))
        {
            ChartBars.Add(new TrafficChartBar
            {
                Height = bucket.Bytes / maxBytes * 80,  // max chart height = 80px
                Tooltip = $"{FormatBytes(bucket.Bytes)}/s"
            });
        }
    }

    [RelayCommand]
    private void ResetTraffic()
    {
        _trafficService.Reset();
        Refresh();
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000:F2} GB";
        if (bytes >= 1_000_000) return $"{bytes / 1_000_000:F2} MB";
        if (bytes >= 1_000) return $"{bytes / 1_000:F1} KB";
        return $"{bytes:F0} B";
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
    }
}
