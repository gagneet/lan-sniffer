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
