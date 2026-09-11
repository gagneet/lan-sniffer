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

    /// <summary>
    /// Writes the configuration to <paramref name="path"/>, creating the directory if needed.
    /// </summary>
    /// <remarks>
    /// Written through a temporary file and moved into place, so an interrupted save cannot leave
    /// a half-written file where the device list used to be.
    /// </remarks>
    public static void Save(string path, KnownDevicesConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        });

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>
    /// Where user edits are saved: per-user configuration, never the copy shipped with the
    /// application, which an update would overwrite.
    /// </summary>
    public static string GetUserConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "LanInspector", "known-devices.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "laninspector", "known-devices.json");
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

    public List<string> KnownTailscaleNames { get; init; } = [];

    /// <summary>
    /// MAC addresses belonging to this device, in any common notation. A MAC survives DHCP
    /// lease changes, so it is the most reliable way to re-find a device that moved IP.
    /// </summary>
    public List<string> KnownMacs { get; init; } = [];

    /// <summary>
    /// Hostnames this device answers to (DNS, MagicDNS or mDNS). Used as a fallback when the
    /// device is on a different layer-2 segment and its MAC is therefore not in the ARP table.
    /// </summary>
    public List<string> KnownHostnames { get; init; } = [];

    public string? ExpectedVendor { get; init; }

    public KnownDeviceSshOptions? Ssh { get; init; }

    public List<string> Tags { get; init; } = [];

    [JsonIgnore]
    public bool IsCritical => Tags.Any(tag => string.Equals(tag, "critical", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normalised (hex-only, upper-case) forms of <see cref="KnownMacs"/> for comparison.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> NormalisedMacs =>
        KnownMacs.Select(MacAddressFormatter.Normalise)
            .Where(mac => mac.Length == 12)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool MatchesMac(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return false;
        }

        var normalised = MacAddressFormatter.Normalise(macAddress);
        return normalised.Length == 12 && NormalisedMacs.Contains(normalised);
    }

    public bool MatchesHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        var trimmed = hostname.Trim().TrimEnd('.');
        var shortName = trimmed.Split('.', 2)[0];

        return KnownHostnames.Concat(KnownTailscaleNames).Any(candidate =>
        {
            var candidateTrimmed = candidate.Trim().TrimEnd('.');
            return string.Equals(candidateTrimmed, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidateTrimmed.Split('.', 2)[0], shortName, StringComparison.OrdinalIgnoreCase);
        });
    }
}

public sealed class KnownDeviceSshOptions
{
    public bool Enabled { get; init; }

    public string? User { get; init; }

    public int Port { get; init; } = 22;
}
