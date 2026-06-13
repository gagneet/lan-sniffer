using LanInspector.Core.Configuration;

namespace LanInspector.Core.RemoteAccess;

public static class SshCommandGenerator
{
    public static string Generate(string user, string host, int port = 22)
    {
        var userAtHost = string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}";
        return port == 22 ? $"ssh {userAtHost}" : $"ssh -p {port} {userAtHost}";
    }

    public static string? TryGenerateForDevice(KnownDeviceDefinition device, string? preferredHost = null)
    {
        var ssh = device.Ssh;
        if (ssh?.Enabled != true || string.IsNullOrWhiteSpace(ssh.User))
        {
            return null;
        }

        var host = preferredHost ?? device.KnownIps.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        return Generate(ssh.User, host, ssh.Port);
    }
}
