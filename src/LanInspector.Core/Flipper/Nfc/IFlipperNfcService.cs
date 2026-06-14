namespace LanInspector.Core.Flipper.Nfc;

public interface IFlipperNfcService
{
    /// <summary>Wait for an NFC card and return its info, or a not-detected result if none arrives within <paramref name="timeout"/>.</summary>
    Task<NfcDetectionResult> DetectAsync(TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Wait for a 125 kHz RFID tag and return its data.</summary>
    Task<RfidDetectionResult> ReadRfidAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
