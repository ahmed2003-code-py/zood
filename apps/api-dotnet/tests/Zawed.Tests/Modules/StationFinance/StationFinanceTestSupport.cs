using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.StationFinance.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Fueling;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.StationFinance;

/// <summary>
/// The application for the station finance tests, as Java <c>StationFinanceTests</c>' <c>@TestPropertySource</c> sets it:
/// no bank change hold (so payouts can follow an approval; <c>BankAccountHoldTests</c> checks the hold with the default
/// window) and an approval threshold of 100,000 piastres. It records <see cref="PayoutPosted"/> and
/// <see cref="BankAccountChangeDecided"/> when their transaction commits, so a test can prove an event was published once.
/// </summary>
public sealed class StationFinanceApi : IAsyncLifetime
{
    public const long Threshold = 100_000;

    public ZawedApiFactory Factory { get; } = new RecordingFactory();

    public Owner Owner => Factory.Owner;

    public ConcurrentQueue<object> Events => Factory.Services.GetRequiredService<ConcurrentQueue<object>>();

    public Task InitializeAsync()
    {
        _ = Factory.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    private sealed class RecordingFactory() : ZawedApiFactory(settings: new Dictionary<string, string?>
    {
        ["Zawed:Finance:BankChangeHoldHours"] = "0",
        ["Zawed:Finance:PayoutApprovalThresholdPiastres"] = Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // Java's @TestPropertySource of StationFinanceTests: an offline capture past 10,000 has its payable held.
        ["Zawed:Sync:OfflineExposureCapPiastres"] = "10000",
        ["Zawed:Sync:CompanyOfflineAlertPiastres"] = "40000",
    })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ConcurrentQueue<object>>();
                services.AddScoped<IDomainEventHandler<PayoutPosted>, Recorder<PayoutPosted>>();
                services.AddScoped<IDomainEventHandler<BankAccountChangeDecided>, Recorder<BankAccountChangeDecided>>();
            });
        }
    }

    private sealed class Recorder<T>(ConcurrentQueue<object> log, IDbSession session) : IDomainEventHandler<T> where T : notnull
    {
        public Task HandleAsync(T domainEvent, CancellationToken ct)
        {
            session.Current.AfterCommit(() =>
            {
                log.Enqueue(domainEvent);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Java <c>StationFinanceTests</c>' helpers on top of the fueling support: real fuelings at a real pump, bank account
/// changes, payouts and reversals through the API, and an offline fueling whose payable is held. Create one per test.
/// </summary>
public sealed class FinanceTestSupport
{
    /// <summary>An Egyptian IBAN that passes mod 97, as the Java tests type it.</summary>
    public const string Iban = "EG38 0019 0005 0000 0000 2631 80002";

    public const string IbanDigits = "0019000500000000263180002";

    public FinanceTestSupport(ZawedApiFactory factory)
    {
        Factory = factory;
        Fueling = new FuelingTestSupport(factory);
    }

    public ZawedApiFactory Factory { get; }

    public FuelingTestSupport Fueling { get; }

    public IdentityTestSupport Id => Fueling.Id;

    public Owner Owner => Fueling.Owner;

    /// <summary>A signed-in Ops user; the rate limits are reset so a test may sign many in.</summary>
    public async Task<SignedIn> OpsAsync(Role role = Role.OPS)
    {
        var signedIn = await Id.SignInAsync(await Id.SeedAsync(role));
        Id.Limiter.Reset();
        return signedIn;
    }

    public async Task<SignedIn> ManagerAsync(string @operator)
    {
        var signedIn = await Id.SignInAsync(await Id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(@operator)), false));
        Id.Limiter.Reset();
        return signedIn;
    }

    public async Task<SignedIn> FleetManagerAsync(string company)
    {
        var signedIn = await Id.SignInAsync(await Id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(company)), false));
        Id.Limiter.Reset();
        return signedIn;
    }

    /// <summary>A pump with a GAS_92 price, a funded company and a car of it.</summary>
    public async Task<(Pump Pump, string Company, Car Car)> StationWithCustomerAsync(long credit = 1_000_000, long tankMl = 60_000)
    {
        var pump = await Fueling.PumpAsync();
        await Fueling.PriceAsync(pump.StationId, "GAS_92", FuelingTestSupport.Price);
        var company = await Fueling.CompanyAsync();
        await Fueling.CreditAsync(company, credit);
        var car = await Fueling.CarAsync(company, tankMl: tankMl);
        return (pump, company, car);
    }

    /// <summary>A completed online fueling; returns its id.</summary>
    public async Task<string> FuelAsync(Pump pump, Car car, long volumeMl)
    {
        var body = await FuelingTestSupport.BodyAsync(await Fueling.FuelAsync(pump, car, volumeMl), 201);
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());
        return body.GetProperty("id").GetString()!;
    }

    public Task<HttpResponseMessage> AuthedAsync(string method, string path, SignedIn who, string? json = null) =>
        Id.AuthedAsync(method, path, who.AccessToken, json);

    public async Task<string> RequestBankChangeAsync(SignedIn ops, string station, string account = Iban)
    {
        var requested = await AuthedAsync("POST", $"/api/v1/ops/stations/{station}/bank-account/changes", ops,
            BankChangeBody(account));
        return (await BodyAsync(requested, 201)).GetProperty("id").GetString()!;
    }

    public static string BankChangeBody(string account) => JsonSerializer.Serialize(new
    {
        accountNumber = account,
        bankName = "Bank",
        holderName = "Station Co",
        bankLetterReference = "BL-" + Guid.NewGuid(),
    });

    public async Task ApproveBankChangeAsync(SignedIn checker, string change) =>
        await BodyAsync(await AuthedAsync("POST", $"/api/v1/ops/bank-account-changes/{change}/approve", checker), 200);

    public Task<HttpResponseMessage> PayoutAsync(SignedIn who, string station, long piastres, string reference, DateOnly? valueDate = null) =>
        AuthedAsync("POST", $"/api/v1/ops/stations/{station}/payouts", who,
            $$"""{"amountPiastres":{{piastres}},"bankReference":"{{reference}}","valueDate":"{{(valueDate ?? LedgerHttp.Today):yyyy-MM-dd}}"}""");

    public Task<HttpResponseMessage> RequestReversalAsync(SignedIn who, string fueling, string reason = "Charged the wrong vehicle") =>
        AuthedAsync("POST", $"/api/v1/ops/fuelings/{fueling}/reversals", who, JsonSerializer.Serialize(new { reason }));

    public static string Ref() => "PO-" + Guid.NewGuid().ToString()[..12].ToUpperInvariant();

    public async Task<long> StationPayableAsync(string station) =>
        await Owner.QueryOneAsync<long>("select balance_piastres from station_payables where station_id = @id::uuid", new { id = station });

    /// <summary>Credits minus debits of the station's payable entries in the ledger.</summary>
    public async Task<long> PayableLedgerAsync(string station) => await Owner.QueryOneAsync<long>("""
        select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
        from ledger_entries e join ledger_accounts a on a.id = e.account_id where a.code = @code
        """, new { code = Account.StationPayable(Guid.Parse(station)).Code });

    public Task<long> WalletBalanceAsync(string company) => Fueling.WalletBalanceAsync(company);

    public static Task<JsonElement> BodyAsync(HttpResponseMessage response, int status) => FuelingTestSupport.BodyAsync(response, status);

    public static async Task<string?> CodeAsync(HttpResponseMessage response) => await PosTestSupport.CodeAsync(response);

    /// <summary>
    /// An offline fueling whose payable was held at sync (B5 contract §4: past the device's offline allowance), for the
    /// concurrency tests' arrangement (<c>StationFinanceTests</c> syncs through <c>/pos/sync</c> since wave 4A): a copy of a real online fueling's row as an OFFLINE capture with
    /// <c>OVER_OFFLINE_ALLOWANCE</c> and <c>payable_held</c>, posted through the real ledger (wallet → station payable, as
    /// the sync posts it) in one platform transaction. Returns the new fueling's id.
    /// </summary>
    public async Task<string> HeldOfflineFuelingAsync(string onlineFuelingId, long amountPiastres)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        return await scope.ServiceProvider.GetRequiredService<ITransactions>().RunAsync(DbScopeContext.Escalate(Scope.Platform, null), async tx =>
        {
            session.Attach(tx);
            try
            {
                var source = await tx.QuerySingleAsync<(Guid Company, Guid Station)>(
                    "select company_id, station_id from fuelings where id = @id::uuid", new { id = onlineFuelingId });
                var id = Guid.CreateVersion7();
                var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
                await ledger.LockWalletAsync(source.Company);
                var posted = await ledger.PostAsync(new Posting(SourceType.FUELING, id, "OFF-" + id.ToString("N")[^12..].ToUpperInvariant(), "Offline fueling",
                    Poster.System, [Entry.Debit(Account.CompanyWallet(source.Company), amountPiastres),
                        Entry.Credit(Account.StationPayable(source.Station), amountPiastres)]));
                await tx.ExecuteAsync("""
                    insert into fuelings (id, client_txn_id, reference, status, origin, company_id, vehicle_id, tag_id, operator_id,
                                          station_id, device_id, agent_id, fuel_type, volume_ml, unit_price_piastres, amount_piastres,
                                          billable, evaluated_at, device_at, received_at, receipt_locale, plate, company_name_ar,
                                          company_name_en, station_name_ar, station_name_en, agent_worker_id, ledger_transaction_id,
                                          conflicts, payable_held)
                    select @id, @client, @reference, 'COMPLETED', 'OFFLINE', company_id, vehicle_id, tag_id, operator_id, station_id,
                           device_id, agent_id, fuel_type, volume_ml, unit_price_piastres, @amount, true, now(), now(), now(),
                           receipt_locale, plate, company_name_ar, company_name_en, station_name_ar, station_name_en, agent_worker_id,
                           @txn, '{OVER_OFFLINE_ALLOWANCE}', true
                    from fuelings where id = @source::uuid
                    """, new
                {
                    id,
                    client = Guid.NewGuid(),
                    reference = "OFF-" + id.ToString("N")[^12..].ToUpperInvariant(),
                    amount = amountPiastres,
                    txn = posted.TransactionId,
                    source = onlineFuelingId,
                });
                return id.ToString();
            }
            finally
            {
                session.Detach();
            }
        });
    }
}
