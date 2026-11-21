using System.Net;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Leads.DemoFormApi;

namespace Zawed.Tests.Modules.Leads;

/// <summary>
/// The per-address limit with forwarded headers on, as the deployed profile runs (Java <c>DemoRequestBehindProxyTests</c>;
/// <c>PlatformSetup.TrustedProxyNetworks</c>, which include 127.0.0.0/8, so here the test client plays the platform's
/// proxy). A proxy appends the address it saw to <c>X-Forwarded-For</c>; the list is read from the right and stops at the
/// first address that is not a trusted proxy: that is the client. Whatever a client writes itself sits to the left of it
/// and is ignored. Each test uses client addresses of its own, so no counter needs deleting.
/// </summary>
[Collection(DemoFormCollection.Name)]
public sealed class DemoRequestBehindProxyTests(BehindProxyApi api) : IClassFixture<BehindProxyApi>
{
    [Fact]
    public async Task TheAddressIsTheOneTheProxySawNotTheOneTheClientWrote()
    {
        var client = Fresh();
        for (var i = 0; i < 5; i++)
        {
            // The client forges a different address each time; the proxy appends the one it saw.
            var response = await api.SubmitAsync(Form(), $"203.0.113.{i + 1}, {client}");
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        }
        // The forged addresses never gave a fresh budget.
        Assert.Equal((HttpStatusCode)429, (await api.SubmitAsync(Form(), $"192.0.2.77, {client}")).StatusCode);
    }

    [Fact]
    public async Task ForwardedHeadersAreReallyReadHere()
    {
        // The positive control: two clients behind the proxy have two budgets, so the test above could fail.
        var (first, second) = (Fresh(), Fresh());
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await api.SubmitAsync(Form(), first)).StatusCode);
        }
        Assert.Equal((HttpStatusCode)429, (await api.SubmitAsync(Form(), first)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await api.SubmitAsync(Form(), second)).StatusCode);
    }

    /// <summary>A public address of this run's own (TEST-NET-2 is reserved for documentation, so it is never a proxy's).</summary>
    private static string Fresh() => $"198.51.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
}
