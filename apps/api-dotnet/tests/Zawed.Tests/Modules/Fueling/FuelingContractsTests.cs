using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Fueling;
using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Ledger;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// The application for the contract tests: a recorder of <see cref="HardAlertRaised"/> that keeps an event only when its
/// transaction commits, and a second handler that fails inside its own savepoint, as a notification handler of wave 3C does
/// (B7 W5: a notice that cannot be written must not stop the fueling).
/// </summary>
public sealed class FuelingEventsApi : IAsyncLifetime
{
    public ZawedApiFactory Factory { get; } = new RecordingFactory();

    public ConcurrentQueue<HardAlertRaised> Raised => Factory.Services.GetRequiredService<ConcurrentQueue<HardAlertRaised>>();

    public Task InitializeAsync()
    {
        _ = Factory.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    private sealed class RecordingFactory : ZawedApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ConcurrentQueue<HardAlertRaised>>();
                services.AddScoped<IDomainEventHandler<HardAlertRaised>, FailingNotice>();
                services.AddScoped<IDomainEventHandler<HardAlertRaised>, Recorder>();
            });
        }
    }

    private sealed class Recorder(ConcurrentQueue<HardAlertRaised> log, IDbSession session) : IDomainEventHandler<HardAlertRaised>
    {
        public Task HandleAsync(HardAlertRaised domainEvent, CancellationToken ct)
        {
            session.Current.AfterCommit(() =>
            {
                log.Enqueue(domainEvent);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        }
    }

    /// <summary>Writes into a table that does not exist, inside a savepoint, and swallows the failure.</summary>
    private sealed class FailingNotice(IDbSession session) : IDomainEventHandler<HardAlertRaised>
    {
        public async Task HandleAsync(HardAlertRaised domainEvent, CancellationToken ct)
        {
            try
            {
                await session.Current.SavepointAsync("test_notice", () => session.Current.ExecuteAsync("insert into no_such_table values (1)", ct: ct), ct);
            }
            catch (Npgsql.PostgresException)
            {
                // A notice that cannot be written is logged and dropped; the alert and the fueling go on.
            }
        }
    }
}

/// <summary>
/// What the fueling module publishes for other modules (wave 3A): <see cref="HardAlertRaised"/> once per alert written and
/// only on commit, the low-balance alert raised from the ledger's event, <see cref="IFuelingApi"/> under row-level security,
/// and the activity calendar.
/// </summary>
public sealed class FuelingContractsTests(FuelingEventsApi api) : IClassFixture<FuelingEventsApi>
{
    private readonly FuelingTestSupport f = new(api.Factory);

    [Fact]
    public async Task EveryHardAlertIsAnnouncedOnceOnCommitAndAFailingNoticeDoesNotStopTheFueling()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync(90_000);
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company, tankMl: 10_000);

        var done = await BodyAsync(await f.FuelAsync(pump, car, 20_000), 201); // over the tank, and below the threshold
        Assert.Equal("COMPLETED", done.GetProperty("status").GetString());
        Assert.Equal(70_000, await f.WalletBalanceAsync(company));
        var fuelingId = Guid.Parse(done.GetProperty("id").GetString()!);
        var raised = api.Raised.Where(e => e.CompanyId == Guid.Parse(company)).ToList();
        Assert.Equal(new[] { "LOW_BALANCE", "TANK_CAPACITY_EXCEEDED" }, raised.Select(e => e.Type).Order());
        var tank = raised.Single(e => e.Type == "TANK_CAPACITY_EXCEEDED");
        Assert.Equal(("CRITICAL", fuelingId, Guid.Parse(pump.StationId), Guid.Parse(pump.OperatorId)),
            (tank.Severity, tank.FuelingId, tank.StationId, tank.OperatorId));
        var low = raised.Single(e => e.Type == "LOW_BALANCE");
        Assert.Equal(("WARNING", (Guid?)null, (Guid?)null), (low.Severity, low.FuelingId, low.StationId));
        Assert.Equal(2, await f.CountAsync("select count(*) from alerts where company_id = @c::uuid", new { c = company }));
        Assert.Contains(raised, e => e.AlertId != Guid.Empty);
        foreach (var e in raised)
        {
            Assert.Equal(1, await f.CountAsync("select count(*) from alerts where id = @id", new { id = e.AlertId }));
        }
    }

    [Fact]
    public async Task ARolledBackFuelingAnnouncesNothing()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync(90_000);
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company, tankMl: 10_000);
        var trigger = "fueling_test_refuse_" + Guid.Parse(company).ToString("N")[..12];
        await f.Owner.ExecuteAsync($"""
            create function {trigger}() returns trigger language plpgsql as $$
            begin raise exception 'refused for the test'; end $$;
            create trigger {trigger} before insert on alerts
              for each row when (new.company_id = '{company}'::uuid and new.type = 'TANK_CAPACITY_EXCEEDED') execute function {trigger}();
            """);
        try
        {
            Assert.Equal(500, (int)(await f.FuelAsync(pump, car, 20_000)).StatusCode);
        }
        finally
        {
            await f.Owner.ExecuteAsync($"drop trigger {trigger} on alerts; drop function {trigger}();");
        }
        // The LOW_BALANCE alert was written and announced inside the transaction, then rolled back with it.
        Assert.DoesNotContain(api.Raised, e => e.CompanyId == Guid.Parse(company));
        Assert.Equal(0, await f.CountAsync("select count(*) from alerts where company_id = @c::uuid", new { c = company }));
        Assert.Equal(100_000, await f.WalletBalanceAsync(company));
    }

    [Fact]
    public async Task TheFuelingApiAnswersUnderRowLevelSecurity()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company);
        var id = Guid.Parse((await BodyAsync(await f.FuelAsync(pump, car, 10_000), 201)).GetProperty("id").GetString()!);
        var companyId = Guid.Parse(company);

        var asCompany = await InScopeAsync(Scope.Company(companyId), async api =>
            (await api.VisibleAsync(id), await api.FindAsync(id), await api.UsageSinceAsync(DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-3))));
        Assert.True(asCompany.Item1);
        var found = asCompany.Item2!;
        Assert.Equal(("COMPLETED", "ONLINE", 10_000L, 15_000L, true, companyId), (found.Status, found.Origin, found.VolumeMl, found.AmountPiastres,
            found.Billable, found.CompanyId));
        Assert.NotNull(found.LedgerTransactionId);
        Assert.Equal(new FuelingUsage(10_000, 10_000, 10_000, 15_000, 15_000, 15_000), asCompany.Item3[Guid.Parse(car.VehicleId)]);

        var asStranger = await InScopeAsync(Scope.Company(Guid.CreateVersion7()), async api => (await api.VisibleAsync(id), await api.FindAsync(id)));
        Assert.False(asStranger.Item1);
        Assert.Null(asStranger.Item2);
        Assert.True(await InScopeAsync(new Scope(ScopeType.OPERATOR, Guid.Parse(pump.OperatorId)), api => api.VisibleAsync(id)));
    }

    [Fact]
    public async Task TheActivityCalendarCountsEachAudiencesOwnDays()
    {
        var pump = await f.PumpAsync();
        await f.PriceAsync(pump.StationId, "GAS_92", Price);
        var company = await f.CompanyAsync();
        await f.CreditAsync(company, 100_000);
        var car = await f.CarAsync(company, tankMl: 10_000);
        Assert.Equal("COMPLETED", await StatusAsync(await f.FuelAsync(pump, car, 20_000))); // and a tank alert
        Assert.Equal("COOLDOWN", await ReasonAsync(await f.FuelAsync(pump, car, 1_000)));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Activity.Cairo).DateTime);
        var from = today.AddDays(-2).ToString("yyyy-MM-dd");
        var to = today.ToString("yyyy-MM-dd");

        var manager = await f.SignInAsync(Role.FLEET_MANAGER, Scope.Company(Guid.Parse(company)));
        var days = await BodyAsync(await f.GetAsync($"/api/v1/company/activity?from={from}&to={to}", manager), 200);
        Assert.Equal(3, days.GetArrayLength());
        Assert.Equal(from, days[0].GetProperty("day").GetString());
        Assert.Equal(0, days[0].GetProperty("completed").GetInt32());
        var last = days[2];
        Assert.Equal((to, 1, 1, 0, 0, 20_000L, 30_000L, 1), (last.GetProperty("day").GetString(), last.GetProperty("completed").GetInt32(),
            last.GetProperty("declined").GetInt32(), last.GetProperty("offline").GetInt32(), last.GetProperty("withConflicts").GetInt32(),
            last.GetProperty("volumeMl").GetInt64(), last.GetProperty("amountPiastres").GetInt64(), last.GetProperty("alerts").GetInt32()));

        var station = await f.SignInAsync(Role.STATION_MANAGER, new Scope(ScopeType.OPERATOR, Guid.Parse(pump.OperatorId)));
        var atStation = await BodyAsync(await f.GetAsync($"/api/v1/station/activity?from={to}&to={to}&stationId={pump.StationId}", station), 200);
        Assert.Equal(2, atStation[0].GetProperty("completed").GetInt32() + atStation[0].GetProperty("declined").GetInt32());

        var ops = await f.OpsAsync();
        var byDevice = await BodyAsync(await f.GetAsync($"/api/v1/ops/activity?from={to}&to={to}&deviceId={pump.DeviceId}", ops), 200);
        Assert.Equal(1, byDevice[0].GetProperty("completed").GetInt32());
        Assert.Equal(1, byDevice[0].GetProperty("alerts").GetInt32());

        Assert.Equal("from", (await Http.AssertErrorAsync(await f.GetAsync("/api/v1/ops/activity", ops), 400, "VALIDATION_FAILED"))
            .GetProperty("field").GetString());
        Assert.Equal("from", (await Http.AssertErrorAsync(await f.GetAsync($"/api/v1/ops/activity?from={to}&to={from}", ops), 400,
            "VALIDATION_FAILED")).GetProperty("field").GetString());
        var long1 = today.AddDays(-366).ToString("yyyy-MM-dd");
        var refused = await Http.AssertErrorAsync(await f.GetAsync($"/api/v1/ops/activity?from={long1}&to={to}", ops), 400, "EXPORT_RANGE_TOO_LONG");
        Assert.Equal(366, refused.GetProperty("params").GetProperty("maxDays").GetInt32());
        var year = today.AddDays(-365).ToString("yyyy-MM-dd");
        Assert.Equal(366, (await BodyAsync(await f.GetAsync($"/api/v1/ops/activity?from={year}&to={to}&deviceId={pump.DeviceId}", ops), 200))
            .GetArrayLength());
    }

    private async Task<T> InScopeAsync<T>(Scope scope, Func<IFuelingApi, Task<T>> work)
    {
        await using var services = api.Factory.Services.CreateAsyncScope();
        var session = services.ServiceProvider.GetRequiredService<DbSession>();
        return await services.ServiceProvider.GetRequiredService<ITransactions>().RunAsync(DbScopeContext.Escalate(scope, null), async tx =>
        {
            session.Attach(tx);
            try
            {
                return await work(services.ServiceProvider.GetRequiredService<IFuelingApi>());
            }
            finally
            {
                session.Detach();
            }
        });
    }
}
