using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanInspector.Core.Dns;

namespace LanInspector.UI.ViewModels;

public sealed class DnsQueryViewModel
{
    public string Time { get; init; } = "";
    public string Client { get; init; } = "";
    public string Domain { get; init; } = "";
    public string QueryType { get; init; } = "";
    public string Blocked { get; init; } = "";
}

public sealed class DnsClientViewModel
{
    public string ClientIp { get; init; } = "";
    public int QueryCount { get; init; }
}

public sealed class DnsDomainViewModel
{
    public string Domain { get; init; } = "";
    public int HitCount { get; init; }
    public string Blocked { get; init; } = "";
}

public sealed partial class DnsTabViewModel : ObservableObject
{
    private readonly IDnsFilterService? _service;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _providerName = "No provider configured";

    [ObservableProperty]
    private string _filteringStatus = "";

    [ObservableProperty]
    private string _blockedToday = "";

    [ObservableProperty]
    private string _totalToday = "";

    [ObservableProperty]
    private string _blockPercent = "";

    [ObservableProperty]
    private bool _isConnected;

    public ObservableCollection<DnsQueryViewModel> RecentQueries { get; } = [];
    public ObservableCollection<DnsClientViewModel> TopClients { get; } = [];
    public ObservableCollection<DnsDomainViewModel> TopDomains { get; } = [];

    public DnsTabViewModel(IDnsFilterService? service)
    {
        _service = service;
        if (service is null)
        {
            StatusText = $"No DNS provider configured. Create integrations.json at {DnsIntegrationsConfigLoader.GetDefaultPath()}";
            ProviderName = "None";
        }
        else
        {
            ProviderName = service.ProviderName;
            StatusText = "Click Refresh to load data";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_service is null)
            return;

        StatusText = "Loading...";

        try
        {
            var summary = await _service.GetSummaryAsync();

            ProviderName = summary.Status.ProviderName;
            IsConnected = summary.Status.IsConnected;
            FilteringStatus = summary.Status.FilteringEnabled ? "Enabled" : "Disabled";
            BlockedToday = $"{summary.Status.BlockedToday:N0}";
            TotalToday = $"{summary.Status.TotalToday:N0}";
            BlockPercent = $"{summary.Status.BlockPercent:F1}%";

            if (summary.Status.ErrorMessage is not null)
                StatusText = $"Error: {summary.Status.ErrorMessage}";
            else
                StatusText = $"Last refreshed: {DateTime.Now:HH:mm:ss}";

            TopClients.Clear();
            foreach (var c in summary.TopClients.Take(10))
                TopClients.Add(new DnsClientViewModel { ClientIp = c.ClientIp, QueryCount = c.QueryCount });

            TopDomains.Clear();
            foreach (var d in summary.TopDomains.Take(20))
                TopDomains.Add(new DnsDomainViewModel { Domain = d.Domain, HitCount = d.HitCount, Blocked = d.IsBlocked ? "Blocked" : "" });

            RecentQueries.Clear();
            foreach (var q in summary.RecentQueries.Take(50))
            {
                RecentQueries.Add(new DnsQueryViewModel
                {
                    Time = q.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                    Client = q.ClientIp,
                    Domain = q.Domain,
                    QueryType = q.QueryType,
                    Blocked = q.WasBlocked ? "Blocked" : ""
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }
}
