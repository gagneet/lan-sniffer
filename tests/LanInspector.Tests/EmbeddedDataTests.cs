using LanInspector.Core.Configuration;
using LanInspector.Core.Identity;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// The vendor list and the example configuration are compiled into the assembly so a published
/// single-file build is one portable executable. If an embedded resource is renamed or dropped
/// from the project file, nothing fails to compile — the data just silently goes missing at
/// runtime, which is exactly what these guard against.
/// </summary>
public sealed class EmbeddedDataTests
{
    [Fact]
    public void LoadBuiltIn_ReturnsVendorPrefixes()
    {
        var lookup = new OuiVendorLookup();

        var added = lookup.LoadBuiltIn();

        Assert.True(added > 0, "The built-in OUI list is embedded but loaded nothing.");
        Assert.Equal(added, lookup.Count);
    }

    [Fact]
    public void LoadBuiltIn_ResolvesAKnownPrefix()
    {
        var lookup = new OuiVendorLookup();
        lookup.LoadBuiltIn();

        Assert.Equal("Raspberry Pi Foundation", lookup.LookupVendor("B8:27:EB:11:22:33"));
    }

    [Fact]
    public void LoadCsv_FromAFile_AddsToTheBuiltInList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-oui-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "Prefix,Vendor\n1CF64C,Example Corp\n");

            var lookup = new OuiVendorLookup();
            var builtIn = lookup.LoadBuiltIn();
            var added = lookup.LoadCsv(path);

            Assert.Equal(1, added);
            Assert.Equal(builtIn + 1, lookup.Count);
            Assert.Equal("Example Corp", lookup.LookupVendor("1c:f6:4c:51:76:d3"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadCsv_UserEntry_OverridesTheBuiltInOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-oui-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "Prefix,Vendor\nB827EB,My Own Label\n");

            var lookup = new OuiVendorLookup();
            lookup.LoadBuiltIn();
            lookup.LoadCsv(path);

            Assert.Equal("My Own Label", lookup.LookupVendor("B8:27:EB:11:22:33"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadCsv_MissingFile_IsNotAnError()
    {
        var lookup = new OuiVendorLookup();

        Assert.Equal(0, lookup.LoadCsv("/this/does/not/exist.csv"));
    }

    [Fact]
    public void LoadCsv_ListWithNoHeader_IsStillRead()
    {
        var lookup = new OuiVendorLookup();

        var added = lookup.LoadCsv(new StringReader("AABBCC,First Vendor\nDDEEFF,Second Vendor\n"));

        Assert.Equal(2, added);
        Assert.Equal("First Vendor", lookup.LookupVendor("AA:BB:CC:00:00:01"));
    }

    [Fact]
    public void GetExampleJson_IsEmbeddedAndParses()
    {
        var json = KnownDevicesConfiguration.GetExampleJson();

        Assert.Contains("knownDevices", json);

        var path = Path.Combine(Path.GetTempPath(), $"laninspector-example-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            Assert.NotEmpty(KnownDevicesConfiguration.Load(path).KnownDevices);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteExampleTo_CreatesTheFileAndDoesNotOverwriteIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"laninspector-{Guid.NewGuid():N}");
        try
        {
            var path = KnownDevicesConfiguration.WriteExampleTo(directory);
            Assert.True(File.Exists(path));

            File.WriteAllText(path, "edited by hand");
            KnownDevicesConfiguration.WriteExampleTo(directory);

            // An existing file is someone's work, even if it started as the example.
            Assert.Equal("edited by hand", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
