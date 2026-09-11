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

public sealed class KnownDeviceIdentityTests
{
    private static readonly KnownDeviceDefinition HomeServer = new()
    {
        Id = "home-server",
        DisplayName = "Home Server",
        KnownMacs = ["9c:6b:00:aa:bb:cc"],
        KnownHostnames = ["ubuntu-svr"],
        KnownTailscaleNames = ["ubuntu-svr.tail7f7c1e.ts.net"]
    };

    [Theory]
    [InlineData("9C6B00AABBCC")]
    [InlineData("9c:6b:00:aa:bb:cc")]
    [InlineData("9C-6B-00-AA-BB-CC")]
    [InlineData("9c:6b:0:aa:bb:cc")]
    public void MatchesMac_AcceptsEveryNotation(string mac)
    {
        Assert.True(HomeServer.MatchesMac(mac));
    }

    [Theory]
    [InlineData("9C6B00AABBCD")]
    [InlineData("not-a-mac")]
    [InlineData("")]
    [InlineData(null)]
    public void MatchesMac_RejectsOtherDevices(string? mac)
    {
        Assert.False(HomeServer.MatchesMac(mac));
    }

    [Theory]
    [InlineData("ubuntu-svr")]
    [InlineData("UBUNTU-SVR")]
    [InlineData("ubuntu-svr.local")]
    [InlineData("ubuntu-svr.tail7f7c1e.ts.net")]
    [InlineData("ubuntu-svr.tail7f7c1e.ts.net.")]
    public void MatchesHostname_MatchesShortAndQualifiedForms(string hostname)
    {
        Assert.True(HomeServer.MatchesHostname(hostname));
    }

    [Theory]
    [InlineData("other-host")]
    [InlineData("")]
    [InlineData(null)]
    public void MatchesHostname_RejectsOtherNames(string? hostname)
    {
        Assert.False(HomeServer.MatchesHostname(hostname));
    }

    [Fact]
    public void NormalisedMacs_DropsUnparseableEntries()
    {
        var device = new KnownDeviceDefinition { Id = "x", KnownMacs = ["9c:6b:00:aa:bb:cc", "junk", ""] };

        Assert.Equal(["9C6B00AABBCC"], device.NormalisedMacs);
    }
}
