using System.Diagnostics;

namespace LanInspector.UI.Services;

public sealed class WindowsTerminalLauncher
{
    public bool OpenSsh(string command)
    {
        return TryStart("wt.exe", command) || TryStart("powershell.exe", $"-NoExit -Command \"{command}\"");
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
