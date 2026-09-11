using LanInspector.Core.Configuration;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// Round-trips through the Settings grid's flattened representation.
/// </summary>
public sealed class KnownDeviceEditingTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = new KnownDeviceDefinition
        {
            Id = "home-server",
            DisplayName = "Home Server",
            DeviceType = "Server",
            KnownIps = ["192.168.0.148", "192.168.0.149"],
            KnownSubnets = ["192.168.0.0/24"],
            KnownMacs = ["68:1d:ef:3c:d5:45"],
            KnownHostnames = ["ubuntu-svr"],
            KnownTailscaleNames = ["ubuntu-svr"],
            Ssh = new KnownDeviceSshOptions { Enabled = true, User = "someone", Port = 2222 },
            Tags = ["critical", "server"]
        };

        var result = KnownDeviceDraft.From(original).ToDefinition();

        Assert.Equal("home-server", result.Id);
        Assert.Equal("Home Server", result.DisplayName);
        Assert.Equal(original.KnownIps, result.KnownIps);
        Assert.Equal(original.KnownSubnets, result.KnownSubnets);
        Assert.Equal(original.KnownMacs, result.KnownMacs);
        Assert.Equal(original.KnownHostnames, result.KnownHostnames);
        Assert.Equal(original.KnownTailscaleNames, result.KnownTailscaleNames);
        Assert.True(result.Ssh!.Enabled);
        Assert.Equal("someone", result.Ssh.User);
        Assert.Equal(2222, result.Ssh.Port);
        Assert.True(result.IsCritical);
        Assert.Contains("server", result.Tags);
    }

    [Fact]
    public void ToDefinition_CriticalCheckbox_BecomesATag()
    {
        var edit = new KnownDeviceDraft { DisplayName = "Server", IsCritical = true, Tags = "server" };

        var result = edit.ToDefinition();

        Assert.True(result.IsCritical);
        // Not duplicated into the visible tag list the user typed.
        Assert.Equal(["critical", "server"], result.Tags);
    }

    [Fact]
    public void From_CriticalTag_IsLiftedOutOfTheEditableTagList()
    {
        var definition = new KnownDeviceDefinition { Id = "x", DisplayName = "X", Tags = ["critical", "server"] };

        var edit = KnownDeviceDraft.From(definition);

        Assert.True(edit.IsCritical);
        Assert.Equal("server", edit.Tags);
    }

    [Fact]
    public void ToDefinition_MissingId_IsDerivedFromTheDisplayName()
    {
        var edit = new KnownDeviceDraft { DisplayName = "My NAS Box" };

        Assert.Equal("my-nas-box", edit.ToDefinition().Id);
    }

    [Theory]
    [InlineData("a, b,c ;d", new[] { "a", "b", "c", "d" })]
    [InlineData("  ", new string[0])]
    [InlineData("", new string[0])]
    public void ToDefinition_ListFields_AcceptCommasSemicolonsAndStrayWhitespace(string input, string[] expected)
    {
        var edit = new KnownDeviceDraft { DisplayName = "X", KnownIps = input };

        Assert.Equal(expected, edit.ToDefinition().KnownIps);
    }

    [Fact]
    public void ToDefinition_NoSshDetails_LeavesTheSshBlockOut()
    {
        var edit = new KnownDeviceDraft { DisplayName = "Printer" };

        Assert.Null(edit.ToDefinition().Ssh);
    }

    [Fact]
    public void ToDefinition_ZeroPort_FallsBackToTwentyTwo()
    {
        var edit = new KnownDeviceDraft { DisplayName = "X", SshEnabled = true, SshUser = "me", SshPort = 0 };

        Assert.Equal(22, edit.ToDefinition().Ssh!.Port);
    }

    [Fact]
    public void IsUsable_RequiresANameOrAnId()
    {
        Assert.False(new KnownDeviceDraft().IsUsable);
        Assert.True(new KnownDeviceDraft { DisplayName = "X" }.IsUsable);
        Assert.True(new KnownDeviceDraft { Id = "x" }.IsUsable);
    }

    [Fact]
    public void SaveThenLoad_ReturnsTheSameDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-settings-{Guid.NewGuid():N}.json");
        try
        {
            var configuration = new KnownDevicesConfiguration
            {
                KnownDevices =
                [
                    new KnownDeviceDefinition
                    {
                        Id = "home-server",
                        DisplayName = "Home Server",
                        KnownMacs = ["68:1d:ef:3c:d5:45"],
                        Tags = ["critical"]
                    }
                ]
            };

            KnownDevicesConfiguration.Save(path, configuration);
            var reloaded = KnownDevicesConfiguration.Load(path);

            var device = Assert.Single(reloaded.KnownDevices);
            Assert.Equal("Home Server", device.DisplayName);
            Assert.Equal(["68:1d:ef:3c:d5:45"], device.KnownMacs);
            Assert.True(device.IsCritical);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_OverwritesAnExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-settings-{Guid.NewGuid():N}.json");
        try
        {
            KnownDevicesConfiguration.Save(path, new KnownDevicesConfiguration
            {
                KnownDevices = [new KnownDeviceDefinition { Id = "a", DisplayName = "A" }]
            });

            KnownDevicesConfiguration.Save(path, new KnownDevicesConfiguration
            {
                KnownDevices = [new KnownDeviceDefinition { Id = "b", DisplayName = "B" }]
            });

            Assert.Equal("B", Assert.Single(KnownDevicesConfiguration.Load(path).KnownDevices).DisplayName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
