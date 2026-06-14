namespace LanInspector.Core.Flipper.SubGhz;

public sealed class FlipperSubGhzService : IFlipperSubGhzService
{
    private readonly IFlipperConnectionService _flipper;

    public FlipperSubGhzService(IFlipperConnectionService flipper) => _flipper = flipper;

    public async Task<SubGhzScanResult> ScanAsync(
        IEnumerable<double>? frequenciesMhz = null,
        TimeSpan? durationPerFrequency = null,
        CancellationToken ct = default)
    {
        var freqs    = (frequenciesMhz ?? IFlipperSubGhzService.DefaultFrequencies).ToList();
        var dwell    = durationPerFrequency ?? TimeSpan.FromSeconds(5);
        var started  = DateTime.UtcNow;
        var signals  = new List<SubGhzSignal>();

        if (_flipper.State != FlipperConnectionState.Connected)
            return new SubGhzScanResult
            {
                Signals = [],
                FrequenciesScanned = freqs,
                Duration = TimeSpan.Zero,
                Error = "Flipper not connected."
            };

        try
        {
            foreach (var freqMhz in freqs)
            {
                ct.ThrowIfCancellationRequested();

                var freqHz = (long)(freqMhz * 1_000_000);
                var lines  = await _flipper.ExecuteStreamingCommandAsync(
                    $"subghz rx {freqHz} 0", dwell, ct); // 0 = CC1101_INT (built-in radio)

                signals.AddRange(ParseSubGhzOutput(lines, freqMhz));
            }
        }
        catch (OperationCanceledException)
        {
            // Return partial results
        }
        catch (Exception ex)
        {
            return new SubGhzScanResult
            {
                Signals = signals,
                FrequenciesScanned = freqs,
                Duration = DateTime.UtcNow - started,
                Error = ex.Message
            };
        }

        return new SubGhzScanResult
        {
            Signals = signals,
            FrequenciesScanned = freqs,
            Duration = DateTime.UtcNow - started
        };
    }

    public async Task<SubGhzSignal?> ListenOnceAsync(double frequencyMhz, TimeSpan timeout, CancellationToken ct = default)
    {
        var freqHz = (long)(frequencyMhz * 1_000_000);
        var lines  = await _flipper.ExecuteStreamingCommandAsync($"subghz rx {freqHz} 0", timeout, ct);
        return ParseSubGhzOutput(lines, frequencyMhz).FirstOrDefault();
    }

    // ── Output parsing ────────────────────────────────────────────────────────

    private static List<SubGhzSignal> ParseSubGhzOutput(IReadOnlyList<string> lines, double freqMhz)
    {
        var results = new List<SubGhzSignal>();
        if (lines.Count == 0) return results;

        // Group lines into per-signal blocks. A new block starts when we see a
        // "Received" marker, a Protocol line, or a line with a known code field.
        SubGhzProtocol proto    = SubGhzProtocol.Unknown;
        double rssi             = 0;
        string? decoded         = null;
        bool inSignal           = false;

        void Flush()
        {
            if (!inSignal) return;
            results.Add(new SubGhzSignal
            {
                FrequencyMhz = freqMhz,
                RssiDbm      = rssi,
                Protocol     = proto,
                DecodedData  = decoded,
                RawLine      = decoded
            });
            proto = SubGhzProtocol.Unknown; rssi = 0; decoded = null; inSignal = false;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // RSSI line — marks the start of a new reception event
            if (TryParseRssi(line, out var parsedRssi))
            {
                Flush();
                rssi    = parsedRssi;
                inSignal = true;
                continue;
            }

            // Protocol identification
            var protoName = ExtractField(line, "Protocol");
            if (protoName is not null)
            {
                proto    = ParseProtocol(protoName);
                inSignal = true;
                continue;
            }

            // Decoded data (Data:, Code:, Key:)
            var data = ExtractField(line, "Data")
                    ?? ExtractField(line, "Code")
                    ?? ExtractField(line, "Key");
            if (data is not null)
            {
                decoded  = data;
                inSignal = true;
                continue;
            }

            // Raw line with no protocol info — treat as raw signal if it has meaningful content
            if (inSignal) continue;

            if (line.Contains("RAW", StringComparison.OrdinalIgnoreCase) && line.Contains("MHz"))
            {
                results.Add(new SubGhzSignal
                {
                    FrequencyMhz = freqMhz,
                    RssiDbm      = rssi,
                    Protocol     = SubGhzProtocol.Raw,
                    RawLine      = line
                });
            }
        }

        Flush();
        return results;
    }

    private static bool TryParseRssi(string line, out double rssi)
    {
        rssi = 0;
        var idx = line.IndexOf("RSSI", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        // Find a number (possibly negative) after "RSSI"
        var after = line[(idx + 4)..].TrimStart(':', ' ', '=');
        var end   = after.IndexOfAny([' ', '\t', '\r', 'd']); // stop at space or 'dBm'
        var numStr = end > 0 ? after[..end] : after;
        return double.TryParse(numStr, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out rssi);
    }

    private static string? ExtractField(string line, string fieldName)
    {
        var idx = line.IndexOf(fieldName, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = line[(idx + fieldName.Length)..].TrimStart(':', ' ');
        if (string.IsNullOrWhiteSpace(after)) return null;

        // Take up to next field delimiter (space + capital letter or '+')
        var end = after.IndexOf(" +", StringComparison.Ordinal);
        return end > 0 ? after[..end].Trim() : after.Trim();
    }

    private static SubGhzProtocol ParseProtocol(string name)
    {
        return name.ToLowerInvariant() switch
        {
            var s when s.Contains("princeton")    => SubGhzProtocol.Princeton,
            var s when s.Contains("nice")         => SubGhzProtocol.NiceFlo,
            var s when s.Contains("came")         => SubGhzProtocol.CAME,
            var s when s.Contains("holtek")       => SubGhzProtocol.Holtek,
            var s when s.Contains("gatetx")       => SubGhzProtocol.GateTx,
            var s when s.Contains("linear")       => SubGhzProtocol.LinearDelta3,
            var s when s.Contains("liftmaster")   => SubGhzProtocol.LiftMaster,
            var s when s.Contains("keeloq")       => SubGhzProtocol.KeeLoq,
            var s when s.Contains("oregon") && s.Contains("3") => SubGhzProtocol.OregonV3,
            var s when s.Contains("oregon")       => SubGhzProtocol.OregonV2,
            var s when s.Contains("marantec")     => SubGhzProtocol.Marantec,
            var s when s.Contains("secplus") && s.Contains("2") => SubGhzProtocol.SecPlusV2,
            var s when s.Contains("secplus")      => SubGhzProtocol.SecPlusV1,
            var s when s.Contains("raw")          => SubGhzProtocol.Raw,
            _                                     => SubGhzProtocol.Unknown
        };
    }
}
