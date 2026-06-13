using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PacketDotNet;

namespace LanInspector.Core.Analysis;

public sealed record LldpNeighbour(
    string ChassisId,
    string PortId,
    string? SystemName,
    string? ManagementAddress,
    DateTime LastSeen);

public sealed class LldpAnalyzer : IPacketAnalyzer
{
    // EtherType 0x88CC = LLDP
    private const ushort LldpEtherType = 0x88CC;

    private readonly ConcurrentDictionary<string, LldpNeighbour> _neighbours = new();

    public event EventHandler<LldpNeighbour>? NeighbourDiscovered;

    public IReadOnlyCollection<LldpNeighbour> Neighbours => _neighbours.Values.ToArray();

    public void Analyze(Packet packet)
    {
        var eth = packet.Extract<EthernetPacket>();
        if (eth is null)
            return;

        // EtherType is a ushort; check for LLDP
        if (eth.Type != (EthernetType)LldpEtherType)
            return;

        var payload = eth.PayloadData;
        if (payload is null || payload.Length < 4)
            return;

        ParseLldpPdu(payload);
    }

    private void ParseLldpPdu(byte[] pdu)
    {
        string? chassisId = null;
        string? portId = null;
        string? systemName = null;
        string? managementAddress = null;

        var offset = 0;
        while (offset + 2 <= pdu.Length)
        {
            var tlvHeader = (ushort)((pdu[offset] << 8) | pdu[offset + 1]);
            var tlvType = (tlvHeader >> 9) & 0x7F;
            var tlvLength = tlvHeader & 0x01FF;
            offset += 2;

            if (offset + tlvLength > pdu.Length)
                break;

            var value = pdu.AsSpan(offset, tlvLength);

            switch (tlvType)
            {
                case 0: // end of LLDPDU
                    goto done;

                case 1: // chassis ID
                    if (tlvLength > 1)
                        chassisId = ParseIdString(value[1..]);
                    break;

                case 2: // port ID
                    if (tlvLength > 1)
                        portId = ParseIdString(value[1..]);
                    break;

                case 5: // system name
                    systemName = Encoding.UTF8.GetString(value);
                    break;

                case 8: // management address
                    managementAddress = ParseManagementAddress(value);
                    break;
            }

            offset += tlvLength;
        }

        done:
        if (chassisId is null || portId is null)
            return;

        var neighbour = new LldpNeighbour(chassisId, portId, systemName, managementAddress, DateTime.UtcNow);
        _neighbours[chassisId] = neighbour;
        NeighbourDiscovered?.Invoke(this, neighbour);
    }

    private static string ParseIdString(ReadOnlySpan<byte> data)
    {
        // Try printable ASCII, else hex
        bool printable = true;
        foreach (var b in data)
        {
            if (b < 0x20 || b > 0x7E) { printable = false; break; }
        }

        return printable
            ? Encoding.ASCII.GetString(data)
            : BitConverter.ToString(data.ToArray()).Replace("-", ":");
    }

    private static string? ParseManagementAddress(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2)
            return null;

        var addrLen = value[0] - 1;
        var addrType = value[1]; // 1 = IPv4, 2 = IPv6

        if (addrLen == 4 && addrType == 1 && value.Length >= 2 + addrLen)
        {
            var addr = new IPAddress(value.Slice(2, 4).ToArray());
            return addr.ToString();
        }

        return null;
    }
}
