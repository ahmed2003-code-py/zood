using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// Issues access tokens (Java <c>AccessTokens</c>): short-lived HS256 JWTs. They are only an index into the session
/// table, which is read on every request; role and scope in the token are informational for clients.
/// </summary>
public sealed class AccessTokens(IOptions<ZawedOptions> options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly SigningCredentials credentials =
        new(new SymmetricSecurityKey(options.Value.Security.JwtSecretBytes()), SecurityAlgorithms.HmacSha256);

    private readonly JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };

    public string Issue(Guid userId, Guid sessionId, Role role, Scope scope)
    {
        var now = clock.GetUtcNow();
        var scopeClaim = new Dictionary<string, object> { ["type"] = scope.Type.ToString() };
        if (scope.Id is { } id)
        {
            scopeClaim["id"] = id.ToString();
        }
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authentication.TokenIssuer,
            IssuedAt = now.UtcDateTime,
            Expires = (now + Lifetime).UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["jti"] = Guid.CreateVersion7(now).ToString(),
                ["typ"] = Authentication.UserTokenType,
                ["sid"] = sessionId.ToString(),
                ["role"] = role.ToString(),
                ["scope"] = scopeClaim,
            },
        });
    }
}
