using System.Net.Http;
using System.Text.Json;

namespace LanInspector.Core.Dns;

public sealed class PiHoleDnsService : IDnsFilterService
{
    private readonly PiHoleConfig _config;
    private readonly HttpClient _http;

    public string ProviderName => "Pi-hole";

    public PiHoleDnsService(PiHoleConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.Url.TrimEnd('/') + "/") };
    }

    private string ApiBase => $"admin/api.php?auth={_config.ApiToken ?? ""}";

    public async Task<DnsFilterStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{ApiBase}&summaryRaw", cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() : "unknown";
            var blockedToday = doc.RootElement.TryGetProperty("ads_blocked_today", out var bEl) ? bEl.GetInt32() : 0;
            var totalToday = doc.RootElement.TryGetProperty("dns_queries_today", out var tEl) ? tEl.GetInt32() : 0;
            var percent = doc.RootElement.TryGetProperty("ads_percentage_today", out var pEl) ? pEl.GetDouble() : 0.0;

            return new DnsFilterStatus(ProviderName, true,
                string.Equals(status, "enabled", StringComparison.OrdinalIgnoreCase),
                blockedToday, totalToday, percent);
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
            var json = await _http.GetStringAsync($"{ApiBase}&topClients&topItems", cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("top_sources", out var sourcesEl))
            {
                foreach (var prop in sourcesEl.EnumerateObject())
                    topClients.Add(new DnsTopClient(prop.Name.Split('|')[0], prop.Value.GetInt32()));
            }

            if (doc.RootElement.TryGetProperty("top_queries", out var queriesEl))
            {
                foreach (var prop in queriesEl.EnumerateObject())
                    topDomains.Add(new DnsTopDomain(prop.Name, prop.Value.GetInt32(), false));
            }

            if (doc.RootElement.TryGetProperty("top_ads", out var adsEl))
            {
                var blockedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in adsEl.EnumerateObject())
                    blockedSet.Add(prop.Name);

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
            var json = await _http.GetStringAsync($"{ApiBase}&getAllQueries={count}", cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var records = new List<DnsQueryRecord>();

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            foreach (var entry in data.EnumerateArray())
            {
                var arr = entry.EnumerateArray().ToList();
                if (arr.Count < 6) continue;

                var ts = DateTimeOffset.FromUnixTimeSeconds(arr[0].GetInt64()).UtcDateTime;
                var qType = arr[1].GetString() ?? "A";
                var domain = arr[2].GetString() ?? "";
                var client = arr[3].GetString() ?? "";
                var statusCode = arr[4].GetInt32();
                var blocked = statusCode is 1 or 4 or 5 or 6;

                records.Add(new DnsQueryRecord(ts, client, domain, qType, "", blocked));
            }

            return records;
        }
        catch
        {
            return [];
        }
    }
}
