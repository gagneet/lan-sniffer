using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LanInspector.Core.Dns;

public sealed class AdGuardHomeDnsService : IDnsFilterService
{
    private readonly AdGuardHomeConfig _config;
    private readonly HttpClient _http;

    public string ProviderName => "AdGuard Home";

    public AdGuardHomeDnsService(AdGuardHomeConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.Url.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    public async Task<DnsFilterStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var statusJson = await _http.GetStringAsync("control/status", cancellationToken);
            var statsJson = await _http.GetStringAsync("control/stats", cancellationToken);

            using var statusDoc = JsonDocument.Parse(statusJson);
            using var statsDoc = JsonDocument.Parse(statsJson);

            var filteringEnabled = statusDoc.RootElement.TryGetProperty("protection_enabled", out var pe) && pe.GetBoolean();
            var blockedToday = statsDoc.RootElement.TryGetProperty("num_blocked_filtering", out var blocked)
                ? (int)blocked.GetInt64() : 0;
            var totalToday = statsDoc.RootElement.TryGetProperty("num_dns_queries", out var total)
                ? (int)total.GetInt64() : 0;
            var blockPercent = totalToday > 0 ? (double)blockedToday / totalToday * 100 : 0;

            return new DnsFilterStatus(ProviderName, true, filteringEnabled, blockedToday, totalToday, blockPercent);
        }
        catch (Exception ex)
        {
            return new DnsFilterStatus(ProviderName, false, false, 0, 0, 0, ex.Message);
        }
    }

    public async Task<DnsFilterSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var queries = await GetRecentQueriesAsync(100, cancellationToken);

        List<DnsTopClient> topClients = [];
        List<DnsTopDomain> topDomains = [];

        try
        {
            var statsJson = await _http.GetStringAsync("control/stats", cancellationToken);
            using var doc = JsonDocument.Parse(statsJson);

            if (doc.RootElement.TryGetProperty("top_clients", out var clientsEl))
            {
                foreach (var entry in clientsEl.EnumerateArray())
                {
                    foreach (var prop in entry.EnumerateObject())
                        topClients.Add(new DnsTopClient(prop.Name, (int)prop.Value.GetInt64()));
                }
            }

            if (doc.RootElement.TryGetProperty("top_queried_domains", out var domainsEl))
            {
                foreach (var entry in domainsEl.EnumerateArray())
                {
                    foreach (var prop in entry.EnumerateObject())
                        topDomains.Add(new DnsTopDomain(prop.Name, (int)prop.Value.GetInt64(), false));
                }
            }

            if (doc.RootElement.TryGetProperty("top_blocked_domains", out var blockedEl))
            {
                var blockedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in blockedEl.EnumerateArray())
                {
                    foreach (var prop in entry.EnumerateObject())
                        blockedSet.Add(prop.Name);
                }

                topDomains = topDomains
                    .Select(d => d with { IsBlocked = blockedSet.Contains(d.Domain) })
                    .ToList();
            }
        }
        catch { }

        return new DnsFilterSummary(status, topClients, topDomains, queries);
    }

    public async Task<IReadOnlyList<DnsQueryRecord>> GetRecentQueriesAsync(
        int count = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"control/querylog?limit={count}", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var records = new List<DnsQueryRecord>();

            var data = doc.RootElement.TryGetProperty("data", out var dataEl) ? dataEl : doc.RootElement;

            foreach (var entry in data.EnumerateArray())
            {
                var ts = entry.TryGetProperty("time", out var timeEl) && DateTime.TryParse(timeEl.GetString(), out var t) ? t : DateTime.UtcNow;
                var client = entry.TryGetProperty("client", out var clientEl) ? clientEl.GetString() ?? "" : "";
                var domain = entry.TryGetProperty("question", out var qEl) && qEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var qType = entry.TryGetProperty("question", out var q2El) && q2El.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "A" : "A";
                var answer = entry.TryGetProperty("answer", out var ansEl) ? ansEl.GetString() ?? "" : "";
                var blocked = entry.TryGetProperty("reason", out var reasonEl) && (reasonEl.GetString()?.Contains("Block") == true);
                var blockedBy = blocked && entry.TryGetProperty("rule", out var ruleEl) ? ruleEl.GetString() : null;

                records.Add(new DnsQueryRecord(ts, client, domain, qType, answer, blocked, blockedBy));
            }

            return records;
        }
        catch
        {
            return [];
        }
    }
}
