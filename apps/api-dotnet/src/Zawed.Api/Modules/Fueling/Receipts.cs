using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// Receipt links (PRD §6.1: the driver can get the receipt digitally; Java <c>Receipts</c>). A link is the fueling id and a
/// keyed signature of it: unguessable, unforgeable without the server key, and naming exactly one receipt. The receipt it
/// opens carries no ids and nothing usable elsewhere.
/// </summary>
public sealed class Receipts(LookupHasher signer, IRateLimiter limiter, ITransactions transactions, IServiceScopeFactory scopes)
{
    public const string Namespace = "receipt-link";
    private const int ReadsPerMinute = 30;

    /// <summary>The receipt's path under the API base, e.g. <c>/api/v1/receipts/{id}?sig=...</c>.</summary>
    public string PathOf(Fueling fueling) => ApiContract.BasePath + "/receipts/" + fueling.Id + "?sig=" + Signature(fueling.Id);

    /// <summary>The fueling a link names, when its signature is right. Any other link answers 404.</summary>
    public async Task<Fueling> OpenAsync(Guid id, string? sig, string? clientAddress, CancellationToken ct)
    {
        // Java String.valueOf(null) is "null": an unknown address shares one bucket.
        limiter.Acquire("receipt:" + SecureTokens.Sha256Hex(clientAddress ?? "null"), ReadsPerMinute, TimeSpan.FromMinutes(1));
        if (sig is null || !SecureTokens.ConstantTimeEquals(Signature(id), sig))
        {
            throw ApiException.NotFound("Receipt");
        }
        // Anonymous by design: the signature is the proof, checked above, and the read names one row.
        return await transactions.RunAsync(DbScopeContext.Escalate(Scope.Platform, null), async tx =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            session.Attach(tx);
            try
            {
                return await scope.ServiceProvider.GetRequiredService<Fuelings>().FindFuelingAsync(id, ct);
            }
            finally
            {
                session.Detach();
            }
        }, ct: ct) ?? throw ApiException.NotFound("Receipt");
    }

    private string Signature(Guid id) => signer.Hash(Namespace, id.ToString());
}
