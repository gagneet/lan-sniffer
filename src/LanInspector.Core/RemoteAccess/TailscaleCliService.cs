using System.Net;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Core.RemoteAccess;

public sealed class TailscaleCliService : ITailscaleService
{
    private static string ExecutableName => OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";

    public async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // Whether the binary exists is decided by whether the process launched, not by what it
        // printed. Matching on the text of an error message reported a missing tailscale as
        // "installed but not connected", which sends the user to `tailscale up` when what they
        // actually need is to install it.
        var version = await ProcessHelper.TryRunAsync(ExecutableName, "version", TimeSpan.FromSeconds(5), cancellationToken);
        if (!version.Started)
        {
            return new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []);
        }

        var status = await ProcessHelper.TryRunAsync(ExecutableName, "status --json", TimeSpan.FromSeconds(10), cancellationToken);
        if (!status.Started || string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
        }

        return TailscaleStatusParser.Parse(status.StandardOutput);
    }

    public async Task<IPEndPoint?> TryGetDirectEndpointAsync(string target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        // --until-direct makes tailscale keep probing until a peer-to-peer path is up rather
        // than stopping at the first DERP-relayed reply, which carries no LAN address.
        var output = await ProcessHelper.RunAsync(
            ExecutableName,
            $"ping --c 5 --timeout 2s --until-direct \"{target}\"",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        return TailscalePingParser.TryParseDirectEndpoint(output);
    }
}
