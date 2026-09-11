using System.Net;
using System.Net.NetworkInformation;
using PacketDotNet;

namespace LanInspector.Tests;

/// <summary>
/// Builds real DNS response packets so the analyzer can be driven the way a capture drives it,
/// rather than by reaching past it into its events.
/// </summary>
internal static class DnsPacketBuilder
{
    /// <summary>
    /// A DNS response carrying one question and one A-record answer for <paramref name="name"/>.
    /// </summary>
    public static Packet AnswerFor(
        string name,
        string answerAddress,
        string sourceAddress = "192.168.0.1",
        string destinationAddress = "192.168.0.50",
        int sourcePort = 53)
    {
        var payload = BuildDnsResponse(name, IPAddress.Parse(answerAddress));

        var udp = new UdpPacket((ushort)sourcePort, 51000) { PayloadData = payload };
        var ip = new IPv4Packet(IPAddress.Parse(sourceAddress), IPAddress.Parse(destinationAddress))
        {
            PayloadPacket = udp
        };
        var ethernet = new EthernetPacket(
            PhysicalAddress.Parse("AA-BB-CC-DD-EE-FF"),
            PhysicalAddress.Parse("11-22-33-44-55-66"),
            EthernetType.IPv4)
        {
            PayloadPacket = ip
        };

        udp.UpdateCalculatedValues();
        ip.UpdateCalculatedValues();

        // Re-parsing gives the analyzer the same byte-level view a captured frame would have.
        return Packet.ParsePacket(LinkLayers.Ethernet, ethernet.Bytes);
    }

    private static byte[] BuildDnsResponse(string name, IPAddress answer)
    {
        var bytes = new List<byte>();

        // Header: id, flags (response), 1 question, 1 answer, no authority or additional records.
        bytes.AddRange([0x12, 0x34]);
        bytes.AddRange([0x81, 0x80]);
        bytes.AddRange([0x00, 0x01]);
        bytes.AddRange([0x00, 0x01]);
        bytes.AddRange([0x00, 0x00]);
        bytes.AddRange([0x00, 0x00]);

        var encodedName = EncodeName(name).ToArray();

        // Question: name, QTYPE=A, QCLASS=IN.
        bytes.AddRange(encodedName);
        bytes.AddRange([0x00, 0x01]);
        bytes.AddRange([0x00, 0x01]);

        // Answer: the name written out again rather than as a compression pointer, which keeps
        // this builder independent of where the question happens to sit in the message.
        bytes.AddRange(encodedName);
        bytes.AddRange([0x00, 0x01]);
        bytes.AddRange([0x00, 0x01]);
        bytes.AddRange([0x00, 0x00, 0x01, 0x2C]);
        bytes.AddRange([0x00, 0x04]);
        bytes.AddRange(answer.GetAddressBytes());

        return [.. bytes];
    }

    private static IEnumerable<byte> EncodeName(string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes(label);
            yield return (byte)encoded.Length;
            foreach (var b in encoded)
            {
                yield return b;
            }
        }

        yield return 0;
    }
}
