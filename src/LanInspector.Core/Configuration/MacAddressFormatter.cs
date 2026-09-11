namespace LanInspector.Core.Configuration;

/// <summary>
/// MAC addresses reach this application in several notations: PacketDotNet emits
/// <c>9C6B00AABBCC</c>, <c>ip neigh</c> emits <c>9c:6b:00:aa:bb:cc</c>, Windows <c>arp -a</c>
/// emits <c>9c-6b-00-aa-bb-cc</c>, and macOS drops leading zeros (<c>9c:6b:0:aa:bb:cc</c>).
/// Everything that compares MAC addresses goes through here first.
/// </summary>
public static class MacAddressFormatter
{
    /// <summary>
    /// Strips separators and upper-cases. Colon/hyphen separated input is re-padded so that
    /// macOS-style abbreviated octets (<c>9c:6b:0:aa:bb:cc</c>) normalise identically to their
    /// zero-padded equivalents. Returns <see cref="string.Empty"/> for unusable input.
    /// </summary>
    public static string Normalise(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return string.Empty;
        }

        var trimmed = macAddress.Trim();

        if (trimmed.Contains(':') || trimmed.Contains('-'))
        {
            var octets = trimmed.Split([':', '-'], StringSplitOptions.TrimEntries);
            if (octets.Length == 6 && octets.All(octet => octet.Length is 1 or 2 && octet.All(Uri.IsHexDigit)))
            {
                return string.Concat(octets.Select(octet => octet.PadLeft(2, '0'))).ToUpperInvariant();
            }
        }

        var hexOnly = new string(trimmed.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hexOnly.Length == 12 ? hexOnly : string.Empty;
    }

    /// <summary>
    /// Renders a normalised MAC in the colon-separated form used throughout the UI and CLI.
    /// Input that is not a full MAC is returned unchanged.
    /// </summary>
    public static string ToDisplayForm(string? macAddress)
    {
        var normalised = Normalise(macAddress);
        if (normalised.Length != 12)
        {
            return macAddress ?? string.Empty;
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(index => normalised.Substring(index * 2, 2)));
    }
}
