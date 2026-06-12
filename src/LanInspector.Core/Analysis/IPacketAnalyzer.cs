using PacketDotNet;

namespace LanInspector.Core.Analysis;

public interface IPacketAnalyzer
{
    void Analyze(Packet packet);
}
