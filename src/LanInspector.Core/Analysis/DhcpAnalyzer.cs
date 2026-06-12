using System.Collections.Concurrent;
using LanInspector.Core.Model;
using PacketDotNet;

namespace LanInspector.Core.Analysis;

public sealed class DhcpAnalyzer : IDeviceObservingAnalyzer
{
    private readonly ConcurrentDictionary<string, Device> _devices;

    public DhcpAnalyzer(ConcurrentDictionary<string, Device> devices)
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

        var isDhcp = udp.SourcePort is 67 or 68 || udp.DestinationPort is 67 or 68;
        if (!isDhcp || udp.PayloadData is not { Length: >= 240 } payload)
        {
            return;
        }

        if (payload[236] != 99 || payload[237] != 130 || payload[238] != 83 || payload[239] != 99)
        {
            return;
        }

        var hardwareLength = payload[2];
        if (hardwareLength is 0 or > 16 || 28 + hardwareLength > payload.Length)
        {
            return;
        }

        var mac = BitConverter.ToString(payload, 28, Math.Min(6, (int)hardwareLength));
        var assignedAddress = string.Join('.', payload.Skip(16).Take(4));
        var requestedAddress = string.Empty;
        var hostname = string.Empty;
        var vendorClass = string.Empty;
        var serverIdentifier = string.Empty;

        var position = 240;
        while (position < payload.Length)
        {
            var option = payload[position++];
            if (option == 255)
            {
                break;
            }

            if (option == 0)
            {
                continue;
            }

            if (position >= payload.Length)
            {
                break;
            }

            var length = payload[position++];
            if (position + length > payload.Length)
            {
                break;
            }

            var value = payload.AsSpan(position, length);
            switch (option)
            {
                case 12:
                    hostname = DecodeAscii(value);
                    break;
                case 50 when length == 4:
                    requestedAddress = string.Join('.', value.ToArray());
                    break;
                case 54 when length == 4:
                    serverIdentifier = string.Join('.', value.ToArray());
                    break;
                case 60:
                    vendorClass = DecodeAscii(value);
                    break;
            }

            position += length;
        }

        var now = DateTime.UtcNow;
        var device = _devices.AddOrUpdate(
            mac,
            _ => new Device
            {
                MacAddress = mac,
                FirstSeen = now,
                LastSeen = now
            },
            (_, existing) => existing);

        lock (device)
        {
            if (!string.IsNullOrWhiteSpace(requestedAddress) && requestedAddress != "0.0.0.0")
            {
                device.IpAddresses.Add(requestedAddress);
            }

            if (!string.IsNullOrWhiteSpace(assignedAddress) && assignedAddress != "0.0.0.0")
            {
                device.IpAddresses.Add(assignedAddress);
            }

            if (!string.IsNullOrWhiteSpace(hostname))
            {
                device.Hostname = hostname;
                device.ObservedNames.Add(hostname);
            }

            if (!string.IsNullOrWhiteSpace(vendorClass))
            {
                device.DhcpVendorClass = vendorClass;
            }

            if (!string.IsNullOrWhiteSpace(serverIdentifier))
            {
                device.SeenVia.Add($"DHCP via {serverIdentifier}");
            }
            else
            {
                device.SeenVia.Add("DHCP");
            }

            device.LastSeen = now;
        }

        DeviceObserved?.Invoke(this, new DeviceObservedEventArgs(device));
    }

    private static string DecodeAscii(ReadOnlySpan<byte> value)
    {
        return System.Text.Encoding.ASCII.GetString(value).Trim('\0', ' ');
    }
}
