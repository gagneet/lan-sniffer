using System.Net;

namespace LanInspector.Core.RemoteAccess;

public interface ITailscaleService
{
    Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks Tailscale to establish a direct path to <paramref name="target"/> and reports the
    /// endpoint it used. On a peer that shares a LAN with this machine, that endpoint is the
    /// peer's current LAN address — which is how a DHCP-reassigned server can be re-found
    /// without scanning the subnet.
    /// </summary>
    /// <returns>The direct endpoint, or <see langword="null"/> if the peer is unreachable or
    /// only reachable through a DERP relay.</returns>
    Task<IPEndPoint?> TryGetDirectEndpointAsync(string target, CancellationToken cancellationToken = default);
}
