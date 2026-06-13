using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace LanInspector.Core.Snmp;

public sealed class SnmpDiscoveryService : ISnmpDiscoveryService
{
    // Standard MIB OIDs
    private static readonly ObjectIdentifier OidSysDescr = new("1.3.6.1.2.1.1.1.0");
    private static readonly ObjectIdentifier OidSysName = new("1.3.6.1.2.1.1.5.0");
    private static readonly ObjectIdentifier OidSysLocation = new("1.3.6.1.2.1.1.6.0");
    private static readonly ObjectIdentifier OidIfTable = new("1.3.6.1.2.1.2.2");
    private static readonly ObjectIdentifier OidIpAddrTable = new("1.3.6.1.2.1.4.20");
    private static readonly ObjectIdentifier OidFdbTable = new("1.3.6.1.2.1.17.4.3");

    private const int DefaultPort = 161;
    private const int TimeoutMs = 3000;

    public async Task<SnmpQueryResult> QueryAsync(
        IPAddress target, string community = "public", CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new IPEndPoint(target, DefaultPort);
            var communityOctet = new OctetString(community);

            var scalar = await GetScalarsAsync(endpoint, communityOctet, cancellationToken);
            var sysDescr = scalar.GetValueOrDefault(OidSysDescr);
            var sysName = scalar.GetValueOrDefault(OidSysName);
            var sysLocation = scalar.GetValueOrDefault(OidSysLocation);

            var interfaces = await WalkInterfacesAsync(endpoint, communityOctet, cancellationToken);
            var ips = await WalkIpAddressesAsync(endpoint, communityOctet, cancellationToken);

            var info = new SnmpDeviceInfo(target, sysDescr, sysName, sysLocation, interfaces, ips);
            return new SnmpQueryResult(target, info);
        }
        catch (OperationCanceledException)
        {
            return new SnmpQueryResult(target, null, "Cancelled");
        }
        catch (Exception ex)
        {
            return new SnmpQueryResult(target, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<SnmpFdbEntry>> GetFdbTableAsync(
        IPAddress target, string community = "public", CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new IPEndPoint(target, DefaultPort);
            var communityOctet = new OctetString(community);
            var results = new List<SnmpFdbEntry>();

            var table = await WalkAsync(endpoint, communityOctet, OidFdbTable, cancellationToken);
            foreach (var pair in table)
            {
                var oidStr = pair.Key.ToString();
                // OID suffix encodes MAC as decimal octets: .1.3.6.1.2.1.17.4.3.1.1.<m1>.<m2>.<m3>.<m4>.<m5>.<m6>
                // We want the port from .1.3.6.1.2.1.17.4.3.1.2.<m1>...<m6>
                if (oidStr.Contains(".17.4.3.1.2."))
                {
                    var octets = oidStr.Split('.').TakeLast(6).Select(o => int.TryParse(o, out var b) ? b : 0).ToArray();
                    if (octets.Length == 6)
                    {
                        var mac = string.Join(":", octets.Select(o => o.ToString("x2")));
                        if (int.TryParse(pair.Value, out var port))
                            results.Add(new SnmpFdbEntry(mac, port));
                    }
                }
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<Dictionary<ObjectIdentifier, string>> GetScalarsAsync(
        IPEndPoint endpoint, OctetString community, CancellationToken cancellationToken)
    {
        var oids = new List<Variable>
        {
            new(OidSysDescr),
            new(OidSysName),
            new(OidSysLocation)
        };

        var result = new Dictionary<ObjectIdentifier, string>();

        await Task.Run(() =>
        {
            var response = Messenger.Get(
                VersionCode.V2,
                endpoint,
                community,
                oids,
                TimeoutMs);

            foreach (var v in response)
                result[v.Id] = v.Data.ToString() ?? "";
        }, cancellationToken);

        return result;
    }

    private static async Task<List<SnmpInterface>> WalkInterfacesAsync(
        IPEndPoint endpoint, OctetString community, CancellationToken cancellationToken)
    {
        var ifDescr = new ObjectIdentifier("1.3.6.1.2.1.2.2.1.2");
        var ifType = new ObjectIdentifier("1.3.6.1.2.1.2.2.1.3");
        var ifOperStatus = new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8");
        var ifSpeed = new ObjectIdentifier("1.3.6.1.2.1.2.2.1.5");

        var ifaces = new List<SnmpInterface>();

        try
        {
            var descrTable = await WalkAsync(endpoint, community, ifDescr, cancellationToken);
            var operTable = await WalkAsync(endpoint, community, ifOperStatus, cancellationToken);
            var speedTable = await WalkAsync(endpoint, community, ifSpeed, cancellationToken);

            foreach (var kv in descrTable)
            {
                var idxStr = kv.Key.ToString().Split('.').Last();
                if (!int.TryParse(idxStr, out var idx)) continue;

                var operKey = operTable.Keys.FirstOrDefault(k => k.ToString().EndsWith($".{idx}"));
                var speedKey = speedTable.Keys.FirstOrDefault(k => k.ToString().EndsWith($".{idx}"));

                var oper = operKey is not null && operTable.TryGetValue(operKey, out var o) ? o : "unknown";
                var speedStr = speedKey is not null && speedTable.TryGetValue(speedKey, out var s) ? s : null;
                var speed = long.TryParse(speedStr, out var sp) ? sp : (long?)null;

                ifaces.Add(new SnmpInterface(idx, kv.Value, "unknown", oper, speed));
            }
        }
        catch { }

        return ifaces;
    }

    private static async Task<List<string>> WalkIpAddressesAsync(
        IPEndPoint endpoint, OctetString community, CancellationToken cancellationToken)
    {
        var addrs = new List<string>();
        try
        {
            var table = await WalkAsync(endpoint, community, OidIpAddrTable, cancellationToken);
            foreach (var kv in table)
            {
                if (kv.Key.ToString().Contains(".4.20.1.1."))
                    addrs.Add(kv.Value);
            }
        }
        catch { }
        return addrs;
    }

    private static Task<Dictionary<ObjectIdentifier, string>> WalkAsync(
        IPEndPoint endpoint, OctetString community, ObjectIdentifier startOid, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new Dictionary<ObjectIdentifier, string>();
            var list = new List<Variable>();

            Messenger.Walk(
                VersionCode.V2,
                endpoint,
                community,
                startOid,
                list,
                TimeoutMs,
                WalkMode.WithinSubtree);

            foreach (var v in list)
                result[v.Id] = v.Data.ToString() ?? "";

            return result;
        }, cancellationToken);
    }
}
