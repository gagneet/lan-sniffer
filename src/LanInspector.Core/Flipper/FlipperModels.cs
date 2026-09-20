namespace LanInspector.Core.Flipper;

public enum FlipperConnectionState { Disconnected, Connecting, Connected, Error }

public sealed record FlipperPortInfo(string Name, string Description, bool LooksLikeFlipper);

public sealed record FlipperDeviceInfo(
    string PortName,
    string FirmwareVersion,
    string HardwareVersion,
    string Target,
    string BuildDate);

// ── Sub-GHz ──────────────────────────────────────────────────────────────────

public enum SubGhzProtocol
{
    Unknown = 0,
    Raw,
    Princeton,
    NiceFlo,
    CAME,
    Holtek,
    GateTx,
    LinearDelta3,
    LiftMaster,
    KeeLoq,
    OregonV2,
    OregonV3,
    Marantec,
    SecPlusV1,
    SecPlusV2,
}

public sealed class SubGhzSignal
{
    public required double FrequencyMhz { get; init; }
    public required double RssiDbm { get; init; }
    public SubGhzProtocol Protocol { get; init; }
    public string? DecodedData { get; init; }
    public string? RawLine { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public string ProtocolLabel => Protocol switch
    {
        SubGhzProtocol.Princeton     => "Princeton (gate/garage remote)",
        SubGhzProtocol.NiceFlo       => "Nice FLO (gate/garage)",
        SubGhzProtocol.CAME          => "CAME (gate controller)",
        SubGhzProtocol.Holtek        => "Holtek (wireless remote)",
        SubGhzProtocol.GateTx        => "Gate TX",
        SubGhzProtocol.LinearDelta3  => "Linear Delta 3",
        SubGhzProtocol.LiftMaster    => "LiftMaster (garage door)",
        SubGhzProtocol.KeeLoq        => "KeeLoq (car/security rolling code)",
        SubGhzProtocol.OregonV2
         or SubGhzProtocol.OregonV3  => "Oregon Scientific (weather sensor)",
        SubGhzProtocol.Marantec      => "Marantec (garage door)",
        SubGhzProtocol.SecPlusV1
         or SubGhzProtocol.SecPlusV2 => "Security+ (garage door)",
        SubGhzProtocol.Raw           => "Unknown (raw signal)",
        _                            => Protocol.ToString()
    };

    public string FrequencyLabel => FrequencyMhz switch
    {
        >= 315.0 and <= 315.1   => "315 MHz (US ISM – remotes/sensors)",
        >= 433.8 and <= 434.1   => "433.92 MHz (EU/US ISM – IoT sensors)",
        >= 868.0 and <= 868.6   => "868 MHz (EU Z-Wave / LoRa)",
        >= 915.0 and <= 915.1   => "915 MHz (US Z-Wave / LoRa)",
        _                       => $"{FrequencyMhz:F3} MHz"
    };
}

public sealed class SubGhzScanResult
{
    public required IReadOnlyList<SubGhzSignal> Signals { get; init; }
    public required IReadOnlyList<double> FrequenciesScanned { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => Error is null;
}

// ── NFC / RFID ───────────────────────────────────────────────────────────────

public sealed class NfcDetectionResult
{
    public bool Detected { get; init; }
    public string? Uid { get; init; }
    public string? CardType { get; init; }
    public string? Atqa { get; init; }
    public string? Sak { get; init; }
    public string? Error { get; init; }
}

public sealed class RfidDetectionResult
{
    public bool Detected { get; init; }
    public string? Data { get; init; }
    public string? Protocol { get; init; }
    public string? Error { get; init; }
}

// ── BLE / 2.4 GHz band ───────────────────────────────────────────────────────

/// <summary>
/// One BLE advertising channel sampled with <c>bt rx_carrier</c>.
/// The radio reports received energy, so the samples cover everything on that
/// frequency — BLE advertisements, Wi-Fi, Zigbee, microwave ovens — not just BLE.
/// </summary>
public sealed class BleChannelSurvey
{
    /// <summary>HCI LE test channel index, 0-39. Frequency is 2402 + 2N MHz.</summary>
    public required int RfChannel { get; init; }

    /// <summary>
    /// The BLE advertising channel this index carries (37, 38 or 39), or null for one of
    /// the 37 data channels, which `--channel` can also ask for. Null rather than 0,
    /// because 0 is a valid RF index and reading "Adv ch 0" as a channel number is wrong.
    /// </summary>
    public int? AdvertisingChannel { get; init; }

    public required IReadOnlyList<double> Samples { get; init; }

    public double FrequencyMhz => 2402 + (2 * RfChannel);

    public double PeakDbm => Samples.Count == 0 ? double.NaN : Samples.Max();

    public double MedianDbm
    {
        get
        {
            if (Samples.Count == 0) return double.NaN;
            var sorted = Samples.OrderBy(v => v).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }

    /// <summary>
    /// Which 2.4 GHz Wi-Fi channels sit on this frequency. A busy advertising channel
    /// next to a Wi-Fi channel in use is the reading that matters on a home network.
    /// </summary>
    public string WifiOverlapLabel => AdvertisingChannel switch
    {
        37 => "2402 MHz — below Wi-Fi ch 1, usually the quietest",
        38 => "2426 MHz — inside Wi-Fi ch 3-6",
        39 => "2480 MHz — inside Wi-Fi ch 13/14",
        _  => $"{FrequencyMhz:F0} MHz — BLE data channel"
    };
}

public sealed class BleSurveyResult
{
    public required IReadOnlyList<BleChannelSurvey> Channels { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>Radio and stack state from <c>bt hci_info</c>, which needs no Debug mode.</summary>
    public string? HciInfo { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// The Flipper answered with its <c>bt</c> usage text, which means the RF test
    /// commands are compiled in but gated: Settings → System → Debug must be ON.
    /// </summary>
    public bool DebugModeRequired { get; init; }

    public bool Succeeded => Error is null;
}
