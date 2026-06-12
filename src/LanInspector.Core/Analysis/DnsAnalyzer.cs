using System.Collections.Concurrent;
using System.Net;
using LanInspector.Core.Model;
using PacketDotNet;

namespace LanInspector.Core.Analysis;

public sealed class DnsAnalyzer : IDeviceObservingAnalyzer
{
    private readonly ConcurrentDictionary<string, Device> _devices;

    public DnsAnalyzer(ConcurrentDictionary<string, Device> devices)
    {
        _devices = devices;
    }

    public event EventHandler<DeviceObservedEventArgs>? DeviceObserved;

    public void Analyze(Packet packet)
    {
        var udp = packet.Extract<UdpPacket>();
        if (udp is null)
        {
            return;
        }

        var isDns = udp.SourcePort is 53 or 5353 || udp.DestinationPort is 53 or 5353;
        if (!isDns || udp.PayloadData is not { Length: >= 12 } payload)
        {
            return;
        }

        IPAddress? sourceAddress = packet.Extract<IPv4Packet>()?.SourceAddress
            ?? packet.Extract<IPv6Packet>()?.SourceAddress;

        try
        {
            var message = SimpleDnsParser.Parse(payload);
            var seenVia = udp.SourcePort == 5353 || udp.DestinationPort == 5353 ? "mDNS" : "DNS";

            foreach (var answer in message.Answers)
            {
                if (answer.Type is not DnsRecordType.A and not DnsRecordType.Aaaa)
                {
                    continue;
                }

                if (!IPAddress.TryParse(answer.Data, out _))
                {
                    continue;
                }

                var name = NormalizeName(answer.Name);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                UpdateByIp(answer.Data, name, seenVia);
            }

            if (sourceAddress is null)
            {
                return;
            }

            foreach (var question in message.Questions)
            {
                var name = NormalizeName(question.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    UpdateByIp(sourceAddress.ToString(), name, seenVia, preferHostname: false);
                }
            }
        }
        catch
        {
            // DNS packets are often truncated or compressed in forms this small parser does not need yet.
        }
    }

    private void UpdateByIp(string ipAddress, string name, string seenVia, bool preferHostname = true)
    {
        foreach (var device in _devices.Values)
        {
            var changed = false;

            lock (device)
            {
                if (!device.IpAddresses.Contains(ipAddress))
                {
                    continue;
                }

                device.ObservedNames.Add(name);
                device.SeenVia.Add(seenVia);

                if (preferHostname || string.IsNullOrWhiteSpace(device.Hostname))
                {
                    device.Hostname = name;
                }

                device.LastSeen = DateTime.UtcNow;
                changed = true;
            }

            if (changed)
            {
                DeviceObserved?.Invoke(this, new DeviceObservedEventArgs(device));
            }
        }
    }

    private static string NormalizeName(string value)
    {
        return value.Trim().TrimEnd('.');
    }
}

public enum DnsRecordType : ushort
{
    A = 1,
    Cname = 5,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28
}

public sealed record DnsQuestion(string Name, ushort Type, ushort Class);

public sealed record DnsAnswer(string Name, DnsRecordType Type, ushort Class, uint Ttl, string Data);

public sealed class SimpleDnsMessage
{
    public List<DnsQuestion> Questions { get; } = [];

    public List<DnsAnswer> Answers { get; } = [];
}

public static class SimpleDnsParser
{
    public static SimpleDnsMessage Parse(byte[] data)
    {
        var message = new SimpleDnsMessage();
        var position = 0;

        if (data.Length < 12)
        {
            throw new ArgumentException("DNS payload is too short.", nameof(data));
        }

        position += 4;
        var questionCount = ReadUInt16(data, ref position);
        var answerCount = ReadUInt16(data, ref position);
        position += 4;

        for (var index = 0; index < questionCount; index++)
        {
            var name = ReadName(data, ref position);
            var type = ReadUInt16(data, ref position);
            var cls = ReadUInt16(data, ref position);
            message.Questions.Add(new DnsQuestion(name, type, cls));
        }

        for (var index = 0; index < answerCount; index++)
        {
            var name = ReadName(data, ref position);
            var type = (DnsRecordType)ReadUInt16(data, ref position);
            var cls = ReadUInt16(data, ref position);
            var ttl = ReadUInt32(data, ref position);
            var dataLength = ReadUInt16(data, ref position);

            if (position + dataLength > data.Length)
            {
                throw new ArgumentException("DNS record length exceeds payload size.", nameof(data));
            }

            var recordData = ReadRecordData(data, position, dataLength, type);
            position += dataLength;
            message.Answers.Add(new DnsAnswer(name, type, cls, ttl, recordData));
        }

        return message;
    }

    private static string ReadRecordData(byte[] data, int position, ushort length, DnsRecordType type)
    {
        if (type == DnsRecordType.A && length == 4)
        {
            return new IPAddress(data.AsSpan(position, 4)).ToString();
        }

        if (type == DnsRecordType.Aaaa && length == 16)
        {
            return new IPAddress(data.AsSpan(position, 16)).ToString();
        }

        if (type is DnsRecordType.Cname or DnsRecordType.Ptr)
        {
            var nestedPosition = position;
            return ReadName(data, ref nestedPosition);
        }

        return Convert.ToHexString(data.AsSpan(position, length));
    }

    private static ushort ReadUInt16(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, 2);
        var value = (ushort)((data[position] << 8) | data[position + 1]);
        position += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, 4);
        var value = ((uint)data[position] << 24)
            | ((uint)data[position + 1] << 16)
            | ((uint)data[position + 2] << 8)
            | data[position + 3];
        position += 4;
        return value;
    }

    private static string ReadName(byte[] data, ref int position)
    {
        var labels = new List<string>();
        var jumpedPosition = -1;
        var guard = 0;

        while (position < data.Length && guard++ < data.Length)
        {
            var length = data[position++];
            if (length == 0)
            {
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(data, position, 1);
                var pointer = ((length & 0x3F) << 8) | data[position++];
                jumpedPosition = jumpedPosition == -1 ? position : jumpedPosition;
                position = pointer;
                continue;
            }

            EnsureAvailable(data, position, length);
            labels.Add(System.Text.Encoding.UTF8.GetString(data, position, length));
            position += length;
        }

        if (jumpedPosition != -1)
        {
            position = jumpedPosition;
        }

        return string.Join(".", labels);
    }

    private static void EnsureAvailable(byte[] data, int position, int count)
    {
        if (position < 0 || position + count > data.Length)
        {
            throw new ArgumentException("DNS payload ended unexpectedly.", nameof(data));
        }
    }
}
