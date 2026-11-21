using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Domain;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Network;

/// <summary>
/// What other modules ask the network module, in the caller's transaction and scope: <see cref="IOperatorDirectory"/>
/// (pricing, user admin), <see cref="IStationDirectory"/> (the fueling engine, receipts) and <see cref="IAgentShifts"/>
/// (offline sync, B5 contract §3). Row-level security decides what a scope finds.
/// </summary>
public sealed class NetworkContractsTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly PosTestSupport pos = new(api.Factory);

    [Fact]
    public async Task TheDirectoriesAnswerInTheCallersScope()
    {
        var mine = await pos.OperatorAsync();
        var station = await pos.StationAsync(mine, "CNG", "GAS_95");
        var theirs = await pos.OperatorAsync();

        var asPlatform = await InScopeAsync(Scope.Platform, async sp =>
            (await sp.GetRequiredService<IOperatorDirectory>().FindAsync(Guid.Parse(mine)),
             await sp.GetRequiredService<IOperatorDirectory>().StationExistsAsync(Guid.Parse(station)),
             await sp.GetRequiredService<IStationDirectory>().FindStationAsync(Guid.Parse(station))));
        Assert.Equal(new OperatorRef(Guid.Parse(mine), "مشغل", "Operator", true), asPlatform.Item1);
        Assert.True(asPlatform.Item2);
        var found = asPlatform.Item3!;
        Assert.Equal(("Africa/Cairo", true, Guid.Parse(mine)), (found.TimeZone, found.Active, found.OperatorId));
        Assert.Equal([FuelType.GAS_95, FuelType.CNG], found.FuelTypes);
        Assert.Equal(TimeSpan.FromHours(2), found.Zone.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));

        // Another operator's user finds neither the operator nor its station.
        var asOther = await InScopeAsync(Scope.Operator(Guid.Parse(theirs)), async sp =>
            (await sp.GetRequiredService<IOperatorDirectory>().FindAsync(Guid.Parse(mine)),
             await sp.GetRequiredService<IOperatorDirectory>().StationExistsAsync(Guid.Parse(station)),
             await sp.GetRequiredService<IStationDirectory>().FindStationAsync(Guid.Parse(station))));
        Assert.Equal((null, false, null), asOther);

        // Suspending the operator closes its stations to fuelings.
        await pos.Owner.ExecuteAsync("update station_operators set status = 'SUSPENDED' where id = @id::uuid", new { id = mine });
        var suspended = await InScopeAsync(Scope.Platform, async sp => await sp.GetRequiredService<IStationDirectory>().FindStationAsync(Guid.Parse(station)));
        Assert.False(suspended!.Active);
    }

    [Fact]
    public async Task AnOfflineCapturesAgentCountsOnlyWithinASessionOnThatDevice()
    {
        var pump = await pos.PumpAsync();
        var agent = await pos.Owner.QueryOneAsync<Guid>("select id from agents where worker_id = @w", new { w = pump.WorkerId });
        var started = await pos.Owner.QueryOneAsync<DateTime>("select created_at from agent_sessions where agent_id = @agent", new { agent });
        var at = new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc));
        var (device, station) = (Guid.Parse(pump.DeviceId), Guid.Parse(pump.StationId));

        var answers = await InScopeAsync(Scope.Platform, async sp =>
        {
            var shifts = sp.GetRequiredService<IAgentShifts>();
            var tx = sp.GetRequiredService<IDbSession>().Current;
            return new[]
            {
                await shifts.OnDeviceAtAsync(tx, device, station, agent, at.AddMinutes(1)),
                await shifts.OnDeviceAtAsync(tx, device, station, agent, at.AddMinutes(-4)), // a slow device clock, within tolerance
                await shifts.OnDeviceAtAsync(tx, device, station, agent, at.AddMinutes(-6)),
                await shifts.OnDeviceAtAsync(tx, Guid.CreateVersion7(), station, agent, at.AddMinutes(1)),
                await shifts.OfStationAsync(tx, station, agent),
                await shifts.OfStationAsync(tx, Guid.CreateVersion7(), agent),
            };
        });
        var expected = new AgentOnDevice(agent, pump.WorkerId);
        Assert.Equal(new AgentOnDevice?[] { expected, expected, null, null, expected, null }.AsEnumerable(), answers.AsEnumerable());

        // Another operator's scope does not see the session history: the capture would be refused.
        var asOperator = await InScopeAsync(Scope.Operator(Guid.Parse(await pos.OperatorAsync())), async sp =>
            await sp.GetRequiredService<IAgentShifts>().OnDeviceAtAsync(sp.GetRequiredService<IDbSession>().Current, device, station, agent, at.AddMinutes(1)));
        Assert.Null(asOperator);
    }

    private async Task<T> InScopeAsync<T>(Scope scope, Func<IServiceProvider, Task<T>> work)
    {
        await using var services = api.Factory.Services.CreateAsyncScope();
        var session = services.ServiceProvider.GetRequiredService<DbSession>();
        var role = scope.IsPlatform ? Role.OPS : Role.STATION_MANAGER;
        await using var tx = await services.ServiceProvider.GetRequiredService<ITransactions>().BeginAsync(
            DbScopeContext.ForPrincipal(new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), role, scope, false)));
        session.Attach(tx);
        try
        {
            return await work(services.ServiceProvider);
        }
        finally
        {
            session.Detach();
        }
    }
}
