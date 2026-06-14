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

            // Firmware 1.x uses `device_info`; older official firmware used `version`;
            // some community builds use `unit_info`. Try all three in order.
            var version = await RunCommandAsync("version", TimeSpan.FromSeconds(3));
            if (IsUnknownCommand(version))
                version = await RunCommandAsync("device_info", TimeSpan.FromSeconds(3));
            if (IsUnknownCommand(version))
                version = await RunCommandAsync("unit_info", TimeSpan.FromSeconds(3));
            if (IsUnknownCommand(version))
                version = "";   // no version info available; still connect

            DeviceInfo = ParseVersionOutput(version, name);
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
            var byId = FindLinuxFlipperPort();
            if (byId is not null) return byId;
            return ports.Where(p => p.StartsWith("/dev/ttyACM")).OrderBy(p => p).FirstOrDefault();
        }

        if (OperatingSystem.IsMacOS())
            return ports.FirstOrDefault(p => p.Contains("usbmodem") || p.Contains("Flipper"));

        if (OperatingSystem.IsWindows())
        {
            var known = GetWindowsFlipperPorts();
            // Prefer the first confirmed Flipper port; fall back to first COM port
            return known.Keys.OrderBy(p => p).FirstOrDefault() ?? ports.FirstOrDefault();
        }

        return ports.FirstOrDefault();
    }

    private static string? FindLinuxFlipperPort()
    {
        foreach (var port in SerialPort.GetPortNames())
        {
            var baseName    = Path.GetFileName(port);
            var vendorFile  = $"/sys/class/tty/{baseName}/device/../idVendor";
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

    // Reads HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_0483&PID_5740 to find
    // any COM ports belonging to the Flipper Zero (STM32 VCP, VID 0483 / PID 5740).
    // Returns portName → friendly description, e.g. "COM3" → "STM32 Virtual COM Port (COM3)".
    private static Dictionary<string, string> GetWindowsFlipperPorts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return result;

        try
        {
            using var usbKey = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is null) return result;

            foreach (var keyName in usbKey.GetSubKeyNames())
            {
                // Match VID_0483&PID_5740 exactly, or composite devices like VID_0483&PID_5740&MI_00
                if (!keyName.StartsWith("VID_0483&PID_5740", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var deviceKey = usbKey.OpenSubKey(keyName);
                if (deviceKey is null) continue;

                foreach (var instance in deviceKey.GetSubKeyNames())
                {
                    using var instKey = deviceKey.OpenSubKey(instance);
                    if (instKey is null) continue;

                    var friendly = instKey.GetValue("FriendlyName") as string ?? "Flipper Zero";

                    using var devParams = instKey.OpenSubKey("Device Parameters");
                    var portName = devParams?.GetValue("PortName") as string;

                    if (!string.IsNullOrEmpty(portName))
                        result[portName] = friendly;
                }
            }
        }
        catch { /* registry access denied or unavailable */ }

        return result;
    }

    private static bool IsLikelyFlipper(string portName)
    {
        if (OperatingSystem.IsLinux())   return portName.StartsWith("/dev/ttyACM");
        if (OperatingSystem.IsMacOS())   return portName.Contains("usbmodem") || portName.Contains("Flipper");
        if (OperatingSystem.IsWindows()) return GetWindowsFlipperPorts().ContainsKey(portName);
        return false;
    }

    private static string GetPortDescription(string portName)
    {
        if (OperatingSystem.IsWindows())
        {
            var known = GetWindowsFlipperPorts();
            return known.TryGetValue(portName, out var desc) ? desc : portName;
        }

        if (OperatingSystem.IsLinux())
        {
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

        return portName;
    }

    private static bool IsUnknownCommand(string response) =>
        response.Contains("could not find", StringComparison.OrdinalIgnoreCase) ||
        response.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
        response.Contains("not found",       StringComparison.OrdinalIgnoreCase);

    // ── Version parsing ───────────────────────────────────────────────────────

    private static FlipperDeviceInfo ParseVersionOutput(string output, string portName)
    {
        // Collect every "Key : Value" / "Key: Value" pair so we can handle both
        // old firmware ("SW version : 0.88.0") and new firmware ("Git Tag : 0.97.0")
        // without hard-coding exact spacing.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var key   = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                fields.TryAdd(key, value);
        }

        // Firmware version
        // firmware 1.x device_info: "firmware_branch" → e.g. "release-1.43"
        // firmware 0.x version:     "Git Tag"         → e.g. "0.97.0"
        // older firmware version:   "SW version"      → e.g. "0.88.0 abc ..."
        var fwRaw = fields.GetValueOrDefault("firmware_branch")
                 ?? fields.GetValueOrDefault("Git Tag")
                 ?? fields.GetValueOrDefault("Git Branch Num")
                 ?? fields.GetValueOrDefault("SW version")
                 ?? "";
        // Strip "release-" prefix so we show "1.43" not "release-1.43"
        var fw = fwRaw.StartsWith("release-", StringComparison.OrdinalIgnoreCase)
                     ? fwRaw["release-".Length..]
                     : fwRaw;

        // Hardware
        // firmware 1.x: "hardware_model" + "hardware_ver"; old: "HW version"
        var hwModel = fields.GetValueOrDefault("hardware_model") ?? "";
        var hwVer   = fields.GetValueOrDefault("hardware_ver")   ?? "";
        var hw = fields.GetValueOrDefault("HW version")
              ?? (hwModel.Length > 0 || hwVer.Length > 0
                      ? $"{hwModel} v{hwVer}".Trim(' ', 'v')
                      : "");

        // Target
        var target = fields.GetValueOrDefault("firmware_target")
                  ?? fields.GetValueOrDefault("Target")
                  ?? "";

        // Build date
        // firmware 1.x: "firmware_build_date"; 0.x: "Build Date" + optional "Build Time"
        var buildDate = fields.GetValueOrDefault("firmware_build_date")
                     ?? fields.GetValueOrDefault("Build Date")
                     ?? fields.GetValueOrDefault("Build date")
                     ?? "";
        var buildTime = fields.GetValueOrDefault("Build Time") ?? "";
        var build = (buildDate, buildTime) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{buildDate} {buildTime}",
            ({ Length: > 0 }, _)               => buildDate,
            _                                  => ""
        };

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
