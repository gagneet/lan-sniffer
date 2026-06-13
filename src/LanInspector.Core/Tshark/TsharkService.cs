using System.Text.Json;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Core.Tshark;

public sealed class TsharkService : ITsharkService
{
    private static readonly string[] TsharkPaths = OperatingSystem.IsWindows()
        ? [@"C:\Program Files\Wireshark\tshark.exe"]
        : ["/usr/bin/tshark", "/usr/local/bin/tshark"];

    private static readonly string[] WiresharkPaths = OperatingSystem.IsWindows()
        ? [@"C:\Program Files\Wireshark\Wireshark.exe"]
        : ["/usr/bin/wireshark", "/usr/local/bin/wireshark"];

    public bool IsTsharkAvailable => TsharkPath is not null;
    public bool IsWiresharkAvailable => WiresharkPath is not null;

    public string? TsharkPath { get; } = DetectTool("tshark", TsharkPaths);
    public string? WiresharkPath { get; } = DetectTool("wireshark", WiresharkPaths);

    public async Task<TsharkExportResult> ExportPcapngAsync(
        string deviceName, TimeSpan duration, string outputPath, CancellationToken cancellationToken = default)
    {
        if (!IsTsharkAvailable)
            return new TsharkExportResult("", 0, "tshark is not installed");

        var seconds = (int)duration.TotalSeconds;
        var args = $"-i \"{deviceName}\" -a duration:{seconds} -w \"{outputPath}\" -F pcapng";

        try
        {
            await ProcessHelper.RunAsync(TsharkPath!, args, duration + TimeSpan.FromSeconds(10), cancellationToken);
            var exists = File.Exists(outputPath);
            return new TsharkExportResult(outputPath, exists ? -1 : 0,
                exists ? null : "Output file was not created");
        }
        catch (Exception ex)
        {
            return new TsharkExportResult(outputPath, 0, ex.Message);
        }
    }

    public async Task<IReadOnlyList<TsharkPacketSummary>> ReadSummaryAsync(
        string pcapPath, CancellationToken cancellationToken = default)
    {
        if (!IsTsharkAvailable)
            return [];

        if (!File.Exists(pcapPath))
            return [];

        var args = $"-r \"{pcapPath}\" -T fields -e frame.number -e frame.time_relative " +
                   $"-e ip.src -e ip.dst -e _ws.col.Protocol -e frame.len -e _ws.col.Info -E header=n -E separator=|";

        try
        {
            var output = await ProcessHelper.RunAsync(TsharkPath!, args, TimeSpan.FromMinutes(2), cancellationToken);
            return ParseTsharkFields(output);
        }
        catch
        {
            return [];
        }
    }

    public Task<bool> OpenInWiresharkAsync(string pcapPath, CancellationToken cancellationToken = default)
    {
        if (!IsWiresharkAvailable)
            return Task.FromResult(false);

        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = WiresharkPath!,
                Arguments = $"\"{pcapPath}\"",
                UseShellExecute = false
            });
            return Task.FromResult(proc is not null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static IReadOnlyList<TsharkPacketSummary> ParseTsharkFields(string output)
    {
        var packets = new List<TsharkPacketSummary>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 7) continue;

            if (!int.TryParse(parts[0].Trim(), out var num)) continue;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var ts)) continue;
            if (!int.TryParse(parts[5].Trim(), out var len)) continue;

            packets.Add(new TsharkPacketSummary(
                num, ts,
                parts[2].Trim(), parts[3].Trim(),
                parts[4].Trim(), len,
                parts.Length > 6 ? parts[6].Trim() : ""));
        }

        return packets;
    }

    private static string? DetectTool(string name, string[] paths)
    {
        if (ProcessHelper.IsAvailable(name))
            return name;
        return paths.FirstOrDefault(File.Exists);
    }
}
