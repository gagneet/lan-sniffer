using PacketDotNet;
using SharpPcap;

namespace LanInspector.Core.Capture;

public sealed class PcapCaptureProvider : ICaptureProvider
{
    private ICaptureDevice? _device;
    private bool _disposed;

    public event EventHandler<PacketCapturedEventArgs>? PacketCaptured;

    public IReadOnlyList<CaptureDeviceInfo> GetDevices()
    {
        return CaptureDeviceList.Instance
            .Select(device => new CaptureDeviceInfo(
                device.Name,
                string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description,
                Array.Empty<string>()))
            .ToArray();
    }

    public void Start(string deviceName, string filter)
    {
        ThrowIfDisposed();
        Stop();

        var devices = CaptureDeviceList.Instance;
        _device = devices.FirstOrDefault(device => device.Name == deviceName)
            ?? throw new InvalidOperationException($"Capture device '{deviceName}' was not found.");

        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(DeviceModes.Promiscuous, read_timeout: 1000);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            _device.Filter = filter;
        }

        _device.StartCapture();
    }

    public void Stop()
    {
        if (_device is null)
        {
            return;
        }

        _device.OnPacketArrival -= OnPacketArrival;

        if (_device.Started)
        {
            _device.StopCapture();
        }

        _device.Close();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        var raw = capture.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        PacketCaptured?.Invoke(this, new PacketCapturedEventArgs(raw, packet));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
