namespace LanInspector.Core.RemoteAccess;

public enum ConnectionMethod
{
    DirectLanIp,
    TailscaleIp,
    TailscaleName,
    PortForward,
    ManualNetworkSwitch,
    Unavailable
}
