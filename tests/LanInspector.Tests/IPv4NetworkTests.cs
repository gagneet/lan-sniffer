using System.Net;
using LanInspector.Core.Network;
using Xunit;

namespace LanInspector.Tests;

public sealed class IPv4NetworkTests
{
    [Fact]
    public void Constructor_HostAddress_IsMaskedToTheNetworkAddress()
    {
        // The constructor used to store the address unmasked, which left Contains matching nothing.
        var network = new IPv4Network(IPAddress.Parse("192.168.1.100"), 24);

        Assert.Equal("192.168.1.0", network.NetworkAddress.ToString());
        Assert.Equal("192.168.1.0/24", network.ToString());
    }

    [Fact]
    public void Constructor_AndFromAddressAndPrefix_Agree()
    {
        var viaConstructor = new IPv4Network(IPAddress.Parse("192.168.1.100"), 24);
        var viaFactory = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("192.168.1.100"), 24);

        Assert.Equal(viaFactory, viaConstructor);
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.1.254", true)]
    [InlineData("192.168.2.1", false)]
    [InlineData("10.0.0.1", false)]
    public void Contains_Slash24_MatchesOnlyThatSubnet(string address, bool expected)
    {
        var network = new IPv4Network(IPAddress.Parse("192.168.1.100"), 24);

        Assert.Equal(expected, network.Contains(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData(8, "10.255.255.255", true)]
    [InlineData(16, "10.0.255.255", true)]
    [InlineData(16, "10.1.0.0", false)]
    [InlineData(32, "10.0.0.1", true)]
    [InlineData(32, "10.0.0.2", false)]
    public void Contains_HonoursThePrefixLength(int prefix, string address, bool expected)
    {
        var network = new IPv4Network(IPAddress.Parse("10.0.0.1"), prefix);

        Assert.Equal(expected, network.Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void Contains_ZeroPrefix_MatchesEveryIpv4Address()
    {
        var network = new IPv4Network(IPAddress.Parse("0.0.0.0"), 0);

        Assert.True(network.Contains(IPAddress.Parse("8.8.8.8")));
        Assert.True(network.Contains(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Contains_Ipv6Address_IsFalseRatherThanThrowing()
    {
        var network = new IPv4Network(IPAddress.Parse("192.168.1.0"), 24);

        Assert.False(network.Contains(IPAddress.Parse("fe80::1")));
    }

    [Fact]
    public void Constructor_RejectsInputItCannotRepresent()
    {
        Assert.Throws<ArgumentException>(() => new IPv4Network(IPAddress.Parse("fe80::1"), 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IPv4Network(IPAddress.Parse("192.168.1.0"), 33));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IPv4Network(IPAddress.Parse("192.168.1.0"), -1));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.55", true)]
    [InlineData("192.168.1.100/24", "192.168.1.55", true)]
    [InlineData("10.0.0.0/8", "10.20.4.1", true)]
    [InlineData("10.0.0.0/8", "192.168.1.1", false)]
    public void TryParse_RoundTripsAndMatches(string cidr, string address, bool expected)
    {
        Assert.True(IPv4Network.TryParse(cidr, out var network));
        Assert.Equal(expected, network!.Contains(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("not-a-network")]
    [InlineData("192.168.1.0")]
    [InlineData("192.168.1.0/33")]
    [InlineData("fe80::/64")]
    public void TryParse_RejectsMalformedInput(string value)
    {
        Assert.False(IPv4Network.TryParse(value, out var network));
        Assert.Null(network);
    }
}
