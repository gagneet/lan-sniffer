using PacketDotNet;
using LanInspector.Core.Analysis;

namespace LanInspector.Core.Traffic;

public sealed class TrafficFlowAnalyzer : IPacketAnalyzer
{
    private readonly TrafficFlowService _service;

    public TrafficFlowAnalyzer(TrafficFlowService service)
    {
        _service = service;
    }

    public void Analyze(Packet packet)
    {
        var ip = packet.Extract<IPPacket>();
        if (ip is null)
            return;

        var src = ip.SourceAddress;
        var dst = ip.DestinationAddress;
        var bytes = ip.TotalLength;

        var tcp = packet.Extract<TcpPacket>();
        if (tcp is not null)
        {
            _service.Record(src, dst, tcp.SourcePort, tcp.DestinationPort, "TCP", bytes);
            return;
        }

        var udp = packet.Extract<UdpPacket>();
        if (udp is not null)
        {
            _service.Record(src, dst, udp.SourcePort, udp.DestinationPort, "UDP", bytes);
            return;
        }

        _service.Record(src, dst, 0, 0, ip.Protocol.ToString(), bytes);
    }
}
