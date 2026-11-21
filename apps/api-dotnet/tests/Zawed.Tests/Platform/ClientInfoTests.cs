using System.Net;
using Zawed.Api.Platform.Web;

namespace Zawed.Tests.Platform;

/// <summary>
/// Java <c>ClientInfoTests</c>: the subject of a per-address limit (U1 demo form), IPv4 as it is, IPv6 by its /64.
/// The .NET methods take the address Kestrel reports, so text that is not an address arrives as none.
/// </summary>
public sealed class ClientInfoTests
{
    private static IPAddress? Address(string? text) => IPAddress.TryParse(text, out var address) ? address : null;

    [Fact]
    public void AnIpv4AddressIsLimitedAsItIs()
    {
        Assert.Equal("203.0.113.7", ClientInfo.LimitKeyOf(Address("203.0.113.7")));
        Assert.Equal("203.0.113.7", ClientInfo.LimitKeyOf(Address("::ffff:203.0.113.7")));
    }

    [Fact]
    public void EveryAddressOfOneIpv6Slash64SharesOneBudget()
    {
        var a = ClientInfo.LimitKeyOf(Address("2001:db8:1234:5678:0:0:0:1"));
        var b = ClientInfo.LimitKeyOf(Address("2001:db8:1234:5678:abcd:ef01:2345:6789"));
        var otherNetwork = ClientInfo.LimitKeyOf(Address("2001:db8:1234:5679::1"));
        Assert.Equal(a, b);
        Assert.EndsWith("/64", a);
        Assert.NotEqual(a, otherNetwork);
    }

    [Fact]
    public void AnIpv6ClientAlsoCountsInItsSlash48AndIpv4HasNoWiderNetwork()
    {
        var a = ClientInfo.NetworkKeyOf(Address("2001:db8:1234:5678::1"));
        var b = ClientInfo.NetworkKeyOf(Address("2001:db8:1234:ffff:1:2:3:4"));
        Assert.Equal(a, b);
        Assert.EndsWith("/48", a);
        Assert.NotEqual(a, ClientInfo.NetworkKeyOf(Address("2001:db8:1235::1")));
        Assert.Null(ClientInfo.NetworkKeyOf(Address("203.0.113.7")));
        Assert.Null(ClientInfo.NetworkKeyOf(Address("::ffff:203.0.113.7")));
        Assert.Null(ClientInfo.NetworkKeyOf(Address("junk")));
    }

    [Fact]
    public void TheKeysAreTheTextTheJavaApiHashes()
    {
        // Java's InetAddress.getHostAddress (checked with jshell): the counters in rate_limit_counters are shared.
        Assert.Equal("2001:db8:1234:5678:0:0:0:0/64", ClientInfo.LimitKeyOf(Address("2001:db8:1234:5678::1")));
        Assert.Equal("2001:db8:1234:0:0:0:0:0/48", ClientInfo.NetworkKeyOf(Address("2001:0db8:1234:5678::1")));
    }

    [Fact]
    public void AnythingElseIsOneSharedUnknownBucket()
    {
        Assert.Equal("unknown", ClientInfo.LimitKeyOf(null));
        Assert.Equal("unknown", ClientInfo.LimitKeyOf(Address("not an address")));
    }
}
