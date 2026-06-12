using PacketDotNet;
using SharpPcap;

namespace LanInspector.Core.Capture;

public interface ICaptureProvider : IDisposable
{
    event EventHandler<PacketCapturedEventArgs>? PacketCaptured;

    IReadOnlyList<CaptureDeviceInfo> GetDevices();

    void Start(string deviceName, string filter);

    void Stop();
}

public sealed class PacketCapturedEventArgs : EventArgs
{
    public PacketCapturedEventArgs(RawCapture rawCapture, Packet parsedPacket)
    {
        RawCapture = rawCapture;
        ParsedPacket = parsedPacket;
    }

    public RawCapture RawCapture { get; }

    public Packet ParsedPacket { get; }
}
