using System.Diagnostics;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Platform.Windows;

public sealed class WindowsTerminalLauncher : ITerminalLauncher
{
    public bool CanLaunch => OperatingSystem.IsWindows();

    public Task<bool> LaunchSshAsync(string sshCommand, CancellationToken cancellationToken = default)
    {
        if (!CanLaunch || string.IsNullOrWhiteSpace(sshCommand))
        {
            return Task.FromResult(false);
        }

        var launched = TryStart("wt.exe", sshCommand) || TryStart("powershell.exe", $"-NoExit -Command \"{sshCommand}\"");
        return Task.FromResult(launched);
    }

    private static bool TryStart(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
