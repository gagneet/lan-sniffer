namespace LanInspector.Core.Flipper.Ble;

public sealed class FlipperBleService : IFlipperBleService
{
    private readonly IFlipperConnectionService _flipper;

    public FlipperBleService(IFlipperConnectionService flipper) => _flipper = flipper;

    public async Task<string> GetHciInfoAsync(CancellationToken ct = default)
    {
        if (_flipper.State != FlipperConnectionState.Connected)
            return "";
        try
        {
            return await _flipper.ExecuteCommandAsync("bt hci_info", TimeSpan.FromSeconds(5), ct);
        }
        catch (Exception ex)
        {
            return $"(hci_info failed: {ex.Message})";
        }
    }

    public async Task<BleSurveyResult> SurveyAsync(
        IEnumerable<int>? rfChannels = null,
        TimeSpan? durationPerChannel = null,
        CancellationToken ct = default)
    {
        var started  = DateTime.UtcNow;
        var channels = (rfChannels ?? IFlipperBleService.AdvertisingChannels.Select(c => c.RfChannel)).ToList();
        var dwell    = durationPerChannel ?? TimeSpan.FromSeconds(5);

        if (_flipper.State != FlipperConnectionState.Connected)
            return new BleSurveyResult { Channels = [], Duration = TimeSpan.Zero, Error = "Flipper not connected." };

        var hci     = await GetHciInfoAsync(ct);
        var results = new List<BleChannelSurvey>();
        var gated   = false;

        try
        {
            foreach (var rfChannel in channels)
            {
                ct.ThrowIfCancellationRequested();

                if (rfChannel is < 0 or > 39)
                    return new BleSurveyResult
                    {
                        Channels = results, Duration = DateTime.UtcNow - started, HciInfo = hci,
                        Error = $"Channel {rfChannel} is out of range; BLE test channels are 0-39."
                    };

                var lines = await _flipper.ExecuteStreamingCommandAsync($"bt rx_carrier {rfChannel}", dwell, ct);

                if (LooksLikeUsageText(lines))
                {
                    gated = true;
                    continue;
                }

                results.Add(new BleChannelSurvey
                {
                    RfChannel          = rfChannel,
                    AdvertisingChannel = AdvertisingChannelFor(rfChannel),
                    Samples            = ParseRssiSamples(lines)
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Return partial results
        }
        catch (Exception ex)
        {
            return new BleSurveyResult
            {
                Channels = results, Duration = DateTime.UtcNow - started, HciInfo = hci, Error = ex.Message
            };
        }

        return new BleSurveyResult
        {
            Channels          = results,
            Duration          = DateTime.UtcNow - started,
            HciInfo           = hci,
            DebugModeRequired = gated,
            Error             = gated && results.Count == 0
                                    ? "The Flipper refused 'bt rx_carrier' and printed its usage text. "
                                    + "Turn Debug mode on: Settings → System → Debug = ON, then retry."
                                    : null
        };
    }

    // ── Output parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>bt rx_carrier</c> overwrites one line with "RSSI: %6.1f dB\r" about ten times a
    /// second, so a survey is the set of readings taken over the dwell.
    /// </summary>
    internal static List<double> ParseRssiSamples(IReadOnlyList<string> lines)
    {
        var samples = new List<double>();
        foreach (var line in lines)
            if (FlipperParse.TryParseRssi(line, out var rssi))
                samples.Add(rssi);
        return samples;
    }

    /// <summary>
    /// With Debug mode off the firmware never registers the RF test subcommands and falls
    /// through to <c>bt_cli_print_usage</c>, so the reply is the command list, not readings.
    /// </summary>
    internal static bool LooksLikeUsageText(IReadOnlyList<string> lines)
    {
        if (lines.Any(l => l.Contains("RSSI", StringComparison.OrdinalIgnoreCase)))
            return false;
        return lines.Any(l => l.Contains("Cmd list", StringComparison.OrdinalIgnoreCase)
                           || l.Contains("hci_info", StringComparison.OrdinalIgnoreCase)
                           || l.StartsWith("Usage", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The advertising channel an RF index carries, or null when it carries none.
    /// FirstOrDefault on a tuple returns (0, 0) for "no match", which is indistinguishable
    /// from RF channel 0 — a real advertising channel — so the lookup is explicit.
    /// </summary>
    internal static int? AdvertisingChannelFor(int rfChannel)
    {
        foreach (var (rf, advertising) in IFlipperBleService.AdvertisingChannels)
            if (rf == rfChannel)
                return advertising;
        return null;
    }
}
