using System.Text.RegularExpressions;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Identity;

namespace LanInspector.Core.Locator;

/// <param name="Report">What the device said, or null when it could not be asked.</param>
/// <param name="Failure">Why it could not be asked, phrased to follow "could not ask the device: ".</param>
public sealed record DeviceNetworkInspection(DeviceNetworkReport? Report, string? Failure)
{
    public static DeviceNetworkInspection Failed(string failure) => new(null, failure);
}

public interface IDeviceNetworkInspector
{
    /// <param name="hostIsAuthenticated">
    /// True when <paramref name="host"/> is an address whose identity is already guaranteed, such as
    /// a Tailscale address, which only the peer holding its WireGuard key can answer on. Only then is
    /// a host key never seen before accepted.
    /// </param>
    Task<DeviceNetworkInspection> InspectAsync(
        string user,
        string host,
        int port,
        bool hostIsAuthenticated,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Logs in to a device with the system's own <c>ssh</c> client and asks how it is connected.
/// </summary>
/// <remarks>
/// <para>
/// Keys only. <c>BatchMode</c> turns every password or passphrase prompt into a failure, so a check
/// never hangs waiting for input nobody will type, and no secret passes through this application.
/// </para>
/// <para>
/// The script goes to <c>sh -s</c> on standard input rather than as an argument. As an argument it
/// would cross two sets of quoting rules — Windows command-line parsing on this side, the remote
/// shell on the other — that no single escaping satisfies.
/// </para>
/// </remarks>
public sealed partial class SshNetworkInspector : IDeviceNetworkInspector
{
    // Only what is needed leaves the device: the Tailscale preferences are cut down to the
    // advertised routes on the device itself, since the full dump carries far more than that.
    // The trace target is any address beyond the site; only the private hops before it are kept.
    internal const string Script = """
        echo '### os'; uname -s
        echo '### host'; hostname
        if [ "$(uname -s)" = Linux ]; then
          echo '### ip-addr'; ip -o addr show
          echo '### ip-link'; ip -o link show
          echo '### ip-route'; ip -4 route show default
          echo '### neigh'; ip -4 neigh show
        else
          echo '### ifconfig'; ifconfig
          echo '### route-get'; route -n get default
          echo '### neigh'; arp -an
        fi
        echo '### trace'
        if command -v traceroute >/dev/null 2>&1; then
          traceroute -n -m 4 -w 1 -q 1 1.1.1.1 2>&1
        elif command -v tracepath >/dev/null 2>&1; then
          tracepath -n -m 4 1.1.1.1 2>&1
        fi
        echo '### tailscale-prefs'
        ts=$(command -v tailscale || echo /Applications/Tailscale.app/Contents/MacOS/Tailscale)
        if [ -x "$ts" ]; then
          "$ts" debug prefs 2>/dev/null | awk '/"AdvertiseRoutes"/ { found = 1 } found { print } found && /\]|null/ { exit }'
        fi
        exit 0
        """;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    private readonly Func<string, string, TimeSpan, CancellationToken, string?, Task<ProcessResult>> _runProcess;
    private readonly OuiVendorLookup? _vendors;

    public SshNetworkInspector(OuiVendorLookup? vendors = null)
        : this(ProcessHelper.TryRunAsync, vendors)
    {
    }

    internal SshNetworkInspector(
        Func<string, string, TimeSpan, CancellationToken, string?, Task<ProcessResult>> runProcess,
        OuiVendorLookup? vendors = null)
    {
        _runProcess = runProcess;
        _vendors = vendors;
    }

    public async Task<DeviceNetworkInspection> InspectAsync(
        string user,
        string host,
        int port,
        bool hostIsAuthenticated,
        CancellationToken cancellationToken = default)
    {
        // Both values land on a command line. No login name, hostname or address needs characters
        // outside these, and a leading '-' would be read by ssh as an option.
        if (!SafeTokenRegex().IsMatch(user) || !SafeTokenRegex().IsMatch(host) || port is < 1 or > 65535)
        {
            return DeviceNetworkInspection.Failed($"'{user}@{host}:{port}' is not a usable SSH destination.");
        }

        var hostKeyPolicy = hostIsAuthenticated ? "accept-new" : "yes";
        var arguments = $"-o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking={hostKeyPolicy} -p {port} {user}@{host} sh -s";

        // A source file checked out with CRLF endings would otherwise hand the shell "uname -s\r".
        var result = await _runProcess("ssh", arguments, Timeout, cancellationToken, Script.ReplaceLineEndings("\n"));

        if (!result.Started)
        {
            return DeviceNetworkInspection.Failed(OperatingSystem.IsWindows()
                ? "no ssh client was found. Install the \"OpenSSH Client\" optional feature in Windows Settings."
                : "no ssh client was found on PATH.");
        }

        if (result.TimedOut)
        {
            return DeviceNetworkInspection.Failed($"SSH to {host} timed out.");
        }

        // A late command can fail (no traceroute, no Tailscale) without the rest being wrong, so
        // success is judged by whether the output arrived, not by the exit code.
        if (result.StandardOutput.Contains(DeviceNetworkReportParser.SectionMarker + "os", StringComparison.Ordinal))
        {
            var report = DeviceNetworkReportParser.Parse(result.StandardOutput);
            var vendor = report.GatewayMac is null ? null : _vendors?.LookupVendor(report.GatewayMac);
            return new DeviceNetworkInspection(vendor is null ? report : report with { GatewayVendor = vendor }, null);
        }

        return DeviceNetworkInspection.Failed(DescribeFailure(result.StandardError, host, port));
    }

    internal static string DescribeFailure(string standardError, string host, int port)
    {
        if (standardError.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"{host} did not accept an SSH key (passwords are never tried). Add your public key to ~/.ssh/authorized_keys on the device, or load it into ssh-agent.";
        }

        if (standardError.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
        {
            return $"the host key for {host} is not trusted. Connect once with ssh yourself to check it and accept it.";
        }

        if (standardError.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return $"nothing accepts SSH connections on {host} port {port}.";
        }

        return standardError.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
            ?? $"ssh to {host} failed without saying why.";
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:%-]*$")]
    private static partial Regex SafeTokenRegex();
}
