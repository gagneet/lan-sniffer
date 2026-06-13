using System.Text.Json;

namespace LanInspector.Core.Dns;

public static class DnsIntegrationsConfigLoader
{
    public static DnsIntegrationsConfig Load(string? path = null)
    {
        path ??= GetDefaultPath();

        if (!File.Exists(path))
            return new DnsIntegrationsConfig();

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            return JsonSerializer.Deserialize<DnsIntegrationsConfig>(File.ReadAllText(path), options)
                ?? new DnsIntegrationsConfig();
        }
        catch
        {
            return new DnsIntegrationsConfig();
        }
    }

    public static string GetDefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "LanInspector", "integrations.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "laninspector", "integrations.json");
    }

    public static IDnsFilterService? CreateService(DnsIntegrationsConfig config)
    {
        if (config.AdGuardHome is not null)
            return new AdGuardHomeDnsService(config.AdGuardHome);

        if (config.PiHole is not null)
            return new PiHoleDnsService(config.PiHole);

        return null;
    }
}
