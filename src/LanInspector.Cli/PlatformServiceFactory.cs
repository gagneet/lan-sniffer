using LanInspector.Core.Diagnostics;
using LanInspector.Core.RemoteAccess;
using LanInspector.Platform.Linux;
using LanInspector.Platform.MacOS;
using LanInspector.Platform.Windows;

namespace LanInspector.Cli;

internal static class PlatformServiceFactory
{
    public static IRouteDiagnosticsService CreateRouteDiagnosticsService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsRouteDiagnosticsService();
        if (OperatingSystem.IsMacOS()) return new MacOsRouteDiagnosticsService();
        return new LinuxRouteDiagnosticsService();
    }

    public static ITerminalLauncher CreateTerminalLauncher()
    {
        if (OperatingSystem.IsWindows()) return new WindowsTerminalLauncher();
        if (OperatingSystem.IsMacOS()) return new MacOsTerminalLauncher();
        return new LinuxTerminalLauncher();
    }

    public static ICapturePrerequisiteService CreateCapturePrerequisiteService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCapturePrerequisiteService();
        if (OperatingSystem.IsMacOS()) return new MacOsCapturePrerequisiteService();
        return new LinuxCapturePrerequisiteService();
    }

    public static ITailscaleService CreateTailscaleService() => new TailscaleCliService();
}
