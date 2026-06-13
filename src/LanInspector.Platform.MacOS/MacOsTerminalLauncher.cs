using System.Diagnostics;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Platform.MacOS;

public sealed class MacOsTerminalLauncher : ITerminalLauncher
{
    public bool CanLaunch => OperatingSystem.IsMacOS();

    public Task<bool> LaunchSshAsync(string sshCommand, CancellationToken cancellationToken = default)
    {
        if (!CanLaunch || string.IsNullOrWhiteSpace(sshCommand))
        {
            return Task.FromResult(false);
        }

        // Open Terminal.app — user sees the window; they paste or retype the command.
        // A future iteration can use osascript to pre-fill the command.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "-a Terminal",
                UseShellExecute = false
            });
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
