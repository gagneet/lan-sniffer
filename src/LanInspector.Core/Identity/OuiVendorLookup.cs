using System.Reflection;

namespace LanInspector.Core.Identity;

public sealed class OuiVendorLookup
{
    private const string BuiltInResourceName = "LanInspector.Core.Data.oui.csv";

    private readonly Dictionary<string, string> _vendors = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _vendors.Count;

    /// <summary>
    /// Loads the vendor list compiled into the assembly.
    /// </summary>
    /// <remarks>
    /// Embedded rather than read from a file beside the executable, so a published single-file
    /// build stays one portable file. Copying only the executable used to leave vendor lookup
    /// silently returning nothing.
    /// </remarks>
    public int LoadBuiltIn()
    {
        using var stream = typeof(OuiVendorLookup).Assembly.GetManifestResourceStream(BuiltInResourceName);
        if (stream is null)
        {
            return 0;
        }

        using var reader = new StreamReader(stream);
        return LoadCsv(reader);
    }

    /// <summary>
    /// Loads additional prefixes from a file, overriding any already known. Missing files are
    /// ignored: a user-supplied list is an optional addition to the built-in one.
    /// </summary>
    public int LoadCsv(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var reader = new StreamReader(path);
            return LoadCsv(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Reads "Prefix,Vendor" rows, skipping the header. Returns the number added.</summary>
    public int LoadCsv(TextReader reader)
    {
        var added = 0;
        var isFirstLine = true;

        while (reader.ReadLine() is { } line)
        {
            if (isFirstLine)
            {
                isFirstLine = false;

                // Tolerates a list with no header row, which hand-made ones often lack.
                if (line.StartsWith("Prefix", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

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
            added++;
        }

        return added;
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
