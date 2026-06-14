namespace LanInspector.Core.Flipper;

public interface IFlipperConnectionService : IAsyncDisposable
{
    FlipperConnectionState State { get; }
    FlipperDeviceInfo? DeviceInfo { get; }

    /// <summary>List serial ports, ranked with likely-Flipper ports first.</summary>
    IReadOnlyList<FlipperPortInfo> DetectPorts();

    /// <summary>Connect to the Flipper, optionally on a specific port. Auto-detects if null.</summary>
    Task<bool> ConnectAsync(string? portName = null, CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>Send a CLI command and return its output. Waits for the prompt to return.</summary>
    Task<string> ExecuteCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// Send a streaming CLI command (e.g. subghz rx), collect output lines for
    /// <paramref name="duration"/>, send Ctrl+C, then return everything collected.
    /// </summary>
    Task<IReadOnlyList<string>> ExecuteStreamingCommandAsync(string command, TimeSpan duration, CancellationToken ct = default);
}
