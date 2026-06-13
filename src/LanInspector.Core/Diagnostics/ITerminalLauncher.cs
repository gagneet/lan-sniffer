namespace LanInspector.Core.Diagnostics;

public interface ITerminalLauncher
{
    bool CanLaunch { get; }
    Task<bool> LaunchSshAsync(string sshCommand, CancellationToken cancellationToken = default);
}
