namespace LanInspector.Core.Identity;

public interface IDeviceNameResolver
{
    /// <summary>
    /// A friendly name for an address, or <see langword="null"/> when nothing is known about it.
    /// Called once per row on every UI refresh, so implementations must be cheap.
    /// </summary>
    string? Resolve(string ipAddress);
}
