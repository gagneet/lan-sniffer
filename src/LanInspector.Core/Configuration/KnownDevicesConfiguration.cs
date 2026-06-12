using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanInspector.Core.Configuration;

public sealed class KnownDevicesConfiguration
{
    public List<KnownDeviceDefinition> KnownDevices { get; init; } = [];

    public static KnownDevicesConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            return new KnownDevicesConfiguration();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        try
        {
            return JsonSerializer.Deserialize<KnownDevicesConfiguration>(File.ReadAllText(path), options)
                ?? new KnownDevicesConfiguration();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new KnownDevicesConfiguration();
        }
    }
}

public sealed class KnownDeviceDefinition
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string DeviceType { get; init; } = "Device";

    public List<string> KnownIps { get; init; } = [];

    public List<string> KnownSubnets { get; init; } = [];

    public string? ExpectedVendor { get; init; }

    public KnownDeviceSshOptions? Ssh { get; init; }

    public List<string> Tags { get; init; } = [];

    [JsonIgnore]
    public bool IsCritical => Tags.Any(tag => string.Equals(tag, "critical", StringComparison.OrdinalIgnoreCase));
}

public sealed class KnownDeviceSshOptions
{
    public bool Enabled { get; init; }

    public string? User { get; init; }

    public int Port { get; init; } = 22;
}
