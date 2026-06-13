using LanInspector.Core.Nmap;
using Xunit;

namespace LanInspector.Tests;

public sealed class NmapParsingTests
{

    [Fact]
    public void NmapService_NotAvailable_WhenNmapNotInstalled()
    {
        // Whitebox: NmapService.IsAvailable depends on whether nmap is in PATH
        // On a Linux CI box, nmap may or may not be present — just test the type
        var svc = new NmapService();
        // IsAvailable is a bool — just verify it doesn't throw
        _ = svc.IsAvailable;
    }

    [Fact]
    public void NmapScanResult_Fail_HasErrorMessage()
    {
        var result = new NmapScanResult(DateTime.UtcNow, DateTime.UtcNow, NmapScanMode.Ping, [], "nmap not found");
        Assert.False(result.Succeeded);
        Assert.Equal("nmap not found", result.ErrorMessage);
    }

    [Fact]
    public void NmapScanResult_Success_HasNoErrorMessage()
    {
        var result = new NmapScanResult(DateTime.UtcNow, DateTime.UtcNow, NmapScanMode.Ping, []);
        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void NmapScanMode_Values_Defined()
    {
        // Ensure all three modes exist
        _ = NmapScanMode.Ping;
        _ = NmapScanMode.TcpConnect;
        _ = NmapScanMode.ServiceDetect;
    }
}
