namespace LanInspector.Core.Identity;

public sealed class OuiVendorLookup
{
    private readonly Dictionary<string, string> _vendors = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _vendors.Count;

    public void LoadCsv(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var prefix = Normalize(parts[0]);
            if (prefix.Length < 6)
            {
                continue;
            }

            _vendors[prefix[..6]] = parts[1].Trim();
        }
    }

    public string? LookupVendor(string macAddress)
    {
        var normalized = Normalize(macAddress);
        if (normalized.Length < 6)
        {
            return null;
        }

        if (_vendors.TryGetValue(normalized[..6], out var vendor))
        {
            return vendor;
        }

        return IsLocallyAdministered(normalized) ? "Private/randomized MAC" : null;
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    private static bool IsLocallyAdministered(string normalizedMac)
    {
        if (normalizedMac.Length < 2)
        {
            return false;
        }

        var firstByte = Convert.ToByte(normalizedMac[..2], 16);
        return (firstByte & 0x02) == 0x02;
    }
}
