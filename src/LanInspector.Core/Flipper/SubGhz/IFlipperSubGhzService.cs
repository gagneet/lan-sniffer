namespace LanInspector.Core.Flipper.SubGhz;

public interface IFlipperSubGhzService
{
    /// <summary>
    /// Common IoT sub-GHz frequencies to scan when no explicit list is given:
    /// 315 MHz (US), 433.92 MHz (EU/US), 868.35 MHz (EU Z-Wave), 915 MHz (US Z-Wave/LoRa).
    /// </summary>
    static readonly double[] DefaultFrequencies = [315.0, 433.92, 868.35, 915.0];

    /// <summary>
    /// Scan one or more sub-GHz frequencies for IoT signals.
    /// Listens on each frequency for <paramref name="durationPerFrequency"/> then moves on.
    /// </summary>
    Task<SubGhzScanResult> ScanAsync(
        IEnumerable<double>? frequenciesMhz = null,
        TimeSpan? durationPerFrequency = null,
        CancellationToken ct = default);

    /// <summary>Listen on a single frequency and return the first signal detected, or null if none within <paramref name="timeout"/>.</summary>
    Task<SubGhzSignal?> ListenOnceAsync(double frequencyMhz, TimeSpan timeout, CancellationToken ct = default);
}
