using System.Net;
using System.Text.RegularExpressions;

namespace LanInspector.Core.RemoteAccess;

/// <summary>
/// Reads the path information out of <c>tailscale ping</c> output.
/// </summary>
/// <remarks>
/// A direct (peer-to-peer) reply names the address the packets took, which on a home LAN is the
/// peer's current DHCP address:
/// <code>pong from ubuntu-svr (100.83.183.74) via 192.168.0.154:41641 in 3ms</code>
/// A relayed reply names a DERP region instead, and carries no LAN information:
/// <code>pong from ubuntu-svr (100.83.183.74) via DERP(syd) in 42ms</code>
/// </remarks>
public static partial class TailscalePingParser
{
    [GeneratedRegex(@"\bvia\s+(?<endpoint>\[[0-9a-fA-F:]+\]:\d+|\d{1,3}(?:\.\d{1,3}){3}:\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectEndpointRegex();

    [GeneratedRegex(@"\bvia\s+DERP\((?<region>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex DerpRelayRegex();

    /// <summary>
    /// Returns the direct endpoint from the first <c>pong ... via &lt;ip:port&gt;</c> line, or
    /// <see langword="null"/> when every reply was relayed through DERP, or nothing replied.
    /// </summary>
    public static IPEndPoint? TryParseDirectEndpoint(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("pong", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = DirectEndpointRegex().Match(line);
            if (match.Success && IPEndPoint.TryParse(match.Groups["endpoint"].Value, out var endpoint))
            {
                return endpoint;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the peer answered but only over a DERP relay, meaning no direct path — and so
    /// no LAN address — could be established.
    /// </summary>
    public static bool IsRelayedOnly(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return TryParseDirectEndpoint(output) is null && DerpRelayRegex().IsMatch(output);
    }
}
