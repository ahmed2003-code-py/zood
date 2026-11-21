using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// order), which is how the Java API hashed it with Jackson; a module that ports a keyed create checks its
/// hash against a Java-made one.
/// </summary>
public sealed class IdempotencyKeys(IDbSession db, TimeProvider clock)
{
    public const string Header = "Idempotency-Key";
    public const string InvalidKey = "INVALID_IDEMPOTENCY_KEY";

    public async Task<Guid?> ClaimAsync(Guid actorId, string operation, string? key, object request, Guid newId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        var trimmed = key.Trim();
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

    public Task<Guid?> ClaimRequiredAsync(Guid actorId, string operation, string? key, object request, Guid newId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new ApiException(400, InvalidKey,
                "This request must carry an Idempotency-Key header, so that a retry is never a second request", Header)
            : ClaimAsync(actorId, operation, key, request, newId, ct);

    public static string Hash(object request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, request.GetType(), HashOptions))));

    /// <summary>
    /// The wire rules, but escaping only what JSON requires, as Jackson does: Arabic text and <c>&lt; &gt; &amp; '</c>
    /// stay as they are, so a body hashes as the Java API hashed it (checked by <c>CompanyIdempotencyTests</c>).
    /// </summary>
    private static readonly JsonSerializerOptions HashOptions = new(ZawedJson.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
