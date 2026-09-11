using System.Net;

namespace LanInspector.Core.Snmp;

public interface ISnmpDiscoveryService
{
    Task<SnmpQueryResult> QueryAsync(IPAddress target, string community = "public", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnmpFdbEntry>> GetFdbTableAsync(IPAddress target, string community = "public", CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads per-interface byte counters. Polled repeatedly and differenced, these give whole-home
    /// throughput at the router — traffic a packet capture on one machine cannot see, because a
    /// switch never forwards other devices' unicast frames to it.
    /// </summary>
    Task<SnmpCountersResult> GetInterfaceCountersAsync(IPAddress target, string community = "public", CancellationToken cancellationToken = default);
}
