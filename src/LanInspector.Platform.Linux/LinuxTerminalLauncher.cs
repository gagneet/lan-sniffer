using System.Diagnostics;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Platform.Linux;

public sealed class LinuxTerminalLauncher : ITerminalLauncher
{
    private static readonly string[] TerminalCandidates =
    [
        "x-terminal-emulator",
        "gnome-terminal",
        "konsole",
        "xterm"
    ];

    public bool CanLaunch => OperatingSystem.IsLinux() && HasDisplay();

    public Task<bool> LaunchSshAsync(string sshCommand, CancellationToken cancellationToken = default)
    {
        if (!CanLaunch || string.IsNullOrWhiteSpace(sshCommand))
        {
            return Task.FromResult(false);
        }

        foreach (var terminal in TerminalCandidates)
        {
            if (TryStartWithTerminal(terminal, sshCommand))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private static bool HasDisplay() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static bool TryStartWithTerminal(string terminal, string command)
    {
        try
        {
            var args = terminal switch
            {
                "gnome-terminal" => $"-- {command}",
                "konsole" => $"-e {command}",
                _ => $"-e {command}"
            };

            Process.Start(new ProcessStartInfo
            {
                FileName = terminal,
                Arguments = args,
                UseShellExecute = false
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
