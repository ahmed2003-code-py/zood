using System.Net;
using System.Net.Sockets;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Facts about the caller that are safe to store (Java <c>ClientInfo</c>). The address is the one the
/// platform's proxy reports (forwarded headers, trusted only from private ranges). Only a prefix is ever
/// stored (/24 for IPv4, /48 for IPv6).
/// </summary>
public static class ClientInfo
{
    private const int MaxUserAgent = 300;

    /// <summary>The full address, for rate limiting in memory only. Never store it.</summary>
    public static string? Address(HttpContext context) => Normalise(context.Connection.RemoteIpAddress)?.ToString();

    /// <summary>The subject of a per-address limit: IPv4 as is, IPv6 by its /64. Hash it before storing.</summary>
    public static string LimitKeyOf(IPAddress? address)
    {
        address = Normalise(address);
        if (address is null) return "unknown";
        if (address.AddressFamily == AddressFamily.InterNetwork) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return JavaHostAddress(bytes) + "/64";
    }

    /// <summary>An IPv6 client's /48, for a second, looser limit; null for IPv4.</summary>
    public static string? NetworkKeyOf(IPAddress? address)
    {
        address = Normalise(address);
        if (address is null || address.AddressFamily == AddressFamily.InterNetwork) return null;
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 6, 10);
        return JavaHostAddress(bytes) + "/48";
    }

    /// <summary>The stored prefix: <c>a.b.c.0/24</c> or <c>x:y:z::/48</c> (lower-case hex, no leading zeros).</summary>
    public static string? PrefixOf(IPAddress? address)
    {
        address = Normalise(address);
        if (address is null) return null;
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? $"{b[0]}.{b[1]}.{b[2]}.0/24"
            : $"{(b[0] << 8) | b[1]:x}:{(b[2] << 8) | b[3]:x}:{(b[4] << 8) | b[5]:x}::/48";
    }

    public static string? UserAgent(HttpContext context)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        if (agent.Length == 0) return null;
        var clean = new string(agent.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > MaxUserAgent ? clean[..MaxUserAgent] : clean;
    }

    /// <summary>
    /// An IPv6 address as Java's <c>InetAddress.getHostAddress()</c> writes it: eight groups, lower-case hex without
    /// leading zeros, never compressed (<c>2001:db8:1234:5678:0:0:0:0</c>, not <c>2001:db8:1234:5678::</c>). The limit
    /// keys are hashed into <c>rate_limit_counters</c>, which the Java and .NET instances share, so they are the same
    /// text.
    /// </summary>
    private static string JavaHostAddress(byte[] bytes) =>
        string.Join(':', Enumerable.Range(0, 8).Select(i => ((bytes[2 * i] << 8) | bytes[(2 * i) + 1]).ToString("x", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>An IPv4 address that Kestrel reports as IPv4-mapped IPv6 is the IPv4 address (Java sees it so).</summary>
    private static IPAddress? Normalise(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
