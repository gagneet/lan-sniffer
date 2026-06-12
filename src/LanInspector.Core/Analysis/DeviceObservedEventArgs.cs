using LanInspector.Core.Model;

namespace LanInspector.Core.Analysis;

public sealed class DeviceObservedEventArgs : EventArgs
{
    public DeviceObservedEventArgs(Device device)
    {
        Device = device;
    }

    public Device Device { get; }
}
