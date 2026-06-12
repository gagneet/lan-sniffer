namespace LanInspector.Core.Analysis;

public interface IDeviceObservingAnalyzer : IPacketAnalyzer
{
    event EventHandler<DeviceObservedEventArgs>? DeviceObserved;
}
