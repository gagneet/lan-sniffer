using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Identity;
using LanInspector.Core.Traffic;

namespace LanInspector.UI.ViewModels;

public sealed class TrafficFlowViewModel
{
    public string Source { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string Destination { get; init; } = "";
    public string DestinationName { get; init; } = "";
    public string Protocol { get; init; } = "";
    public long Packets { get; init; }
    public string Bytes { get; init; } = "";
    public string LastSeen { get; init; } = "";
}

public sealed class TrafficTalkerViewModel
{
    public string Address { get; init; } = "";

    /// <summary>Friendly name when one is known; empty otherwise, so the row falls back to the address.</summary>
    public string Name { get; init; } = "";

    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    public string Sent { get; init; } = "";
    public string Received { get; init; } = "";
    public string Total { get; init; } = "";
    public long Packets { get; init; }
    public int Peers { get; init; }
    public string LastSeen { get; init; } = "";
}

public sealed class TrafficPeerViewModel
{
    public string Address { get; init; } = "";
    public string Name { get; init; } = "";
    public string Bytes { get; init; } = "";
    public long Packets { get; init; }

    /// <summary>Name when known, otherwise the bare address — the peer list is too narrow for both.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

public sealed class TrafficChartBar
{
    public double Height { get; init; }
    public double Width { get; init; }
    public string Tooltip { get; init; } = "";
}

public sealed class TrafficWindowOption
{
    public required TrafficWindow Window { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

public sealed partial class TrafficTabViewModel : ObservableObject, IDisposable
{
    /// <summary>Pixel height of the tallest bar; every other bar is scaled against it.</summary>
    private const double ChartHeight = 80;

    /// <summary>Total pixel width the bars share, so windows with fewer buckets still fill it.</summary>
    private const double ChartWidth = 560;

    private readonly ITrafficFlowService _trafficService;
    private readonly IDeviceNameResolver? _nameResolver;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>
    /// Set while a refresh is rebuilding the host list and restoring the selection, so that
    /// restoring it does not recurse straight back into another refresh.
    /// </summary>
    private bool _isRefreshing;

    [ObservableProperty]
    private string _packetsPerSecond = "0 pkt/s";

    [ObservableProperty]
    private string _bytesPerSecond = "0 B/s";

    [ObservableProperty]
    private string _totalPackets = "0";

    [ObservableProperty]
    private string _totalBytes = "0 B";

    [ObservableProperty]
    private string _statusText = "Waiting for capture...";

    [ObservableProperty]
    private string _chartTitle = "Throughput";

    [ObservableProperty]
    private string _chartScaleText = "";

    /// <summary>Totals inside the selected window, distinct from the all-time totals.</summary>
    [ObservableProperty]
    private string _windowSummary = "";

    [ObservableProperty]
    private string _flowsTitle = "Top flows";

    [ObservableProperty]
    private string _detailTitle = "Select a host to drill in";

    [ObservableProperty]
    private string _detailSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearTalkerSelectionCommand))]
    private TrafficTalkerViewModel? _selectedTalker;

    public ObservableCollection<TrafficFlowViewModel> TopFlows { get; } = [];
    public ObservableCollection<TrafficTalkerViewModel> TopTalkers { get; } = [];
    public ObservableCollection<TrafficPeerViewModel> SelectedTalkerPeers { get; } = [];
    public ObservableCollection<TrafficChartBar> ChartBars { get; } = [];

    public IReadOnlyList<TrafficWindowOption> WindowOptions { get; } =
    [
        new() { Window = TrafficWindow.LastMinute, Label = TrafficWindow.LastMinute.GetLabel() },
        new() { Window = TrafficWindow.LastFifteenMinutes, Label = TrafficWindow.LastFifteenMinutes.GetLabel() },
        new() { Window = TrafficWindow.LastHour, Label = TrafficWindow.LastHour.GetLabel() },
        new() { Window = TrafficWindow.LastThreeHours, Label = TrafficWindow.LastThreeHours.GetLabel() }
    ];

    [ObservableProperty]
    private TrafficWindowOption _selectedWindow;

    public TrafficTabViewModel(ITrafficFlowService trafficService, IDeviceNameResolver? nameResolver = null)
    {
        _trafficService = trafficService;
        _nameResolver = nameResolver;
        _selectedWindow = WindowOptions.First(option => option.Window == TrafficWindow.LastHour);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    partial void OnSelectedWindowChanged(TrafficWindowOption value) => Refresh();

    partial void OnSelectedTalkerChanged(TrafficTalkerViewModel? value)
    {
        if (!_isRefreshing)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            RefreshCore();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshCore()
    {
        var window = SelectedWindow.Window;
        var summary = _trafficService.GetSummary(window, topFlowsCount: 15);

        PacketsPerSecond = $"{summary.PacketsPerSecond:F1} pkt/s";
        BytesPerSecond = FormatBytes(summary.BytesPerSecond) + "/s";
        TotalPackets = $"{summary.TotalPackets:N0}";
        TotalBytes = FormatBytes(summary.TotalBytes);
        StatusText = summary.TotalPackets > 0 ? "Live" : "Waiting for capture...";
        WindowSummary = $"{window.GetLabel()}: {FormatBytes(summary.WindowBytes)} in {summary.WindowPackets:N0} packets " +
                        $"(avg {FormatBytes(summary.WindowAverageBytesPerSecond)}/s)";

        RefreshTalkers(window);

        // The chart follows the selection: with a host picked it shows only that host's traffic,
        // which is the point of drilling in.
        var detail = SelectedTalker is null ? null : _trafficService.GetTalkerDetail(SelectedTalker.Address, window);

        var series = detail?.TimeSeries ?? summary.TimeSeries;
        var peakBytesPerSecond = series.Count > 0 ? series.Max(bucket => bucket.BytesPerSecond) : 0;

        ChartTitle = detail is null
            ? $"Throughput — {window.GetLabel()}"
            : $"Throughput — {Describe(detail.Talker.Address)} — {window.GetLabel()}";
        ChartScaleText = peakBytesPerSecond > 0 ? $"peak {FormatBytes(peakBytesPerSecond)}/s" : "no traffic in window";

        RenderChart(series, peakBytesPerSecond, window);
        RefreshFlows(detail, summary);
        RefreshDetail(detail);
    }

    private void RefreshTalkers(TrafficWindow window)
    {
        var talkers = _trafficService.GetTopTalkers(window, count: 20);
        var previousSelection = SelectedTalker?.Address;

        TopTalkers.Clear();
        foreach (var talker in talkers)
        {
            TopTalkers.Add(new TrafficTalkerViewModel
            {
                Address = talker.Address,
                Name = ResolveName(talker.Address),
                Sent = FormatBytes(talker.BytesSent),
                Received = FormatBytes(talker.BytesReceived),
                Total = FormatBytes(talker.TotalBytes),
                Packets = talker.TotalPackets,
                Peers = talker.FlowCount,
                LastSeen = talker.LastSeen.ToLocalTime().ToString("HH:mm:ss")
            });
        }

        // Rebuilding the list drops the selection; restore it so a one-second refresh does not
        // yank the user back out of the host they are inspecting.
        if (previousSelection is not null)
        {
            SelectedTalker = TopTalkers.FirstOrDefault(talker => talker.Address == previousSelection);
        }
    }

    private void RenderChart(IReadOnlyList<TrafficTimeBucket> series, double peakBytesPerSecond, TrafficWindow window)
    {
        ChartBars.Clear();
        if (series.Count == 0)
        {
            return;
        }

        var scale = peakBytesPerSecond > 0 ? peakBytesPerSecond : 1;
        // Minus the 2px of margin each bar carries in the template, so the row fills the width
        // whether it is showing 15 buckets or 180.
        var barWidth = Math.Max(1, ChartWidth / series.Count - 2);
        var timeFormat = window == TrafficWindow.LastMinute ? "HH:mm:ss" : "HH:mm";

        foreach (var bucket in series)
        {
            ChartBars.Add(new TrafficChartBar
            {
                Height = Math.Max(bucket.Bytes > 0 ? 1 : 0, bucket.BytesPerSecond / scale * ChartHeight),
                Width = barWidth,
                Tooltip = $"{bucket.BucketStart.ToLocalTime().ToString(timeFormat)} — " +
                          $"{FormatBytes(bucket.BytesPerSecond)}/s ({bucket.Packets:N0} packets)"
            });
        }
    }

    private void RefreshFlows(TrafficTalkerDetail? detail, TrafficSummary summary)
    {
        var flows = detail?.TopFlows ?? summary.TopFlows;
        FlowsTitle = detail is null ? "Top flows" : $"Flows involving {Describe(detail.Talker.Address)}";

        TopFlows.Clear();
        foreach (var flow in flows)
        {
            TopFlows.Add(new TrafficFlowViewModel
            {
                Source = $"{flow.Key.SourceIp}:{flow.Key.SourcePort}",
                SourceName = ResolveName(flow.Key.SourceIp.ToString()),
                Destination = $"{flow.Key.DestIp}:{flow.Key.DestPort}",
                DestinationName = ResolveName(flow.Key.DestIp.ToString()),
                Protocol = flow.Key.Protocol,
                Packets = flow.Packets,
                Bytes = FormatBytes(flow.Bytes),
                LastSeen = flow.LastSeen.ToLocalTime().ToString("HH:mm:ss")
            });
        }
    }

    private void RefreshDetail(TrafficTalkerDetail? detail)
    {
        SelectedTalkerPeers.Clear();

        if (detail is null)
        {
            DetailTitle = "Select a host to drill in";
            DetailSummary = "Pick a row in Top hosts to filter the chart and flows to that address.";
            return;
        }

        var detailName = ResolveName(detail.Talker.Address);
        DetailTitle = string.IsNullOrWhiteSpace(detailName)
            ? detail.Talker.Address
            : $"{detailName}  ({detail.Talker.Address})";
        var protocols = detail.Protocols.Count == 0
            ? "none"
            : string.Join(", ", detail.Protocols.Take(4).Select(share => $"{share.Protocol} {FormatBytes(share.Bytes)}"));

        DetailSummary = $"sent {FormatBytes(detail.Talker.BytesSent)} · received {FormatBytes(detail.Talker.BytesReceived)} · " +
                        $"{detail.Talker.FlowCount} peer(s) · protocols: {protocols}";

        foreach (var peer in detail.TopPeers)
        {
            SelectedTalkerPeers.Add(new TrafficPeerViewModel
            {
                Address = peer.Address,
                Name = ResolveName(peer.Address),
                Bytes = FormatBytes(peer.Bytes),
                Packets = peer.Packets
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearTalkerSelection))]
    private void ClearTalkerSelection() => SelectedTalker = null;

    private bool CanClearTalkerSelection() => SelectedTalker is not null;

    [RelayCommand]
    private void ResetTraffic()
    {
        _trafficService.Reset();
        SelectedTalker = null;
        Refresh();
    }

    private string ResolveName(string address)
    {
        try
        {
            return _nameResolver?.Resolve(address) ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A name is a convenience; failing to find one must never take down the traffic view.
            System.Diagnostics.Debug.WriteLine($"Name lookup failed for {address}: {ex}");
            return string.Empty;
        }
    }

    private string Describe(string address)
    {
        var name = ResolveName(address);
        return string.IsNullOrWhiteSpace(name) ? address : $"{name} ({address})";
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
