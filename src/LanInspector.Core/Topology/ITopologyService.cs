namespace LanInspector.Core.Topology;

public interface ITopologyService
{
    Task<NetworkTopologySnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default);
}
