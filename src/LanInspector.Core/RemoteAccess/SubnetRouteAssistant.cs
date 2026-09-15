using LanInspector.Core.Configuration;

namespace LanInspector.Core.RemoteAccess;

public static class SubnetRouteAssistant
{
    private const string AdminConsoleNote =
        "After running the command, approve the advertised subnet route in the Tailscale admin console.";

    private const string Explanation =
        "Run this on an always-on device that is on the target subnet; on Linux it also needs IP forwarding enabled. " +
        "Then use Tailscale from any device to access the subnet remotely.";

    // 'tailscale set' changes only the setting it names. 'tailscale up' given one flag refuses to
    // run unless every other non-default setting is restated alongside it.
    private const string CommandPrefix = "sudo tailscale set --advertise-routes=";

    public static string? BuildCommand(KnownDeviceDefinition device)
    {
        if (device.KnownSubnets.Count > 0)
        {
            return CommandPrefix + string.Join(",", device.KnownSubnets);
        }

        if (device.KnownIps.Count > 0)
        {
            var subnet = InferSubnet(device.KnownIps[0]);
            if (subnet is not null)
            {
                return CommandPrefix + subnet;
            }
        }

        return null;
    }

    public static SubnetRouteSuggestion BuildSuggestion(params string[] subnets)
    {
        return new SubnetRouteSuggestion(
            CommandPrefix + string.Join(",", subnets),
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
