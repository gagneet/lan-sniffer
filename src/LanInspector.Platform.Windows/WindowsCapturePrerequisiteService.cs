using LanInspector.Core.Diagnostics;

namespace LanInspector.Platform.Windows;

public sealed class WindowsCapturePrerequisiteService : ICapturePrerequisiteService
{
    public Task<CapturePrerequisiteStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var npcapDll = Path.Combine(system32, "Npcap", "wpcap.dll");
        var winpcapDll = Path.Combine(system32, "wpcap.dll");

        if (File.Exists(npcapDll))
        {
            return Task.FromResult(new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.Available,
                "Npcap is installed."));
        }

        if (File.Exists(winpcapDll))
        {
            return Task.FromResult(new CapturePrerequisiteStatus(
                CapturePrerequisiteKind.Available,
                "WinPcap is installed. Consider upgrading to Npcap for better compatibility."));
        }

        return Task.FromResult(new CapturePrerequisiteStatus(
            CapturePrerequisiteKind.MissingDriver,
            "Npcap is not installed.",
            "Download and install Npcap from https://npcap.com/"));
    }
}
