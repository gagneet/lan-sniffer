namespace LanInspector.Core.Flipper.Ble;

/// <summary>
/// Reads the 2.4 GHz band through the Flipper's BLE radio.
///
/// Stock firmware's BLE stack is peripheral-only: it advertises itself and cannot scan
/// for other devices, so there is no way to list nearby BLE devices, MAC addresses or
/// names over the CLI. What the <c>bt</c> command does expose is the HCI LE receiver
/// test (<c>rx_carrier</c>), which reports received energy on one channel. That makes a
/// band survey — how busy each BLE advertising channel is — which is the useful reading
/// for a network tool, because those channels sit inside the 2.4 GHz Wi-Fi band.
/// </summary>
public interface IFlipperBleService
{
    /// <summary>
    /// The three BLE advertising channels, as HCI LE test channel indices.
    /// Index N is 2402 + 2N MHz, so 0 = 2402 (adv 37), 12 = 2426 (adv 38), 39 = 2480 (adv 39).
    /// </summary>
    static readonly (int RfChannel, int AdvertisingChannel)[] AdvertisingChannels =
        [(0, 37), (12, 38), (39, 39)];

    /// <summary>Radio and stack state from <c>bt hci_info</c>. Works without Debug mode.</summary>
    Task<string> GetHciInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Sample each advertising channel for <paramref name="durationPerChannel"/>.
    /// Requires Debug mode on the Flipper (Settings → System → Debug); without it the
    /// result carries <see cref="BleSurveyResult.DebugModeRequired"/>.
    /// </summary>
    Task<BleSurveyResult> SurveyAsync(
        IEnumerable<int>? rfChannels = null,
        TimeSpan? durationPerChannel = null,
        CancellationToken ct = default);
}
