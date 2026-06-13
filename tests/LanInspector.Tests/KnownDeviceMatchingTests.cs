using LanInspector.Core.Configuration;
using Xunit;

namespace LanInspector.Tests;

public sealed class KnownDeviceMatchingTests
{
    [Fact]
    public void IsCritical_CriticalTag_ReturnsTrue()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "server",
            DisplayName = "Server",
            Tags = ["critical", "server"]
        };

        Assert.True(device.IsCritical);
    }

    [Fact]
    public void IsCritical_NoCriticalTag_ReturnsFalse()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "router",
            DisplayName = "Router",
            Tags = ["router"]
        };

        Assert.False(device.IsCritical);
    }

    [Fact]
    public void LoadMany_MergesById_LastWriteWins()
    {
        var json1 = """
            {
              "knownDevices": [
                { "id": "server", "displayName": "Server v1" }
              ]
            }
            """;

        var json2 = """
            {
              "knownDevices": [
                { "id": "server", "displayName": "Server v2" }
              ]
            }
            """;

        var file1 = Path.GetTempFileName();
        var file2 = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file1, json1);
            File.WriteAllText(file2, json2);

            var config = KnownDevicesConfiguration.LoadMany(file1, file2);

            Assert.Single(config.KnownDevices);
            Assert.Equal("Server v2", config.KnownDevices[0].DisplayName);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public void KnownDeviceDefinition_HasKnownTailscaleNames()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "server",
            KnownTailscaleNames = ["home-server", "homeserver"]
        };

        Assert.Equal(2, device.KnownTailscaleNames.Count);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        var config = KnownDevicesConfiguration.Load("/this/does/not/exist.json");
        Assert.Empty(config.KnownDevices);
    }
}
