using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LanInspector.UI.Services;

public sealed class WindowsTerminalLauncher
{
    private static readonly Regex SshCommandPattern = new(
        @"^ssh(?:\s+-p\s+(?<port>\d{1,5}))?\s+(?:(?<user>[A-Za-z0-9._-]+)@)?(?<host>[A-Za-z0-9.:-]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public bool OpenSsh(string command)
    {
        if (!TryParseSshCommand(command, out var arguments))
        {
            return false;
        }

        return TryStart("wt.exe", arguments) || TryStart("powershell.exe", $"-NoExit -Command \"{arguments}\"");
    }

    private static bool TryParseSshCommand(string command, out string arguments)
    {
        arguments = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var match = SshCommandPattern.Match(command.Trim());
        if (!match.Success)
        {
            return false;
        }

        var user = match.Groups["user"].Value;
        var host = match.Groups["host"].Value;
        var port = match.Groups["port"].Value;

        if (!IsValidPort(port))
        {
            return false;
        }

        var target = string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}";
        arguments = string.IsNullOrWhiteSpace(port) ? $"ssh {target}" : $"ssh -p {port} {target}";
        return true;
    }

    private static bool IsValidPort(string port)
    {
        return string.IsNullOrWhiteSpace(port) || (int.TryParse(port, out var parsed) && parsed is >= 1 and <= 65535);
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
