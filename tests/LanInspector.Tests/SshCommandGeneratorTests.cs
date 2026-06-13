using LanInspector.Core.Configuration;
using LanInspector.Core.RemoteAccess;
using Xunit;

namespace LanInspector.Tests;

public sealed class SshCommandGeneratorTests
{
    [Fact]
    public void Generate_Port22_OmitsPortFlag()
    {
        var result = SshCommandGenerator.Generate("gagneet", "192.168.87.243", 22);
        Assert.Equal("ssh gagneet@192.168.87.243", result);
    }

    [Fact]
    public void Generate_NonStandardPort_IncludesPortFlag()
    {
        var result = SshCommandGenerator.Generate("gagneet", "192.168.87.243", 2222);
        Assert.Equal("ssh -p 2222 gagneet@192.168.87.243", result);
    }

    [Fact]
    public void Generate_TailscaleName_ProducesCorrectCommand()
    {
        var result = SshCommandGenerator.Generate("gagneet", "home-server");
        Assert.Equal("ssh gagneet@home-server", result);
    }

    [Fact]
    public void TryGenerateForDevice_SshEnabled_ReturnsCommand()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "home-server",
            DisplayName = "Home Server",
            KnownIps = ["192.168.87.243"],
            Ssh = new KnownDeviceSshOptions { Enabled = true, User = "gagneet", Port = 22 }
        };

        var result = SshCommandGenerator.TryGenerateForDevice(device);
        Assert.Equal("ssh gagneet@192.168.87.243", result);
    }

    [Fact]
    public void TryGenerateForDevice_SshDisabled_ReturnsNull()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "router",
            DisplayName = "Router",
            KnownIps = ["192.168.0.1"],
            Ssh = new KnownDeviceSshOptions { Enabled = false }
        };

        var result = SshCommandGenerator.TryGenerateForDevice(device);
        Assert.Null(result);
    }

    [Fact]
    public void TryGenerateForDevice_PreferredHost_UsesPreferredHost()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "home-server",
            DisplayName = "Home Server",
            KnownIps = ["192.168.87.243"],
            Ssh = new KnownDeviceSshOptions { Enabled = true, User = "gagneet", Port = 22 }
        };

        var result = SshCommandGenerator.TryGenerateForDevice(device, "100.101.102.103");
        Assert.Equal("ssh gagneet@100.101.102.103", result);
    }
}
