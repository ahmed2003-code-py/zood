using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// The application for the ledger tests: the shared database, two receiving accounts (MAIN, CIB) and a label for MAIN
/// (the Java tests' <c>@TestPropertySource</c>), and a recorder of the ledger's domain events, so a test can prove an
/// event was published once and only once (wave 3 handles them).
/// </summary>
public sealed class LedgerApi : IAsyncLifetime
{
    public const string MainLabel = "Banque Misr ••4821";

    public ZawedApiFactory Factory { get; } = new RecordingFactory();

    public Owner Owner => Factory.Owner;

    public DomainEventLog Events => Factory.Services.GetRequiredService<DomainEventLog>();

    public Task InitializeAsync()
    {
        _ = Factory.Services; // start the host
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    private sealed class RecordingFactory() : ZawedApiFactory(settings: new Dictionary<string, string?>
    {
        ["Zawed:Finance:ReceivingAccounts"] = "MAIN,CIB",
        ["Zawed:Finance:ReceivingAccountLabels"] = "MAIN=" + MainLabel,
    })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<DomainEventLog>();
                services.AddScoped<IDomainEventHandler<WalletLowBalance>, Recorder<WalletLowBalance>>();
                services.AddScoped<IDomainEventHandler<TopUpAwaitingApproval>, Recorder<TopUpAwaitingApproval>>();
                services.AddScoped<IDomainEventHandler<TopUpCredited>, Recorder<TopUpCredited>>();
                services.AddScoped<IDomainEventHandler<TopUpRejected>, Recorder<TopUpRejected>>();
                services.AddScoped<IDomainEventHandler<WalletAdjustmentAwaitingApproval>, Recorder<WalletAdjustmentAwaitingApproval>>();
            });
        }
    }

    /// <summary>Records an event only when its transaction commits, as a reader of the outcome would see it.</summary>
    private sealed class Recorder<T>(DomainEventLog log, IDbSession session) : IDomainEventHandler<T> where T : notnull
    {
        public Task HandleAsync(T domainEvent, CancellationToken ct)
        {
            session.Current.AfterCommit(() =>
            {
                log.Add(domainEvent);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------------------------------------- the ledger

    /// <summary>
    /// Posts in its own transaction under a platform scope with no user, as a job or an Ops request would (the Java
    /// test's <c>DbScope.runAs(PLATFORM)</c> plus <c>TransactionTemplate</c>).
    /// </summary>
    public Task<Posted> PostAsync(SourceType source, Guid sourceId, params Entry[] entries) =>
        InPlatformAsync(ledger => ledger.PostAsync(new Posting(source, sourceId, null, "test posting", Poster.System, entries)));

    public Task<T> InPlatformAsync<T>(Func<ILedger, Task<T>> work) => InPlatformAsync((ledger, _) => work(ledger));

    /// <summary><see cref="InPlatformAsync{T}(Func{ILedger, Task{T}})"/>, with the transaction, to hold it open or read in it.</summary>
    public async Task<T> InPlatformAsync<T>(Func<ILedger, DbTx, Task<T>> work)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        return await scope.ServiceProvider.GetRequiredService<ITransactions>().RunAsync(DbScopeContext.Escalate(Scope.Platform, null), async tx =>
        {
            session.Attach(tx);
            try
            {
                return await work(scope.ServiceProvider.GetRequiredService<ILedger>(), tx);
            }
            finally
            {
                session.Detach();
            }
        });
    }

    public async Task<long> CachedBalanceAsync(Guid company) =>
        await Owner.QueryOneAsync<long>("select balance_piastres from company_wallets where company_id = @company", new { company });

    /// <summary>Credits minus debits of the company's wallet entries.</summary>
    public async Task<long> LedgerBalanceAsync(Guid company) => await Owner.QueryOneAsync<long>("""
        select coalesce(sum(case direction when 'CREDIT' then amount_piastres else -amount_piastres end), 0)::bigint
        from ledger_entries where company_id = @company
        """, new { company });

    /// <summary>Credits minus debits on one account, from its entries.</summary>
    public async Task<long> AccountBalanceAsync(Account account) => await Owner.QueryOneAsync<long>("""
        select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
        from ledger_entries e join ledger_accounts a on a.id = e.account_id where a.code = @code
        """, new { code = account.Code });

    public async Task<long> CountAsync(string sql, object? args = null) => await Owner.QueryOneAsync<long>(sql, args);
}

/// <summary>The events the ledger published and whose transaction committed.</summary>
public sealed class DomainEventLog
{
    private readonly ConcurrentQueue<object> events = new();

    public void Add(object domainEvent) => events.Enqueue(domainEvent);

    public IReadOnlyList<T> Of<T>() => events.OfType<T>().ToList();
}

/// <summary>Runs <c>n</c> calls at the same moment and returns their results in order (Java <c>support.Concurrently</c>).</summary>
public static class Concurrently
{
    public static async Task<List<T>> RunAsync<T>(int n, Func<int, Task<T>> call)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return await call(i);
        })).ToList();
        start.SetResult();
        return (await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60))).ToList();
    }
}

/// <summary>HTTP helpers of the Java ledger tests (<c>IdentityTestSupport.fund</c>, <c>authed</c>, <c>code</c>).</summary>
public sealed class LedgerHttp(IdentityTestSupport id)
{
    private SignedIn? fundsApprover;

    /// <summary>Today in Cairo, the business date a transfer date is checked against.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Api.Modules.Ledger.TopUps.BusinessZone).DateTime);

    public static string Reference() => "REF-" + Guid.NewGuid().ToString()[..12].ToUpperInvariant();

    /// <summary>
    /// Puts money in a wallet the way production does since V12: Operations records a verified bank transfer, and a
    /// different person (an Ops Manager, signed in once per test) approves it.
    /// </summary>
    public async Task FundAsync(SignedIn recorder, Guid company, long piastres)
    {
        var recorded = await id.AuthedAsync("POST", $"/api/v1/ops/companies/{company}/wallet/credits", recorder.AccessToken,
            $"{{\"amountPiastres\":{piastres},\"bankReference\":\"FT-{Guid.NewGuid()}\",\"transferDate\":\"{Today:yyyy-MM-dd}\"}}");
        Assert.True((int)recorded.StatusCode == 201, await recorded.Content.ReadAsStringAsync());
        if (fundsApprover is null)
        {
            fundsApprover = await id.SignInAsync(await id.SeedAsync(Role.OPS_MANAGER));
            id.Limiter.Reset(); // the helper's own sign-in must not spend the test's sign-in budget
        }
        var topUp = (await IdentityTestSupport.JsonOf(recorded))["id"]!.GetValue<string>();
        var approved = await id.AuthedAsync("POST", $"/api/v1/ops/topups/{topUp}/approve", fundsApprover.AccessToken);
        Assert.True((int)approved.StatusCode == 200, await approved.Content.ReadAsStringAsync());
    }

    public Task<HttpResponseMessage> SendAsync(string method, string path, string token, string? json, IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new("Bearer", token);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return id.Client.SendAsync(request);
    }

    public static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.JsonAsync();

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;

    public static async Task<string> TextAsync(HttpResponseMessage response, string property) =>
        (await response.JsonAsync()).GetProperty(property).GetString()!;
}

/// <summary>Raw transactions as the owner or as the runtime role (the Java tests' <c>connect</c> and <c>app</c> helpers).</summary>
public static class RawSql
{
    /// <summary>
    /// A transaction told what <c>DbTx</c> would tell it, as <c>zawed_app</c> when <paramref name="asApp"/>, otherwise as
    /// the owner. One refused statement aborts it: use one per attempt.
    /// </summary>
    public static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Tx)> OpenAsync(Owner owner, bool asApp, string? scopeType,
        Guid? scopeId = null, Guid? userId = null)
    {
        var connection = new NpgsqlConnection(owner.ConnectionString);
        await connection.OpenAsync();
        var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand((asApp ? "set local role zawed_app; " : "")
            + "select set_config('app.scope_type', @t, true), set_config('app.scope_id', @s, true), set_config('app.user_id', @u, true);",
            connection, tx);
        command.Parameters.AddWithValue("t", scopeType ?? "");
        command.Parameters.AddWithValue("s", scopeId?.ToString() ?? "");
        command.Parameters.AddWithValue("u", userId?.ToString() ?? "");
        await command.ExecuteNonQueryAsync();
        return (connection, tx);
    }

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string from)
    {
        await using var command = new NpgsqlCommand("select count(*) from " + from, connection, tx);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The PostgreSQL refusal of <paramref name="work"/>, which must fail.</summary>
    public static async Task<PostgresException> RefusedAsync(Func<Task> work) => await Assert.ThrowsAsync<PostgresException>(work);
}
