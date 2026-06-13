using LanInspector.Core.Diagnostics;

namespace LanInspector.Platform.Linux;

public sealed class LinuxCapturePrerequisiteService : ICapturePrerequisiteService
{
    public async Task<CapturePrerequisiteStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!LibPcapExists())
        {
            return new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.MissingDriver,
                "libpcap is not installed.",
                "Install with: sudo apt-get install libpcap-dev");
        }

        if (IsEffectiveRoot())
        {
            return new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.Available,
                "Running as root. Packet capture is available.");
        }

        var exePath = Environment.ProcessPath ?? string.Empty;
        var capOutput = await ProcessHelper.RunAsync("getcap", exePath, TimeSpan.FromSeconds(3), cancellationToken);
        if (!string.IsNullOrWhiteSpace(capOutput) && capOutput.Contains("cap_net_raw"))
        {
            return new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.Available,
                "Packet capture capability (cap_net_raw) is set on the executable.");
        }

        return new CapturePrerequisiteStatus(
            CapturePrerequisiteKind.InsufficientPermission,
            "Packet capture requires elevated privileges.",
            string.IsNullOrWhiteSpace(exePath)
                ? "Run as root or grant cap_net_raw to the executable."
                : $"Run as root, or grant capability:\n  sudo setcap cap_net_raw,cap_net_admin=eip \"{exePath}\"");
    }

    private static bool LibPcapExists()
    {
        var searchPaths = new[]
        {
            "/usr/lib/libpcap.so",
            "/usr/lib/x86_64-linux-gnu/libpcap.so.0.8",
            "/usr/lib/aarch64-linux-gnu/libpcap.so",
            "/lib/libpcap.so"
        };

        if (searchPaths.Any(File.Exists))
        {
            return true;
        }

        try
        {
            return Directory.GetFiles("/usr/lib", "libpcap.so*", SearchOption.AllDirectories).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEffectiveRoot()
    {
        try
        {
            var status = File.ReadAllText("/proc/self/status");
            foreach (var line in status.Split('\n'))
            {
                if (!line.StartsWith("Euid:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 && parts[1] == "0";
            }
        }
        catch
        {
            // Not on Linux or /proc not available.
        }

        return false;
    }
}
