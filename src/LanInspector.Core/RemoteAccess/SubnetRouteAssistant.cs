using LanInspector.Core.Configuration;

namespace LanInspector.Core.RemoteAccess;

public static class SubnetRouteAssistant
{
    private const string AdminConsoleNote =
        "After running the command, approve the advertised subnet route in the Tailscale admin console.";

    private const string Explanation =
        "Run this on an always-on Linux server or gateway device that can reach the target subnet. " +
        "Then use Tailscale from any device to access the server or subnet remotely.";

    public static string? BuildCommand(KnownDeviceDefinition device)
    {
        if (device.KnownSubnets.Count > 0)
        {
            return $"sudo tailscale up --advertise-routes={string.Join(",", device.KnownSubnets)}";
        }

        if (device.KnownIps.Count > 0)
        {
            var subnet = InferSubnet(device.KnownIps[0]);
            if (subnet is not null)
            {
                return $"sudo tailscale up --advertise-routes={subnet}";
            }
        }

        return null;
    }

    public static SubnetRouteSuggestion BuildSuggestion(params string[] subnets)
    {
        var routeArg = string.Join(",", subnets);
        return new SubnetRouteSuggestion(
            $"sudo tailscale up --advertise-routes={routeArg}",
            Explanation,
            AdminConsoleNote);
    }

    private static string? InferSubnet(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4)
        {
            return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
        }

        return null;
    }
}
