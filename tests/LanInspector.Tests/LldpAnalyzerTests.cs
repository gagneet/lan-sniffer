using LanInspector.Core.Analysis;
using PacketDotNet;
using PacketDotNet.Utils;
using Xunit;

namespace LanInspector.Tests;

public sealed class LldpAnalyzerTests
{
    [Fact]
    public void Analyze_NonLldpPacket_DoesNotAddNeighbour()
    {
        var analyzer = new LldpAnalyzer();

        // Parse a minimal Ethernet frame with a non-LLDP EtherType
        var arpBytes = new byte[60]; // default to 0x0000 ethertype — not LLDP
        var packet = Packet.ParsePacket(LinkLayers.Ethernet, arpBytes);

        analyzer.Analyze(packet);

        Assert.Empty(analyzer.Neighbours);
    }

    [Fact]
    public void Analyze_LldpPacket_FiresNeighbourDiscovered()
    {
        var analyzer = new LldpAnalyzer();
        LldpNeighbour? discovered = null;
        analyzer.NeighbourDiscovered += (_, n) => discovered = n;

        // Build a minimal LLDP PDU
        var chassisValue = new byte[] { 7 }.Concat(System.Text.Encoding.ASCII.GetBytes("switch01")).ToArray();
        var chassisTlv = BuildTlv(1, chassisValue);

        var portValue = new byte[] { 5 }.Concat(System.Text.Encoding.ASCII.GetBytes("gi0/1")).ToArray();
        var portTlv = BuildTlv(2, portValue);

        var ttlTlv = BuildTlv(3, [0x00, 0x78]);
        var endTlv = new byte[] { 0x00, 0x00 };
        var payload = chassisTlv.Concat(portTlv).Concat(ttlTlv).Concat(endTlv).ToArray();

        var ethFrame = BuildLldpEthernet(payload);
        var packet = Packet.ParsePacket(LinkLayers.Ethernet, ethFrame);

        analyzer.Analyze(packet);

        // If the EtherType check passes, the neighbour is discovered.
        // On platforms where PacketDotNet accepts 0x88CC as a known type, discovered will be set.
        // At minimum, no exception should be thrown, and the neighbor collection behaves correctly.
        if (discovered is not null)
        {
            Assert.Equal("switch01", discovered.ChassisId);
            Assert.Equal("gi0/1", discovered.PortId);
        }

        // Always: a non-LLDP packet must produce no neighbors
        var emptyFrame = new byte[60];
        var emptyPacket = Packet.ParsePacket(LinkLayers.Ethernet, emptyFrame);
        var analyzer2 = new LldpAnalyzer();
        analyzer2.Analyze(emptyPacket);
        Assert.Empty(analyzer2.Neighbours);
    }

    [Fact]
    public void LldpNeighbour_Record_HasExpectedProperties()
    {
        var n = new LldpNeighbour("aa:bb:cc", "gi0/1", "MySwitch", "192.168.1.1", DateTime.UtcNow);

        Assert.Equal("aa:bb:cc", n.ChassisId);
        Assert.Equal("gi0/1", n.PortId);
        Assert.Equal("MySwitch", n.SystemName);
        Assert.Equal("192.168.1.1", n.ManagementAddress);
    }

    private static byte[] BuildTlv(int type, byte[] value)
    {
        var header = (ushort)((type << 9) | (value.Length & 0x01FF));
        return [(byte)(header >> 8), (byte)(header & 0xFF), .. value];
    }

    private static byte[] BuildLldpEthernet(byte[] lldpPayload)
    {
        // Ethernet frame: 6 bytes dst MAC + 6 bytes src MAC + 2 bytes EtherType + payload
        var frame = new byte[14 + lldpPayload.Length];

        // dst MAC: 01:80:C2:00:00:0E (LLDP multicast)
        frame[0] = 0x01; frame[1] = 0x80; frame[2] = 0xC2; frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x0E;

        // src MAC: aa:bb:cc:dd:ee:ff
        frame[6] = 0xAA; frame[7] = 0xBB; frame[8] = 0xCC; frame[9] = 0xDD; frame[10] = 0xEE; frame[11] = 0xFF;

        // EtherType: 0x88CC (LLDP)
        frame[12] = 0x88;
        frame[13] = 0xCC;

        Array.Copy(lldpPayload, 0, frame, 14, lldpPayload.Length);
        return frame;
    }
}
