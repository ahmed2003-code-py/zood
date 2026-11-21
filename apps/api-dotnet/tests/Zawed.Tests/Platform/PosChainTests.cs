using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Tests.Platform;

/// <summary>
/// The POS half of the Java <c>SecurityFiltersPathMatchingTests</c> (the second layer of B7 "path matching") and the POS
/// chain's rules (Java <c>SecurityConfig.posSecurity</c>, <c>SecurityErrorHandler.posAccessDenied</c>): even for a request
/// that reached them with a non-canonical raw target (were <c>CanonicalPath</c> ever removed), the public POS paths are
/// matched on the path as routing reads it, and only for POST.
/// </summary>
public sealed class PosChainTests
{
    [Theory]
    [InlineData("/api/v1/pos/activate", "/api/v1/pos/activate")]
    [InlineData("/api/v1/pos/activat%65", "/api/v1/pos/activate")]
    [InlineData("/api/v1/pos/toke%6E", "/api/v1/pos/token")]
    public void ThePosPublicPathsAreMatchedAsRoutedAndOnlyForPost(string raw, string routed)
    {
        Assert.True(PosChannel.IsPublic(Request("POST", raw, routed).Request));
        Assert.False(PosChannel.IsPublic(Request("GET", raw, routed).Request));
    }

    [Fact]
    public void AnEncodedPosPathIsStillThePosChain()
    {
        var sync = Request("POST", "/api/v1/po%73/sync", "/api/v1/pos/sync");
        Assert.True(PosChannel.IsPosPath(sync.Request.Path));
        Assert.False(PosChannel.IsPublic(sync.Request));
    }

    [Theory]
    [InlineData("/api/v1/pos", true)]
    [InlineData("/api/v1/pos/agents/login", true)]
    [InlineData("/api/v1/POS/agents/login", false)] // case-sensitive, as Spring's matcher
    [InlineData("/api/v1/posx", false)]
    [InlineData("/api/v1/ops/devices", false)]
    public void ThePosChainIsTheSubtreeOfPos(string path, bool pos) => Assert.Equal(pos, PosChannel.IsPosPath(path));

    [Theory]
    // The device alone: agent sign-in, and the B5 snapshot and sync.
    [InlineData("device", "POST", "/api/v1/pos/agents/login", null)]
    [InlineData("device", "GET", "/api/v1/pos/snapshot", null)]
    [InlineData("device", "POST", "/api/v1/pos/sync", null)]
    [InlineData("device", "POST", "/api/v1/pos/agents/pin", "AGENT_SESSION_REQUIRED")]
    [InlineData("device", "POST", "/api/v1/pos/agents/logout", "AGENT_SESSION_REQUIRED")]
    [InlineData("device", "POST", "/api/v1/pos/fuelings", "AGENT_SESSION_REQUIRED")]
    [InlineData("device", "GET", "/api/v1/pos/agents/login", "AGENT_SESSION_REQUIRED")] // another method is another rule
    [InlineData("device", "POST", "/api/v1/pos/snapshot", "AGENT_SESSION_REQUIRED")]
    // An agent who must change the PIN: only the PIN change and sign-out, and what the device alone may do.
    [InlineData("pin-change", "POST", "/api/v1/pos/agents/pin", null)]
    [InlineData("pin-change", "POST", "/api/v1/pos/agents/logout", null)]
    [InlineData("pin-change", "POST", "/api/v1/pos/agents/login", null)]
    [InlineData("pin-change", "GET", "/api/v1/pos/snapshot", null)]
    [InlineData("pin-change", "POST", "/api/v1/pos/fuelings", "PIN_CHANGE_REQUIRED")]
    [InlineData("pin-change", "POST", "/api/v1/pos/fuelings/x/resolve", "PIN_CHANGE_REQUIRED")]
    // A working agent: everything, unknown routes included (they then answer 404).
    [InlineData("agent", "POST", "/api/v1/pos/fuelings", null)]
    [InlineData("agent", "POST", "/api/v1/pos/agents/pin", null)]
    [InlineData("agent", "GET", "/api/v1/pos/anything", null)]
    public void TheChainsRulesTellTheTerminalWhatToShowNext(string who, string method, string path, string? refusal)
    {
        var context = Request(method, path, path);
        var authorities = who switch
        {
            "device" => new[] { PosChannel.Device },
            "pin-change" => [PosChannel.Device, PosChannel.AgentPinChange],
            _ => [PosChannel.Device, PosChannel.Agent],
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(authorities.Select(a => new Claim(PosChannel.AuthorityClaim, a)), "test"));
        var refused = PosChannel.Refusal(context.Request, user);
        Assert.Equal(refusal, refused?.Code);
        if (refused is not null)
        {
            Assert.Equal(refusal == "PIN_CHANGE_REQUIRED" ? 403 : 401, refused.Status);
        }
    }

    private static DefaultHttpContext Request(string method, string rawTarget, string routedPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = routedPath; // what Kestrel hands routing: the decoded path
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        Assert.Equal(rawTarget, CanonicalPath.RawPath(context));
        return context;
    }
}
