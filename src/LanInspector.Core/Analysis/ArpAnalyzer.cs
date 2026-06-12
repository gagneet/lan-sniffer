using System.Collections.Concurrent;
using LanInspector.Core.Model;
using PacketDotNet;

namespace LanInspector.Core.Analysis;

public sealed class ArpAnalyzer : IDeviceObservingAnalyzer
{
    private readonly ConcurrentDictionary<string, Device> _devices;

    public ArpAnalyzer(ConcurrentDictionary<string, Device> devices)
    {
        _devices = devices;
    }

    public event EventHandler<DeviceObservedEventArgs>? DeviceObserved;

    public IReadOnlyCollection<Device> Devices => _devices.Values.ToArray();

    public void Analyze(Packet packet)
    {
        var arp = packet.Extract<ArpPacket>();
        if (arp is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var mac = arp.SenderHardwareAddress.ToString();
        var ip = arp.SenderProtocolAddress.ToString();

        var device = _devices.AddOrUpdate(
            mac,
            _ =>
            {
                var created = new Device
                {
                    MacAddress = mac,
                    FirstSeen = now,
                    LastSeen = now
                };

                created.IpAddresses.Add(ip);
                created.SeenVia.Add("ARP");
                return created;
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.IpAddresses.Add(ip);
                    existing.SeenVia.Add("ARP");
                    existing.LastSeen = now;
                }

                return existing;
            });

        DeviceObserved?.Invoke(this, new DeviceObservedEventArgs(device));
    }
}
