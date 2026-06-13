namespace LanInspector.Core.Dns;

public sealed record DnsFilterStatus(
    string ProviderName,
    bool IsConnected,
    bool FilteringEnabled,
    int BlockedToday,
    int TotalToday,
    double BlockPercent,
    string? ErrorMessage = null);

public sealed record DnsQueryRecord(
    DateTime Timestamp,
    string ClientIp,
    string Domain,
    string QueryType,
    string Answer,
    bool WasBlocked,
    string? BlockedBy = null);

public sealed record DnsTopClient(string ClientIp, int QueryCount);

public sealed record DnsTopDomain(string Domain, int HitCount, bool IsBlocked);

public sealed record DnsFilterSummary(
    DnsFilterStatus Status,
    IReadOnlyList<DnsTopClient> TopClients,
    IReadOnlyList<DnsTopDomain> TopDomains,
    IReadOnlyList<DnsQueryRecord> RecentQueries);

public sealed class DnsIntegrationsConfig
{
    public AdGuardHomeConfig? AdGuardHome { get; init; }
    public PiHoleConfig? PiHole { get; init; }
}

public sealed class AdGuardHomeConfig
{
    public string Url { get; init; } = "http://192.168.0.1:3000";
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed class PiHoleConfig
{
    public string Url { get; init; } = "http://192.168.0.1";
    public string? ApiToken { get; init; }
}
