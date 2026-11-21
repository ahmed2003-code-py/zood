using System.Text.RegularExpressions;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>What became of one item of a batch (Java <c>OfflineSync.Outcome</c>).</summary>
public enum SyncOutcome
{
    /// <summary>Recorded now.</summary>
    RECORDED,

    /// <summary>This device sent the same capture before; the stored fueling is returned.</summary>
    DUPLICATE,

    /// <summary>Nothing was recorded; the code says why.</summary>
    REJECTED,

    /// <summary>A fault on our side; nothing was recorded and the device keeps the item to send again.</summary>
    RETRY,
}

/// <param name="Pending">what the device still holds after this batch, as it reported it</param>
public sealed record OfflineBatch(Guid BatchId, int Pending, IReadOnlyList<OfflineRequest> Items);

/// <param name="Code">set when rejected, or the fault when retried</param>
/// <param name="Fueling">set when recorded or duplicate</param>
public sealed record SyncItemResult(Guid ClientTxnId, SyncOutcome Outcome, string? Code, Fueling? Fueling);

public sealed record SyncReceived(Guid BatchId, IReadOnlyList<SyncItemResult> Results);

/// <summary>
/// A batch of offline captures from one device (B5 contract §3; Java <c>OfflineSync</c>). Each item is recorded by the
/// engine in its own transaction, so an item that cannot be recorded never holds back the others, and a batch sent again
/// changes nothing: every item the server already has comes back <c>DUPLICATE</c>.
/// <para>An item that is refused is fuel that left the pump without a record. It raises a <c>SYNC_REJECTED</c> alert for
/// Operations, once however often the device repeats it, with the last four characters of the tag's UID and never the UID
/// itself.</para>
/// <para>A device has no user. The rejection alert and the batch row are written in the platform scope, for the device the
/// POS chain has just verified and naming only that device, its station and its operator (an escalation listed in
/// <c>ArchitectureTests</c>).</para>
/// </summary>
public sealed partial class OfflineSync(FuelingEngine engine, ITransactions transactions, IServiceScopeFactory scopes, IRateLimiter limiter,
    TimeProvider clock, ILogger<OfflineSync> log)
{
    public const int MaxItems = 100;

    /// <summary>
    /// A pump captures a fueling every minute or two, so a day offline is ten batches. This is far above that and stops a
    /// runaway terminal from keeping the engine busy.
    /// </summary>
    private const int BatchesPerMinute = 30;

    /// <summary>
    /// Why an item is refused (B5 contract §3). Each is a fact about the item that sending it again will not change. Anything
    /// else an item runs into is a fault on our side, not a rejection: the device keeps the item and sends it again.
    /// </summary>
    public static readonly IReadOnlySet<string> Rejections = new HashSet<string>
    {
        "TAG_NOT_RECOGNISED", "AGENT_NOT_OF_STATION", "CLIENT_TXN_CONFLICT", "IDEMPOTENCY_MISMATCH", "INVALID_VOLUME", "VALIDATION_FAILED",
    };

    /// <summary>A UID as the fleet accepts it; anything else has no "last four" worth showing.</summary>
    [GeneratedRegex("^[0-9A-F]{8,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Uid();

    /// <summary>Java's <c>[\s:\-]</c>: ASCII whitespace, colons and dashes.</summary>
    [GeneratedRegex("[ \\t\\n\\x0B\\f\\r:\\-]", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    /// <summary>
    /// Records the batch, item by item and in order, and answers one result per item in that order. The work is not tied to
    /// the request's cancellation: as in Java, a batch that has started is processed to the end and logged, so its row in the
    /// sync monitor always matches what was committed.
    /// </summary>
    /// <param name="pos">the device behind the request, from its token. An agent signed in on it now is ignored: each capture
    /// names its own agent, checked against the session history</param>
    public async Task<SyncReceived> ReceiveAsync(PosPrincipal pos, OfflineBatch batch)
    {
        var device = pos.DeviceOnly();
        limiter.Acquire("pos-sync:" + device.DeviceId, BatchesPerMinute, TimeSpan.FromMinutes(1));
        if (batch.Items.Count > MaxItems)
        {
            throw new ApiException(400, "BATCH_TOO_LARGE", "A batch holds at most " + MaxItems + " fuelings", "items",
                new Dictionary<string, object> { ["max"] = MaxItems });
        }
        var receivedAt = clock.GetUtcNow();
        var results = new List<SyncItemResult>();
        foreach (var item in batch.Items)
        {
            results.Add(await RecordAsync(device, batch.BatchId, item));
        }
        await AsPlatformAsync(tx => LogAsync(tx, device, batch, receivedAt, results));
        return new SyncReceived(batch.BatchId, results.AsReadOnly());
    }

    private async Task<SyncItemResult> RecordAsync(PosPrincipal device, Guid batchId, OfflineRequest item)
    {
        FuelingOutcome result;
        try
        {
            result = await engine.RecordOfflineAsync(device, item, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            if (!Rejections.Contains(ex.Code))
            {
                log.LogError(ex, "Offline capture {ClientTxnId} from device {DeviceId} failed with {Code}", item.ClientTxnId, device.DeviceId, ex.Code);
                return new SyncItemResult(item.ClientTxnId, SyncOutcome.RETRY, ex.Code, null);
            }
            // The item's own transaction rolled back and recorded nothing; the alert is its own.
            await AsPlatformAsync(async tx =>
            {
                await using var scope = scopes.CreateAsyncScope();
                var session = scope.ServiceProvider.GetRequiredService<DbSession>();
                session.Attach(tx);
                try
                {
                    await RejectedAsync(scope.ServiceProvider.GetRequiredService<Alerts>(), device, batchId, item, ex);
                }
                finally
                {
                    session.Detach();
                }
                return 0;
            });
            return new SyncItemResult(item.ClientTxnId, SyncOutcome.REJECTED, ex.Code, null);
        }
        catch (Exception ex)
        {
            // One item's fault must not hold back the rest of the batch, nor the queue behind it (B5 review 10).
            log.LogError(ex, "Offline capture {ClientTxnId} from device {DeviceId} failed", item.ClientTxnId, device.DeviceId);
            return new SyncItemResult(item.ClientTxnId, SyncOutcome.RETRY, ErrorCodes.InternalError, null);
        }
        return new SyncItemResult(item.ClientTxnId, result.Replayed ? SyncOutcome.DUPLICATE : SyncOutcome.RECORDED, null, result.Fueling);
    }

    /// <summary>
    /// Fuel was dispensed that the system could not record: Operations must know (B5 contract §4). For Operations only; a
    /// company is not shown a capture that could not be tied to it. The database keeps one alert per device, clientTxnId and
    /// code (V8), so a batch sent again does not raise it again.
    /// </summary>
    private static Task RejectedAsync(Alerts alerts, PosPrincipal device, Guid batchId, OfflineRequest item, ApiException reason)
    {
        var details = new Dictionary<string, object?>
        {
            ["code"] = reason.Code,
            ["clientTxnId"] = item.ClientTxnId.ToString(),
            ["uidLast4"] = UidLast4(item.TagUid),
            ["deviceCode"] = device.DeviceCode,
            ["batchId"] = batchId.ToString(),
            ["deviceAt"] = JavaInstant.Text(item.DeviceAt),
            ["volumeMl"] = item.VolumeMl,
            ["agentId"] = item.AgentId.ToString(),
        };
        if (item.FuelType is { } fuel)
        {
            details["fuelType"] = fuel.ToString();
        }
        foreach (var (key, value) in reason.Params ?? new Dictionary<string, object>())
        {
            details.TryAdd(key, value);
        }
        return alerts.RaiseAsync(AlertType.SYNC_REJECTED, AlertSeverity.CRITICAL,
            new AlertSubject(null, device.OperatorId, device.StationId, device.DeviceId, null, null), details, false, CancellationToken.None);
    }

    /// <summary>
    /// The last four characters of a tag's UID, the most any record outside the fleet's own encrypted column ever holds (plan
    /// §6.9). A string that is not a UID gives nothing: its tail could be most of it.
    /// </summary>
    public static string? UidLast4(string? rawUid)
    {
        var uid = JavaText.ToUpperRoot(Separators().Replace(rawUid ?? "", ""));
        return Uid().IsMatch(uid) ? uid[^4..] : null;
    }

    /// <summary>One row per request received, for the sync monitor (B5 contract §5); a batch sent again is logged again.</summary>
    private async Task<int> LogAsync(DbTx tx, PosPrincipal device, OfflineBatch batch, DateTimeOffset receivedAt, List<SyncItemResult> results) =>
        await tx.ExecuteAsync("""
            insert into sync_batches (id, device_batch_id, device_id, operator_id, station_id, received_at, item_count,
                                      recorded, duplicates, rejected, retried, with_conflicts, pending_reported)
            values (@id, @batch, @device, @operator, @station, @receivedAt, @items, @recorded, @duplicates, @rejected,
                    @retried, @withConflicts, @pending)
            """, new
        {
            id = Guid.CreateVersion7(clock.GetUtcNow()),
            batch = batch.BatchId,
            device = device.DeviceId,
            @operator = device.OperatorId,
            station = device.StationId,
            receivedAt = receivedAt.UtcDateTime,
            items = results.Count,
            recorded = Count(results, SyncOutcome.RECORDED),
            duplicates = Count(results, SyncOutcome.DUPLICATE),
            rejected = Count(results, SyncOutcome.REJECTED),
            retried = Count(results, SyncOutcome.RETRY),
            // Newly recorded with a conflict: a duplicate's conflicts were counted when it was recorded.
            withConflicts = results.Count(r => r.Outcome == SyncOutcome.RECORDED && r.Fueling!.Conflicts.Count > 0),
            pending = batch.Pending,
        }, CancellationToken.None);

    private static int Count(List<SyncItemResult> results, SyncOutcome outcome) => results.Count(r => r.Outcome == outcome);

    /// <summary>The platform scope, without a user, for the device the POS chain verified (Java <c>OfflineSync.asPlatform</c>).</summary>
    private Task<T> AsPlatformAsync<T>(Func<DbTx, Task<T>> work) =>
        transactions.RunAsync(DbScopeContext.Escalate(Scope.Platform, null), work, ct: CancellationToken.None);
}
