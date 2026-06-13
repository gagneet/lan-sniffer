using LanInspector.Core.Dns;
using Xunit;

namespace LanInspector.Tests;

public sealed class DnsFilterConfigTests
{
    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        var config = DnsIntegrationsConfigLoader.Load("/this/path/does/not/exist.json");
        Assert.Null(config.AdGuardHome);
        Assert.Null(config.PiHole);
    }

    [Fact]
    public void Load_AdGuardHomeConfig_ParsesCorrectly()
    {
        var json = """
            {
              "adGuardHome": {
                "url": "http://192.168.0.1:3000",
                "username": "admin",
                "password": "secret"
              }
            }
            """;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var config = DnsIntegrationsConfigLoader.Load(path);

            Assert.NotNull(config.AdGuardHome);
            Assert.Equal("http://192.168.0.1:3000", config.AdGuardHome.Url);
            Assert.Equal("admin", config.AdGuardHome.Username);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PiHoleConfig_ParsesCorrectly()
    {
        var json = """
            {
              "piHole": {
                "url": "http://192.168.0.1",
                "apiToken": "mytoken123"
              }
            }
            """;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var config = DnsIntegrationsConfigLoader.Load(path);

            Assert.NotNull(config.PiHole);
            Assert.Equal("http://192.168.0.1", config.PiHole.Url);
            Assert.Equal("mytoken123", config.PiHole.ApiToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateService_AdGuardHomeConfig_ReturnsAdGuardService()
    {
        var config = new DnsIntegrationsConfig
        {
            AdGuardHome = new AdGuardHomeConfig { Url = "http://192.168.0.1:3000" }
        };

        var svc = DnsIntegrationsConfigLoader.CreateService(config);

        Assert.NotNull(svc);
        Assert.Equal("AdGuard Home", svc.ProviderName);
    }

    [Fact]
    public void CreateService_PiHoleConfig_ReturnsPiHoleService()
    {
        var config = new DnsIntegrationsConfig
        {
            PiHole = new PiHoleConfig { Url = "http://192.168.0.1" }
        };

        var svc = DnsIntegrationsConfigLoader.CreateService(config);

        Assert.NotNull(svc);
        Assert.Equal("Pi-hole", svc.ProviderName);
    }

    [Fact]
    public void CreateService_NoConfig_ReturnsNull()
    {
        var config = new DnsIntegrationsConfig();
        var svc = DnsIntegrationsConfigLoader.CreateService(config);
        Assert.Null(svc);
    }

    [Fact]
    public void CreateService_AdGuardHomeTakesPrecedenceOverPiHole()
    {
        var config = new DnsIntegrationsConfig
        {
            AdGuardHome = new AdGuardHomeConfig { Url = "http://192.168.0.1:3000" },
            PiHole = new PiHoleConfig { Url = "http://192.168.0.2" }
        };

        var svc = DnsIntegrationsConfigLoader.CreateService(config);

        Assert.NotNull(svc);
        Assert.Equal("AdGuard Home", svc.ProviderName);
    }

    [Fact]
    public void GetDefaultPath_Linux_ContainsConfigDir()
    {
        // On Linux, path should be under ~/.config/laninspector/
        if (!OperatingSystem.IsLinux())
            return;

        var path = DnsIntegrationsConfigLoader.GetDefaultPath();
        Assert.Contains(".config", path);
        Assert.Contains("laninspector", path);
        Assert.EndsWith("integrations.json", path);
    }
}
