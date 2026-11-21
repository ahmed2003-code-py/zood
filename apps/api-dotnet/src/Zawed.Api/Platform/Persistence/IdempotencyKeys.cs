using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// <c>Idempotency-Key</c> for creates without a natural key (V13, ADR 0022; Java <c>IdempotencyKeys</c>).
/// Unlike the playbook's response-replaying filter, ZAWED stores the resource a key created and the
/// SHA-256 of the request body, per user and operation, in the transaction that creates the resource:
/// <list type="bullet">
/// <item><see cref="ClaimAsync"/> returns null for "go ahead and create <c>newId</c>", or the id an earlier
/// request with the same key and the same body created, which the caller returns as it is;</item>
/// <item>the same key with another body is <c>409 IDEMPOTENCY_KEY_REUSED</c>; a rollback frees the key; a
/// concurrent request with the same key waits on the primary key, then sees the first result;</item>
/// <item>where a duplicate would move money, <see cref="ClaimRequiredAsync"/>: no key is
/// <c>400 INVALID_IDEMPOTENCY_KEY</c> on field <c>Idempotency-Key</c>. It is called from the service, after the
/// permission check, so a caller without the permission still gets 403.</item>
/// </list>
/// The body hash is SHA-256 hex of the request serialised with the wire JSON rules (camelCase, declaration
/// order) and Jackson's string escaping (<see cref="Json"/>), which is how the Java API hashed it; a module that ports a
/// keyed create checks its hash against a Java-made one. The key is read with Java's <c>isBlank()</c> and
/// <c>strip()</c> (<see cref="JavaText"/>), so a key either instance stored is the same key on the other.
/// </summary>
public sealed class IdempotencyKeys(IDbSession db, TimeProvider clock)
{
    public const string Header = "Idempotency-Key";
    public const string InvalidKey = "INVALID_IDEMPOTENCY_KEY";

    public async Task<Guid?> ClaimAsync(Guid actorId, string operation, string? key, object request, Guid newId, CancellationToken ct = default)
    {
        if (key is null || JavaText.IsBlank(key))
        {
            return null;
        }
        var trimmed = JavaText.Strip(key);
        if (trimmed.Length is < 8 or > 200)
        {
            throw new ApiException(400, InvalidKey, "The Idempotency-Key must be 8 to 200 characters", Header);
        }
        var hash = Hash(request);
        var tx = db.Current;
        var inserted = await tx.ExecuteAsync("""
            insert into idempotency_keys (actor_id, operation, key, request_hash, resource_id, created_at)
            values (@actorId, @operation, @key, @hash, @newId, @now)
            on conflict do nothing
            """, new { actorId, operation, key = trimmed, hash, newId, now = clock.GetUtcNow().UtcDateTime }, ct);
        if (inserted == 1)
        {
            return null;
        }
        var stored = await tx.QuerySingleAsync<(string RequestHash, Guid ResourceId)>(
            "select request_hash, resource_id from idempotency_keys where actor_id = @actorId and operation = @operation and key = @key",
            new { actorId, operation, key = trimmed }, ct);
        if (stored.RequestHash != hash)
        {
            throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request", Header);
        }
        return stored.ResourceId;
    }

    /// <summary>
    /// The header as the client sent it, for an action that binds it with <c>[FromHeader]</c>. MVC's binding turns a
    /// value that .NET counts as white space into null, and .NET counts the no-break spaces; Java does not, so to Java
    /// <c>"\u00A0\u00A0\u00A0"</c> is a key (too short: 400), not the absence of one.
    /// </summary>
    public static string? KeyOf(Microsoft.AspNetCore.Http.HttpRequest request, string? bound) =>
        bound ?? (request.Headers.TryGetValue(Header, out var values) && values.Count > 0 ? values[0] : null);

    public Task<Guid?> ClaimRequiredAsync(Guid actorId, string operation, string? key, object request, Guid newId, CancellationToken ct = default) =>
        key is null || JavaText.IsBlank(key)
            ? throw new ApiException(400, InvalidKey,
                "This request must carry an Idempotency-Key header, so that a retry is never a second request", Header)
            : ClaimAsync(actorId, operation, key, request, newId, ct);

    public static string Hash(object request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json(request))));

    /// <summary>
    /// The request as Jackson 3's <c>writeValueAsString</c> wrote it: the wire JSON (names, order, nulls and numbers as
    /// <see cref="ZawedJson.Options"/> write them), with every string escaped as Jackson escapes it. Jackson escapes only
    /// <c>"</c>, <c>\</c> and U+0000 to U+001F (<c>\b \t \n \f \r</c>, otherwise <c>\u00XX</c> in upper case) and
    /// writes everything else as it is: DEL, the no-break spaces, U+2028 and U+2029, the BOM, characters outside the BMP,
    /// Arabic, and <c>&lt; &gt; &amp; '</c>. No <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> does that
    /// (even the relaxed one escapes the no-break space and every surrogate pair), so the strings are written here.
    /// A lone surrogate cannot arrive: the request was read by System.Text.Json, which refuses one.
    /// </summary>
    public static string Json(object request)
    {
        var json = new StringBuilder();
        Write(JsonSerializer.SerializeToElement(request, request.GetType(), ZawedJson.Options), json);
        return json.ToString();
    }

    private static void Write(JsonElement element, StringBuilder json)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!firstProperty)
                    {
                        json.Append(',');
                    }
                    firstProperty = false;
                    WriteString(property.Name, json);
                    json.Append(':');
                    Write(property.Value, json);
                }
                json.Append('}');
                break;
            case JsonValueKind.Array:
                json.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        json.Append(',');
                    }
                    firstItem = false;
                    Write(item, json);
                }
                json.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, json);
                break;
            default:
                // Numbers, true, false and null, as System.Text.Json wrote them (whole numbers only on this API).
                json.Append(element.GetRawText());
                break;
        }
    }

    private static void WriteString(string value, StringBuilder json)
    {
        json.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\t': json.Append("\\t"); break;
                case '\n': json.Append("\\n"); break;
                case '\f': json.Append("\\f"); break;
                case '\r': json.Append("\\r"); break;
                case < ' ': json.Append("\\u00").Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: json.Append(c); break;
            }
        }
        json.Append('"');
    }
}
