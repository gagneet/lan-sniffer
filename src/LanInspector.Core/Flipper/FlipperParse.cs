using System.Globalization;

namespace LanInspector.Core.Flipper;

/// <summary>Line parsing shared by the Flipper radio services.</summary>
internal static class FlipperParse
{
    /// <summary>
    /// Read the number out of an RSSI line. Covers both shapes the firmware emits:
    /// sub-GHz "RSSI: -75.30 dBm" and <c>bt rx_carrier</c>'s "RSSI:  -75.3 dB".
    /// </summary>
    internal static bool TryParseRssi(string line, out double rssi)
    {
        rssi = 0;
        var idx = line.IndexOf("RSSI", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        // Find a number (possibly negative) after "RSSI"
        var after = line[(idx + 4)..].TrimStart(':', ' ', '=');
        var end   = after.IndexOfAny([' ', '\t', '\r', 'd']); // stop at space or 'dBm'
        var numStr = end > 0 ? after[..end] : after;
        return double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rssi);
    }
}
