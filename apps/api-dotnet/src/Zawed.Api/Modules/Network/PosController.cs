using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// The POS terminal's credentials (B4 contract §1, ADR 0010; Java <c>PosController</c>): activation, device tokens, and the
/// agent's sign-in on the device. Every answer that carries a credential is <c>no-store</c>. Who may call what is the POS
/// chain's decision (<c>PosChannel</c>, by path, as Java's <c>posSecurity</c>); each step runs its own platform-scope
/// transactions after its proof, so the request transaction is not opened.
/// </summary>
[Route("api/v1/pos"), Tags("pos")]
public sealed class PosController(DeviceChannel channel, AgentSessions sessions) : ApiControllerBase
{
    private PosPrincipal Principal => PosPrincipal.From(User)!;

    [HttpPost("activate", Name = "activatePosDevice"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("Redeem the one-time activation code and register the device key")]
    [ProducesResponseType<ActivateResponse>(200)]
    public async Task<IActionResult> Activate([FromBody] ActivateRequest body, CancellationToken ct)
    {
        var device = await channel.ActivateAsync(body.DeviceCode, body.ActivationCode, body.PublicKey, ClientInfo.Address(HttpContext), ct);
        return NoStore(new ActivateResponse(device.DeviceId, device.Code, device.StationId));
    }

    [HttpPost("token", Name = "issuePosDeviceToken"), AllowAnonymous, OwnTransactions]
    [EndpointSummary("A 15-minute device token for a signed timestamp and nonce")]
    [ProducesResponseType<DeviceTokenResponse>(200)]
    public async Task<IActionResult> Token([FromBody] DeviceTokenRequest body, CancellationToken ct)
    {
        var issued = await channel.TokenAsync(body.DeviceId!.Value, body.Timestamp!.Value, body.Nonce, body.Signature,
            ClientInfo.Address(HttpContext), ct);
        return NoStore(new DeviceTokenResponse(issued.Token, issued.ExpiresAt));
    }

    [HttpPost("agents/login", Name = "posAgentLogin"), OwnTransactions]
    [EndpointSummary("Agent sign-in on this device with Worker ID and PIN")]
    [ProducesResponseType<AgentLoginResponse>(200)]
    public async Task<IActionResult> AgentLogin([FromBody] AgentLoginRequest body, CancellationToken ct)
    {
        var signedIn = await sessions.SignInAsync(PosCredentials.DeviceOf(Principal), body.WorkerId, body.Pin, ct);
        var agent = signedIn.Agent;
        return NoStore(new AgentLoginResponse(signedIn.SessionToken, signedIn.ExpiresAt,
            new PosAgentResponse(agent.AgentId, agent.WorkerId, agent.FullName, agent.MustChangePin)));
    }

    /// <summary>Java documents 200 although the answer is 204.</summary>
    [HttpPost("agents/pin", Name = "posAgentChangePin"), OwnTransactions]
    [EndpointSummary("The signed-in agent replaces their PIN")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> ChangePin([FromBody] ChangePinRequest body, CancellationToken ct)
    {
        var principal = Principal;
        await sessions.ChangePinAsync(PosCredentials.DeviceOf(principal), PosCredentials.AgentOf(principal), body.CurrentPin, body.NewPin, ct);
        return NoContent();
    }

    /// <summary>Java documents 200 although the answer is 204.</summary>
    [HttpPost("agents/logout", Name = "posAgentLogout"), OwnTransactions]
    [EndpointSummary("End the agent's session on this device")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> AgentLogout(CancellationToken ct)
    {
        // The chain let this through only with a live session, so the header is there.
        await sessions.SignOutAsync(Rows.JavaStrip(Request.Headers[PosCredentials.AgentHeader][0]!), ct);
        return NoContent();
    }

    private OkObjectResult NoStore<T>(T body)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(body);
    }
}
