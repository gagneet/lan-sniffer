using System.IO.Ports;
using System.Text;

namespace LanInspector.Core.Flipper;

/// <summary>
/// Communicates with the Flipper Zero over its USB CDC serial CLI.
/// USB VID 0x0483 / PID 0x5740 (STMicroelectronics VCP).
/// On Linux: /dev/ttyACM*  |  Windows: COM port  |  macOS: /dev/cu.usbmodem*
/// </summary>
public sealed class FlipperSerialService : IFlipperConnectionService
{
    private const string CliPrompt = ">: ";
    private const int BaudRate = 230400;
    private const int ReadTimeoutMs = 300;

    private SerialPort? _port;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FlipperConnectionState State { get; private set; } = FlipperConnectionState.Disconnected;
    public FlipperDeviceInfo? DeviceInfo { get; private set; }

    // ── Port discovery ────────────────────────────────────────────────────────

    public IReadOnlyList<FlipperPortInfo> DetectPorts()
    {
        var names = SerialPort.GetPortNames();
        return names
            .Select(n => new FlipperPortInfo(n, GetPortDescription(n), IsLikelyFlipper(n)))
            .OrderByDescending(p => p.LooksLikeFlipper)
            .ThenBy(p => p.Name)
            .ToList();
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(string? portName = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (State == FlipperConnectionState.Connected)
                return true;

            State = FlipperConnectionState.Connecting;

            var name = portName ?? AutoDetectPort();
            if (name is null)
            {
                State = FlipperConnectionState.Error;
                return false;
            }

            _port = new SerialPort(name, BaudRate)
            {
                ReadTimeout  = ReadTimeoutMs,
                WriteTimeout = 1000,
                Encoding     = Encoding.UTF8,
                DtrEnable    = true,
                RtsEnable    = true
            };

            _port.Open();

            // Wake the CLI
            _port.Write("\r\n");
            await Task.Delay(500, ct);
            DrainInput();

            var version = await RunCommandAsync("version", TimeSpan.FromSeconds(5));
            DeviceInfo  = ParseVersionOutput(version, name);
            State       = FlipperConnectionState.Connected;
            return true;
        }
        catch
        {
            _port?.Close();
            _port?.Dispose();
            _port  = null;
            State  = FlipperConnectionState.Error;
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _port?.Close();
            _port?.Dispose();
            _port      = null;
            DeviceInfo = null;
            State      = FlipperConnectionState.Disconnected;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Command execution ─────────────────────────────────────────────────────

    public async Task<string> ExecuteCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfNotConnected();
            return await RunCommandAsync(command, timeout ?? TimeSpan.FromSeconds(10));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ExecuteStreamingCommandAsync(string command, TimeSpan duration, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfNotConnected();
            return await RunStreamingAsync(command, duration, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Internal: single-response command ────────────────────────────────────

    private async Task<string> RunCommandAsync(string command, TimeSpan timeout)
    {
        _port!.Write($"{command}\r\n");

        var buf      = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var ch = (char)_port.ReadChar();
                buf.Append(ch);
                if (buf.ToString().EndsWith(CliPrompt))
                    break;
            }
            catch (TimeoutException)
            {
                await Task.Delay(30);
            }
            catch (Exception)
            {
                break;
            }
        }

        return StripEchoAndPrompt(buf.ToString(), command);
    }

    // ── Internal: streaming command (subghz rx, nfc detect, …) ──────────────

    private async Task<IReadOnlyList<string>> RunStreamingAsync(string command, TimeSpan duration, CancellationToken ct)
    {
        _port!.Write($"{command}\r\n");
        await Task.Delay(200, ct); // let command echo settle

        var lines   = new List<string>();
        var linesBuf = new StringBuilder();

        await Task.Run(() =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var ch = (char)_port.ReadChar();
                    if (ch == '\n')
                    {
                        var line = linesBuf.ToString().Trim('\r', '\n', ' ');
                        // Skip the command echo and blank lines
                        if (!string.IsNullOrWhiteSpace(line) &&
                            !line.Equals(command, StringComparison.OrdinalIgnoreCase))
                        {
                            lines.Add(line);
                        }
                        linesBuf.Clear();
                    }
                    else
                    {
                        linesBuf.Append(ch);
                    }
                }
                catch (TimeoutException)
                {
                    // No data in this window – keep listening
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        // Stop the running command on the Flipper
        try
        {
            _port.Write("\x03"); // Ctrl+C
            await Task.Delay(400, CancellationToken.None);
            DrainInput();
        }
        catch { /* port may have been closed */ }

        return lines;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void DrainInput()
    {
        _port!.ReadTimeout = 200;
        try { while (_port.BytesToRead > 0) _port.ReadChar(); }
        catch { /* nothing */ }
    }

    private void ThrowIfNotConnected()
    {
        if (State != FlipperConnectionState.Connected || _port?.IsOpen != true)
            throw new InvalidOperationException("Flipper is not connected.");
    }

    private static string StripEchoAndPrompt(string raw, string command)
    {
        var text = raw.TrimStart();
        // Remove echoed command at the top
        if (text.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            text = text[command.Length..].TrimStart('\r', '\n');
        // Remove trailing prompt
        if (text.EndsWith(CliPrompt))
            text = text[..^CliPrompt.Length];
        return text.Trim();
    }

    // ── Port auto-detection ───────────────────────────────────────────────────

    private static string? AutoDetectPort()
    {
        var ports = SerialPort.GetPortNames();

        if (OperatingSystem.IsLinux())
        {
            // Prefer the port with the Flipper VID/PID in sysfs
            var byId = FindLinuxFlipperPort();
            if (byId is not null) return byId;
            return ports.Where(p => p.StartsWith("/dev/ttyACM")).OrderBy(p => p).FirstOrDefault();
        }

        if (OperatingSystem.IsMacOS())
            return ports.FirstOrDefault(p => p.Contains("usbmodem") || p.Contains("Flipper"));

        // Windows: can't easily filter without WMI; return first COM port
        return ports.FirstOrDefault();
    }

    private static string? FindLinuxFlipperPort()
    {
        foreach (var port in SerialPort.GetPortNames())
        {
            var baseName   = Path.GetFileName(port);
            var vendorFile = $"/sys/class/tty/{baseName}/device/../idVendor";
            var productFile = $"/sys/class/tty/{baseName}/device/../idProduct";

            if (!File.Exists(vendorFile) || !File.Exists(productFile))
                continue;

            try
            {
                if (File.ReadAllText(vendorFile).Trim() == "0483" &&
                    File.ReadAllText(productFile).Trim() == "5740")
                    return port;
            }
            catch { /* sysfs read error, skip */ }
        }
        return null;
    }

    private static bool IsLikelyFlipper(string portName)
    {
        if (OperatingSystem.IsLinux())  return portName.StartsWith("/dev/ttyACM");
        if (OperatingSystem.IsMacOS())  return portName.Contains("usbmodem") || portName.Contains("Flipper");
        return portName.StartsWith("COM");
    }

    private static string GetPortDescription(string portName)
    {
        if (!OperatingSystem.IsLinux()) return portName;

        var baseName    = Path.GetFileName(portName);
        var vendorPath  = $"/sys/class/tty/{baseName}/device/../idVendor";
        var productPath = $"/sys/class/tty/{baseName}/device/../idProduct";

        if (!File.Exists(vendorPath)) return portName;

        try
        {
            var vid = File.ReadAllText(vendorPath).Trim();
            var pid = File.ReadAllText(productPath).Trim();
            return (vid, pid) == ("0483", "5740") ? "Flipper Zero (USB CDC)" : $"USB VID:{vid} PID:{pid}";
        }
        catch { return portName; }
    }

    // ── Version parsing ───────────────────────────────────────────────────────

    private static FlipperDeviceInfo ParseVersionOutput(string output, string portName)
    {
        string fw = "", hw = "", target = "", build = "";
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("SW version", StringComparison.OrdinalIgnoreCase))   fw     = After(t, ':');
            else if (t.StartsWith("HW version", StringComparison.OrdinalIgnoreCase)) hw   = After(t, ':');
            else if (t.StartsWith("Build date", StringComparison.OrdinalIgnoreCase)) build = After(t, ':');
            else if (t.StartsWith("Target",     StringComparison.OrdinalIgnoreCase)) target = After(t, ':');
        }
        return new FlipperDeviceInfo(portName, fw, hw, target, build);
    }

    private static string After(string line, char separator)
    {
        var idx = line.IndexOf(separator);
        return idx >= 0 ? line[(idx + 1)..].Trim() : "";
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _lock.Dispose();
    }
}
