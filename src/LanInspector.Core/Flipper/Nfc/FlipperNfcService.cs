namespace LanInspector.Core.Flipper.Nfc;

public sealed class FlipperNfcService : IFlipperNfcService
{
    private readonly IFlipperConnectionService _flipper;

    public FlipperNfcService(IFlipperConnectionService flipper) => _flipper = flipper;

    public async Task<NfcDetectionResult> DetectAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_flipper.State != FlipperConnectionState.Connected)
            return new NfcDetectionResult { Error = "Flipper not connected." };

        var dwell = timeout ?? TimeSpan.FromSeconds(10);

        try
        {
            var lines = await _flipper.ExecuteStreamingCommandAsync("nfc detect", dwell, ct);
            return ParseNfcOutput(lines);
        }
        catch (Exception ex)
        {
            return new NfcDetectionResult { Error = ex.Message };
        }
    }

    public async Task<RfidDetectionResult> ReadRfidAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_flipper.State != FlipperConnectionState.Connected)
            return new RfidDetectionResult { Error = "Flipper not connected." };

        var dwell = timeout ?? TimeSpan.FromSeconds(10);

        try
        {
            var lines = await _flipper.ExecuteStreamingCommandAsync("rfid read", dwell, ct);
            return ParseRfidOutput(lines);
        }
        catch (Exception ex)
        {
            return new RfidDetectionResult { Error = ex.Message };
        }
    }

    // ── Output parsing ────────────────────────────────────────────────────────

    private static NfcDetectionResult ParseNfcOutput(IReadOnlyList<string> lines)
    {
        string? uid = null, type = null, atqa = null, sak = null;

        foreach (var line in lines)
        {
            uid  ??= TryExtract(line, "UID");
            type ??= TryExtract(line, "Type") ?? TryExtract(line, "ISO");
            atqa ??= TryExtract(line, "ATQA");
            sak  ??= TryExtract(line, "SAK");
        }

        return new NfcDetectionResult
        {
            Detected = uid is not null,
            Uid      = uid,
            CardType = type,
            Atqa     = atqa,
            Sak      = sak
        };
    }

    private static RfidDetectionResult ParseRfidOutput(IReadOnlyList<string> lines)
    {
        string? data = null, proto = null;

        foreach (var line in lines)
        {
            data  ??= TryExtract(line, "Key") ?? TryExtract(line, "Data") ?? TryExtract(line, "ID");
            proto ??= TryExtract(line, "Protocol") ?? TryExtract(line, "Type");
        }

        return new RfidDetectionResult
        {
            Detected = data is not null,
            Data     = data,
            Protocol = proto
        };
    }

    private static string? TryExtract(string line, string field)
    {
        var idx = line.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = line[(idx + field.Length)..].TrimStart(':', ' ');
        return string.IsNullOrWhiteSpace(after) ? null : after.Trim();
    }
}
