namespace LanInspector.Core.Configuration;

/// <summary>
/// A known device flattened into editable text, as an editing grid presents it: the stored model
/// holds lists, a row holds comma-separated strings.
/// </summary>
/// <remarks>
/// Lives here rather than beside the view because the conversion is configuration logic, not
/// presentation — and because the test project is cross-platform while the WPF project is not.
/// </remarks>
public sealed class KnownDeviceDraft
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string DeviceType { get; set; } = "Device";

    public string KnownIps { get; set; } = "";

    public string KnownSubnets { get; set; } = "";

    public string KnownMacs { get; set; } = "";

    public string KnownHostnames { get; set; } = "";

    public string KnownTailscaleNames { get; set; } = "";

    /// <summary>Presented as a checkbox rather than a tag the user has to remember to type.</summary>
    public bool IsCritical { get; set; }

    public bool SshEnabled { get; set; }

    public string SshUser { get; set; } = "";

    public int SshPort { get; set; } = 22;

    /// <summary>Tags other than "critical", which has its own checkbox.</summary>
    public string Tags { get; set; } = "";

    /// <summary>A row is only worth saving once it can be identified.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(Id);

    public static KnownDeviceDraft From(KnownDeviceDefinition definition) => new()
    {
        Id = definition.Id,
        DisplayName = definition.DisplayName,
        DeviceType = definition.DeviceType,
        KnownIps = Join(definition.KnownIps),
        KnownSubnets = Join(definition.KnownSubnets),
        KnownMacs = Join(definition.KnownMacs),
        KnownHostnames = Join(definition.KnownHostnames),
        KnownTailscaleNames = Join(definition.KnownTailscaleNames),
        IsCritical = definition.IsCritical,
        SshEnabled = definition.Ssh?.Enabled == true,
        SshUser = definition.Ssh?.User ?? "",
        SshPort = definition.Ssh?.Port ?? 22,
        Tags = Join(definition.Tags.Where(tag => !string.Equals(tag, "critical", StringComparison.OrdinalIgnoreCase)))
    };

    public KnownDeviceDefinition ToDefinition()
    {
        var tags = Split(Tags).ToList();

        if (IsCritical && !tags.Any(tag => string.Equals(tag, "critical", StringComparison.OrdinalIgnoreCase)))
        {
            tags.Insert(0, "critical");
        }

        return new KnownDeviceDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Slug(DisplayName) : Id.Trim(),
            DisplayName = DisplayName.Trim(),
            DeviceType = string.IsNullOrWhiteSpace(DeviceType) ? "Device" : DeviceType.Trim(),
            KnownIps = [.. Split(KnownIps)],
            KnownSubnets = [.. Split(KnownSubnets)],
            KnownMacs = [.. Split(KnownMacs)],
            KnownHostnames = [.. Split(KnownHostnames)],
            KnownTailscaleNames = [.. Split(KnownTailscaleNames)],
            Ssh = SshEnabled || !string.IsNullOrWhiteSpace(SshUser)
                ? new KnownDeviceSshOptions { Enabled = SshEnabled, User = SshUser.Trim(), Port = SshPort <= 0 ? 22 : SshPort }
                : null,
            Tags = tags
        };
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    /// <summary>Accepts commas or semicolons, and tolerates stray whitespace around entries.</summary>
    private static IEnumerable<string> Split(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Derives an id from a display name, for a row the user did not give one.</summary>
    private static string Slug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug.Trim('-');
    }
}
