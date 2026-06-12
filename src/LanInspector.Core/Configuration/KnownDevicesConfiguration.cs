using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanInspector.Core.Configuration;

public sealed class KnownDevicesConfiguration
{
    public List<KnownDeviceDefinition> KnownDevices { get; init; } = [];

    public static KnownDevicesConfiguration LoadMany(params string[] paths)
    {
        var merged = new KnownDevicesConfiguration();
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var configuration = Load(path);
            foreach (var knownDevice in configuration.KnownDevices)
            {
                if (string.IsNullOrWhiteSpace(knownDevice.Id))
                {
                    merged.KnownDevices.Add(knownDevice);
                    continue;
                }

                if (indexById.TryGetValue(knownDevice.Id, out var existingIndex))
                {
                    merged.KnownDevices[existingIndex] = knownDevice;
                    continue;
                }

                indexById[knownDevice.Id] = merged.KnownDevices.Count;
                merged.KnownDevices.Add(knownDevice);
            }
        }

        return merged;
    }

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
