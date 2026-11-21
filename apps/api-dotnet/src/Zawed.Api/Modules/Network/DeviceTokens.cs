using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Network;

/// <summary>A device token and when it stops working.</summary>
public sealed record IssuedDeviceToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Device tokens (ADR 0010; Java <c>network.DeviceTokens</c>): short-lived HS256 JWTs with <c>typ=device</c>, signed with a
/// key derived from the JWT secret under its own label (<c>HMAC-SHA256(secret, "zawed-device-token-v1")</c>). A device
/// token therefore fails a user token's signature check, and a user token fails a device token's, before either type
/// check runs: the two can never stand in for each other. The token names the device only; whether the device is still
/// active is read from the database on every request.
/// <para>Written as Nimbus wrote it (header <c>{"alg":"HS256"}</c>; <c>iss</c>, <c>sub</c>, <c>jti</c>, <c>iat</c>, <c>exp</c>
/// in seconds, <c>typ</c>), and read as Spring read it (HS256 only, issuer <c>zawed-api</c>, the time window with 30 seconds
/// of skew, <c>typ=device</c> and a subject), so a token issued by either API is accepted by the other across the cutover.</para>
/// </summary>
public sealed class DeviceTokens(IOptions<ZawedOptions> options, TimeProvider clock)
{
    public const string DeviceTokenType = "device";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly byte[] key = HMACSHA256.HashData(options.Value.Security.JwtSecretBytes(), "zawed-device-token-v1"u8);
    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };

    public IssuedDeviceToken Issue(Guid deviceId)
    {
        var now = Micros(clock.GetUtcNow());
        var expires = now + Lifetime;
        using var payload = new MemoryStream();
        using (var json = new Utf8JsonWriter(payload))
        {
            json.WriteStartObject();
            json.WriteString("iss", Authentication.TokenIssuer);
            json.WriteString("sub", deviceId.ToString());
            json.WriteString("jti", Guid.CreateVersion7(now).ToString());
            json.WriteNumber("iat", now.ToUnixTimeSeconds());
            json.WriteNumber("exp", expires.ToUnixTimeSeconds());
            json.WriteString("typ", DeviceTokenType);
            json.WriteEndObject();
        }
        var signingInput = Base64UrlEncoder.Encode("{\"alg\":\"HS256\"}"u8.ToArray()) + "." + Base64UrlEncoder.Encode(payload.ToArray());
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));
        return new IssuedDeviceToken(signingInput + "." + Base64UrlEncoder.Encode(signature), expires);
    }

    /// <summary>The device a token names, or null for any token that is not a valid, current device token.</summary>
    public async Task<Guid?> DeviceOfAsync(string token)
    {
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidIssuer = Authentication.TokenIssuer,
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = false, // Spring's JwtTimestampValidator checks exp and nbf when present
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return (expires is null || now - parameters.ClockSkew <= expires) && (notBefore is null || now + parameters.ClockSkew >= notBefore);
            },
        });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }
        var typ = jwt.TryGetPayloadValue<string>("typ", out var t) ? t : null;
        return typ == DeviceTokenType && Guid.TryParse(jwt.Subject, out var device) ? device : null;
    }

    /// <summary>Microsecond precision, as Java's system clock and PostgreSQL keep it.</summary>
    internal static DateTimeOffset Micros(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));
}
