using LanInspector.Core.Diagnostics;

namespace LanInspector.Platform.MacOS;

public sealed class MacOsCapturePrerequisiteService : ICapturePrerequisiteService
{
    public Task<CapturePrerequisiteStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        // BPF devices on macOS are at /dev/bpf*
        var bpf0 = "/dev/bpf0";
        if (!File.Exists(bpf0))
        {
            return Task.FromResult(new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.Unavailable,
                "BPF devices (/dev/bpf*) are not available.",
                "Packet capture may require macOS permissions or admin access."));
        }

        // Check if current process can read the BPF device
        try
        {
            using var f = File.Open(bpf0, FileMode.Open, FileAccess.Read);
            return Task.FromResult(new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.Available,
                "BPF access is available. Packet capture should work."));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.InsufficientPermission,
                "BPF device access denied.",
                "Run with sudo or adjust BPF device permissions:\n  sudo chmod a+r /dev/bpf*"));
        }
        catch
        {
            return Task.FromResult(new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.InsufficientPermission,
                "Cannot determine BPF access. Route and SSH features still work without packet capture.",
                "Run with sudo for packet capture."));
        }
    }
}
