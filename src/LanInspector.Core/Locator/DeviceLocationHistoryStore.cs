using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanInspector.Core.Locator;

public sealed class DeviceLocationRecord
{
    public string? LastAddress { get; set; }

    public string? PreviousAddress { get; set; }

    public string? Source { get; set; }

    public DateTimeOffset? ObservedAt { get; set; }

    /// <summary>When <see cref="LastAddress"/> first differed from <see cref="PreviousAddress"/>.</summary>
    public DateTimeOffset? ChangedAt { get; set; }
}

public sealed class DeviceLocationHistory
{
    public Dictionary<string, DeviceLocationRecord> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Remembers where each known device was last found, so the app can report "the server moved from
/// 192.168.0.148 to 192.168.0.154 at 09:14" instead of silently showing a different number, and so
/// a previously-good address can be re-probed first after a reboot.
/// </summary>
/// <remarks>
/// Failures to read or write the history file are swallowed: a missing or unwritable history makes
/// locating less informative, but it must never stop a lookup from running.
/// </remarks>
public sealed class DeviceLocationHistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly object _gate = new();

    public DeviceLocationHistoryStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    public string Path => _path;

    public static string GetDefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "LanInspector", "device-locations.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(home, ".config", "laninspector", "device-locations.json");
    }

    public DeviceLocationHistory Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public DeviceLocationRecord? Get(string deviceId)
    {
        return Load().Devices.GetValueOrDefault(deviceId);
    }

    /// <summary>
    /// Records the address a device was found at and returns the address it was previously at,
    /// together with the time the change was first seen. Re-recording an unchanged address keeps
    /// the original change timestamp rather than resetting it on every poll.
    /// </summary>
    public DeviceLocationRecord Record(string deviceId, IPAddress address, LocationSource source, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            var history = LoadCore();
            var addressText = address.ToString();

            if (!history.Devices.TryGetValue(deviceId, out var record))
            {
                record = new DeviceLocationRecord();
                history.Devices[deviceId] = record;
            }

            if (!string.Equals(record.LastAddress, addressText, StringComparison.OrdinalIgnoreCase))
            {
                record.PreviousAddress = record.LastAddress;
                record.LastAddress = addressText;
                record.ChangedAt = observedAt;
            }

            record.Source = source.ToString();
            record.ObservedAt = observedAt;

            Save(history);
            return record;
        }
    }

    private DeviceLocationHistory LoadCore()
    {
        if (!File.Exists(_path))
        {
            return new DeviceLocationHistory();
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceLocationHistory>(File.ReadAllText(_path), SerializerOptions)
                ?? new DeviceLocationHistory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DeviceLocationHistory();
        }
    }

    private void Save(DeviceLocationHistory history)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(history, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A read-only or unavailable profile directory must not break locating.
        }
    }
}
