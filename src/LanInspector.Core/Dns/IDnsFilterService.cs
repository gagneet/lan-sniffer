namespace LanInspector.Core.Dns;

public interface IDnsFilterService
{
    string ProviderName { get; }
    Task<DnsFilterStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<DnsFilterSummary> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DnsQueryRecord>> GetRecentQueriesAsync(int count = 100, CancellationToken cancellationToken = default);
}
