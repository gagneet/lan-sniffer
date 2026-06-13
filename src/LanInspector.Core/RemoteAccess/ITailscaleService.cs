namespace LanInspector.Core.RemoteAccess;

public interface ITailscaleService
{
    Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
