using System.Net;

namespace LanInspector.Core.Snmp;

public interface ISnmpDiscoveryService
{
    Task<SnmpQueryResult> QueryAsync(IPAddress target, string community = "public", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnmpFdbEntry>> GetFdbTableAsync(IPAddress target, string community = "public", CancellationToken cancellationToken = default);
}
