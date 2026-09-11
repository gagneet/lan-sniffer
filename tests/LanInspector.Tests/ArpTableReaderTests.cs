using LanInspector.Core.Configuration;
using LanInspector.Core.Locator;
using Xunit;

namespace LanInspector.Tests;

public sealed class ArpTableReaderTests
{
    [Fact]
    public void Parse_LinuxIpNeighOutput_ReadsAddressMacInterfaceAndState()
    {
        const string output = """
            192.168.0.1 dev enp3s0 lladdr 44:d5:f2:11:22:33 REACHABLE
            192.168.0.154 dev enp3s0 lladdr 9c:6b:00:aa:bb:cc STALE
            192.168.0.77 dev enp3s0  FAILED
            """;

        var entries = ArpTableReader.Parse(output);

        Assert.Equal(2, entries.Count);
        var server = entries.Single(entry => entry.Address.ToString() == "192.168.0.154");
        Assert.Equal("9C6B00AABBCC", server.NormalisedMac);
        Assert.Equal("enp3s0", server.InterfaceName);
        Assert.Equal("STALE", server.State);
    }

    [Fact]
    public void Parse_WindowsArpOutput_SkipsHeadersAndBroadcastEntries()
    {
        const string output = """
            Interface: 192.168.0.50 --- 0xb
              Internet Address      Physical Address      Type
              192.168.0.1           44-d5-f2-11-22-33     dynamic
              192.168.0.154         9c-6b-00-aa-bb-cc     dynamic
              192.168.0.255         ff-ff-ff-ff-ff-ff     static
              224.0.0.22            01-00-5e-00-00-16     static
            """;

        var entries = ArpTableReader.Parse(output);

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, entry => entry.Address.ToString() == "192.168.0.154" && entry.NormalisedMac == "9C6B00AABBCC");
        Assert.DoesNotContain(entries, entry => entry.NormalisedMac == "FFFFFFFFFFFF");
    }

    [Fact]
    public void Parse_MacOsArpOutput_NormalisesAbbreviatedOctets()
    {
        // macOS prints 9c:6b:0:aa:bb:cc — the third octet loses its leading zero.
        const string output = """
            ? (192.168.0.1) at 44:d5:f2:11:22:33 on en0 ifscope [ethernet]
            ? (192.168.0.154) at 9c:6b:0:aa:bb:cc on en0 ifscope [ethernet]
            """;

        var entries = ArpTableReader.Parse(output);

        var server = entries.Single(entry => entry.Address.ToString() == "192.168.0.154");
        Assert.Equal("9C6B00AABBCC", server.NormalisedMac);
        Assert.Equal("en0", server.InterfaceName);
    }

    [Fact]
    public void Parse_DuplicateLines_AreCollapsed()
    {
        const string output = """
            192.168.0.154 dev eth0 lladdr 9c:6b:00:aa:bb:cc REACHABLE
            192.168.0.154 dev eth0 lladdr 9c:6b:00:aa:bb:cc STALE
            """;

        Assert.Single(ArpTableReader.Parse(output));
    }

    [Fact]
    public void Parse_EmptyOrIncompleteOutput_ReturnsEmpty()
    {
        Assert.Empty(ArpTableReader.Parse(null));
        Assert.Empty(ArpTableReader.Parse(""));
        Assert.Empty(ArpTableReader.Parse("192.168.0.9 dev eth0 INCOMPLETE"));
    }

    // An explicit command list keeps this independent of the host OS. An earlier version relied on
    // the platform list and passed on Linux while failing on the Windows runner, which then had
    // only one command to try.
    private static readonly (string FileName, string Arguments)[] TwoCommands =
        [("first", "--try"), ("second", "--try")];

    [Fact]
    public async Task ReadAsync_FallsBackToNextCommandWhenFirstProducesNothing()
    {
        var invoked = new List<string>();

        var reader = new ArpTableReader(
            (fileName, arguments, _, _) =>
            {
                invoked.Add($"{fileName} {arguments}".Trim());
                return Task.FromResult(invoked.Count == 1
                    ? string.Empty
                    : "192.168.0.154 dev eth0 lladdr 9c:6b:00:aa:bb:cc REACHABLE");
            },
            TwoCommands);

        var entries = await reader.ReadAsync();

        Assert.Equal(["first --try", "second --try"], invoked);
        Assert.Equal("192.168.0.154", Assert.Single(entries).Address.ToString());
    }

    [Fact]
    public async Task ReadAsync_StopsAtTheFirstCommandThatProducesEntries()
    {
        var invoked = new List<string>();

        var reader = new ArpTableReader(
            (fileName, arguments, _, _) =>
            {
                invoked.Add($"{fileName} {arguments}".Trim());
                return Task.FromResult("192.168.0.154 dev eth0 lladdr 9c:6b:00:aa:bb:cc REACHABLE");
            },
            TwoCommands);

        await reader.ReadAsync();

        Assert.Equal(["first --try"], invoked);
    }

    [Fact]
    public async Task ReadAsync_EveryCommandProducesNothing_ReturnsEmpty()
    {
        var reader = new ArpTableReader((_, _, _, _) => Task.FromResult(string.Empty), TwoCommands);

        Assert.Empty(await reader.ReadAsync());
    }

    [Fact]
    public void GetCommandsForPlatform_AlwaysOffersAFallback()
    {
        var commands = ArpTableReader.GetCommandsForPlatform();

        Assert.True(commands.Count >= 2, $"Expected a fallback, got: {string.Join(" | ", commands)}");
        Assert.All(commands, command => Assert.False(string.IsNullOrWhiteSpace(command.FileName)));
    }

    [Fact]
    public void Parse_WindowsNetshNeighborOutput_IsUnderstood()
    {
        // netsh is the Windows fallback when arp.exe yields nothing; its state words are mixed
        // case, unlike the upper-case states "ip neigh" emits.
        const string output = """
            Interface 11: Ethernet

            Internet Address                               Physical Address   Type
            ---------------------------------------------  -----------------  -----------
            192.168.0.1                                    44-d5-f2-11-22-33  Reachable (Router)
            192.168.0.154                                  9c-6b-00-aa-bb-cc  Stale
            """;

        var entries = ArpTableReader.Parse(output);

        Assert.Equal(2, entries.Count);
        var server = entries.Single(entry => entry.Address.ToString() == "192.168.0.154");
        Assert.Equal("9C6B00AABBCC", server.NormalisedMac);
        Assert.Equal("STALE", server.State);
    }

    [Theory]
    [InlineData("9c:6b:00:aa:bb:cc", "9C6B00AABBCC")]
    [InlineData("9C-6B-00-AA-BB-CC", "9C6B00AABBCC")]
    [InlineData("9c6b00aabbcc", "9C6B00AABBCC")]
    [InlineData("9c:6b:0:aa:bb:cc", "9C6B00AABBCC")]
    [InlineData("9C6B.00AA.BBCC", "9C6B00AABBCC")]
    public void Normalise_AcceptsEveryNotationTheAppEncounters(string input, string expected)
    {
        Assert.Equal(expected, MacAddressFormatter.Normalise(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-mac")]
    [InlineData("9c:6b:00:aa:bb")]
    public void Normalise_RejectsInputThatIsNotAFullMac(string? input)
    {
        Assert.Equal(string.Empty, MacAddressFormatter.Normalise(input));
    }

    [Fact]
    public void ToDisplayForm_RendersColonSeparatedUpperCase()
    {
        Assert.Equal("9C:6B:00:AA:BB:CC", MacAddressFormatter.ToDisplayForm("9c6b00aabbcc"));
    }
}
