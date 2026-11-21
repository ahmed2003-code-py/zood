using System.Globalization;
using System.Text.Json;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>One capture in the device's queue (Java <c>OfflineTestSupport.Capture</c>).</summary>
public sealed record Capture(Guid ClientTxnId, string TagUid, long VolumeMl, string? Fuel, DateTimeOffset DeviceAt, string AgentId)
{
    public Capture At(DateTimeOffset when) => this with { DeviceAt = when };

    public Capture WithFuel(string selected) => this with { Fuel = selected };

    public Capture By(string otherAgentId) => this with { AgentId = otherAgentId };

    public Capture Volume(long otherVolumeMl) => this with { VolumeMl = otherVolumeMl };

    public string Json() =>
        "{\"clientTxnId\":\"" + ClientTxnId + "\",\"tagUid\":\"" + TagUid + "\",\"volumeMl\":" + VolumeMl
        + (Fuel is null ? "" : ",\"fuelType\":\"" + Fuel + "\"") + ",\"deviceAt\":\"" + Instant(DeviceAt)
        + "\",\"agentId\":\"" + AgentId + "\",\"receiptLocale\":\"ar\"}";

    /// <summary>As Java's <c>Instant.toString()</c> would send it (millisecond-or-finer ISO-8601, UTC).</summary>
    public static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// What a terminal does after it was offline (B5 contract §3; Java <c>OfflineTestSupport</c>): it holds captures and sends
/// them in a batch with its device token alone. Also the two things only history can give a test: a shift that is already
/// over, and a price that was in force before today. Create one per test: it resets the in-memory rate limits.
/// </summary>
public sealed class OfflineTestSupport(ZawedApiFactory factory)
{
    public FuelingTestSupport F { get; } = new(factory);

    public Owner Owner => F.Owner;

    public async Task<string> AgentIdAsync(Pump pump) =>
        (await Owner.QueryOneAsync<Guid>("select id from agents where worker_id = @w", new { w = pump.WorkerId })).ToString();

    /// <summary>A capture made now by the pump's agent, who is signed in on it.</summary>
    public async Task<Capture> CaptureAsync(Pump pump, Car car, long volumeMl) =>
        new(Guid.NewGuid(), car.TagUid, volumeMl, null, DateTimeOffset.UtcNow, await AgentIdAsync(pump));

    /// <summary>Sends a batch as the device alone: no agent session, as after a shift.</summary>
    public Task<HttpResponseMessage> SyncAsync(Pump pump, params Capture[] captures) => SyncAsync(pump, Guid.NewGuid(), 0, captures);

    public Task<HttpResponseMessage> SyncAsync(Pump pump, Guid batchId, int pending, IReadOnlyList<Capture> captures) =>
        F.Pos.PosAsync("POST", "/api/v1/pos/sync", pump.DeviceToken, null, BatchBody(batchId, pending, captures));

    public static string BatchBody(Guid batchId, int pending, IReadOnlyList<Capture> captures) =>
        "{\"batchId\":\"" + batchId + "\",\"pending\":" + pending + ",\"items\":[" + string.Join(",", captures.Select(c => c.Json())) + "]}";

    /// <summary>The answered batch's body (a 200).</summary>
    public static async Task<JsonElement> AnsweredAsync(HttpResponseMessage response) => await FuelingTestSupport.BodyAsync(response, 200);

    /// <summary>The results of a batch that was answered, in item order.</summary>
    public static async Task<List<string>> ResultsAsync(HttpResponseMessage response) =>
        (await AnsweredAsync(response)).GetProperty("results").EnumerateArray().Select(r => r.GetProperty("result").GetString()!).ToList();

    public static JsonElement Item(JsonElement body, int index) => body.GetProperty("results")[index];

    public static string Rejection(JsonElement body, int index)
    {
        Assert.Equal("REJECTED", Item(body, index).GetProperty("result").GetString());
        return Item(body, index).GetProperty("code").GetString()!;
    }

    public static List<string> Conflicts(JsonElement body, int index)
    {
        Assert.True(Item(body, index).GetProperty("result").GetString() == "RECORDED", body.ToString());
        return Item(body, index).GetProperty("fueling").GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    public static string FuelingId(JsonElement body, int index) => Item(body, index).GetProperty("fueling").GetProperty("id").GetString()!;

    /// <summary>
    /// A shift of the pump's agent on its device that is over, as the session history holds it. A capture dated inside it is
    /// one the agent made then and the device kept until now.
    /// </summary>
    public async Task PastShiftAsync(Pump pump, DateTimeOffset from, DateTimeOffset to) =>
        await Owner.ExecuteAsync("""
            insert into agent_sessions (id, agent_id, device_id, operator_id, station_id, token_hash, created_at,
                                        last_seen_at, expires_at, revoked_at, revoke_reason)
            values (@id, @agent::uuid, @device::uuid, @operator::uuid, @station::uuid, @hash, @from, @to, @expires, @to, 'SIGNED_OUT')
            """, new
        {
            id = Guid.NewGuid(),
            agent = await AgentIdAsync(pump),
            device = pump.DeviceId,
            @operator = pump.OperatorId,
            station = pump.StationId,
            hash = "past-" + Guid.NewGuid(),
            from = from.UtcDateTime,
            to = to.UtcDateTime,
            expires = from.AddHours(12).UtcDateTime,
        });

    /// <summary>A station price that took effect in the past (the API only schedules from now on).</summary>
    public Task PriceSinceAsync(string station, string fuel, long piastres, DateTimeOffset since) =>
        Owner.ExecuteAsync("""
            insert into fuel_prices (id, fuel_type, station_id, price_piastres, effective_from, created_at)
            values (@id, @fuel, @station::uuid, @piastres, @since, now())
            """, new { id = Guid.NewGuid(), fuel, station, piastres, since = since.UtcDateTime });

    // ------------------------------------------------------------------------------------------------- the books

    public Task<long> WalletBalanceAsync(string company) => F.WalletBalanceAsync(company);

    /// <summary>Credits minus debits on one ledger account, from its entries; 0 when it was never opened.</summary>
    public async Task<long> AccountBalanceAsync(string code) => await Owner.QueryOneAsync<long>("""
        select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
        from ledger_entries e join ledger_accounts a on a.id = e.account_id where a.code = @code
        """, new { code });

    public Task<long> WalletLedgerAsync(string company) => AccountBalanceAsync("company:" + company + ":wallet");

    /// <summary>What the company owes: the debit balance of its receivable.</summary>
    public async Task<long> OwedAsync(string company) => -await AccountBalanceAsync("company:" + company + ":receivable");

    public Task<long> StationPayableAsync(string station) => AccountBalanceAsync("station:" + station + ":payable");

    public Task<long> CountAsync(string sql, object? args = null) => F.CountAsync(sql, args);

    /// <summary>One row as column → value.</summary>
    public async Task<IDictionary<string, object?>> RowAsync(string sql, object? args = null) =>
        (IDictionary<string, object?>)(await Owner.QueryAsync<dynamic>(sql, args)).Single();

    public async Task<List<IDictionary<string, object?>>> RowsAsync(string sql, object? args = null) =>
        (await Owner.QueryAsync<dynamic>(sql, args)).Select(r => (IDictionary<string, object?>)r).ToList();

    public Task<IDictionary<string, object?>> FuelingRowAsync(string id) => RowAsync("select * from fuelings where id = @id::uuid", new { id });

    /// <summary>The device's batches as the sync monitor reads them, oldest first.</summary>
    public Task<List<IDictionary<string, object?>>> BatchRowsAsync(Pump pump) =>
        RowsAsync("select * from sync_batches where device_id = @d::uuid order by id", new { d = pump.DeviceId });

    /// <summary>The entries of one ledger transaction as "DIRECTION account-code amount".</summary>
    public Task<List<string>> EntriesAsync(Guid transactionId) => Owner.QueryAsync<string>("""
        select e.direction || ' ' || a.code || ' ' || e.amount_piastres
        from ledger_entries e join ledger_accounts a on a.id = e.account_id where e.transaction_id = @t
        """, new { t = transactionId });
}
