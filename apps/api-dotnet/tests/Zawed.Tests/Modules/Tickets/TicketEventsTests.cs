using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Tickets.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Network.PosTestSupport;

namespace Zawed.Tests.Modules.Tickets;

/// <summary>
/// The events the notifications wave handles (Java <c>TicketOpened</c>, <c>TicketAnswered</c>): published inside the request's
/// transaction, one per opened ticket and one per public Operations answer; an internal note and a company's or station's reply
/// publish nothing, and a refused request publishes nothing that survives.
/// </summary>
public sealed class TicketEventsTests(TicketEventsTests.Api api) : IClassFixture<TicketEventsTests.Api>
{
    private readonly PosTestSupport pos = new(api.Factory);

    [Fact]
    public async Task OpeningAndAnsweringPublishTheirEventsAndNothingElseDoes()
    {
        var id = pos.Id;
        var company = await id.SeedCompanyAsync();
        var fleet = (await id.SignInAsync(await id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company), false))).AccessToken;
        var ops = await pos.Ops.Value;

        var ticket = Guid.Parse((await Json(await id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            """{"type":"STOLEN_TAG","subject":"Stolen","body":"Car 7"}"""), 201))["ticket"]!["id"]!.GetValue<string>());
        var opened = Assert.Single(api.Recorder.Of(ticket));
        Assert.Equal(new TicketOpened(ticket, "STOLEN_TAG", company, null), opened.Event);
        Assert.True(opened.InTransaction);

        await Json(await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", ops, """{"body":"note","internal":true}"""), 200);
        await Json(await id.AuthedAsync("POST", $"/api/v1/company/tickets/{ticket}/messages", fleet, """{"body":"Any news?"}"""), 200);
        Assert.Single(api.Recorder.Of(ticket));

        await Json(await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", ops, """{"body":"Blocked the tag"}"""), 200);
        await Json(await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", ops, """{"body":"And a new one is coming"}"""), 200);
        var answers = api.Recorder.Of(ticket).Skip(1).Select(e => e.Event).ToList();
        Assert.Equal([new TicketAnswered(ticket, "STOLEN_TAG", company, null), new TicketAnswered(ticket, "STOLEN_TAG", company, null)], answers);

        // A closed ticket refuses the answer, and publishes nothing.
        await Json(await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", ops, """{"status":"CLOSED"}"""), 200);
        await Http.AssertErrorAsync(await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", ops, """{"body":"late"}"""), 409, "TICKET_CLOSED");
        Assert.Equal(3, api.Recorder.Of(ticket).Count);
    }

    [Fact]
    public async Task AStationTicketNamesItsOperator()
    {
        var id = pos.Id;
        var @operator = await pos.OperatorAsync();
        var manager = (await id.SignInAsync(await id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(@operator)), false))).AccessToken;
        var ticket = Guid.Parse((await Json(await id.AuthedAsync("POST", "/api/v1/station/tickets", manager,
            """{"type":"PAYOUT_QUERY","subject":"Payout","body":"Late"}"""), 201))["ticket"]!["id"]!.GetValue<string>());
        await Json(await id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", await pos.Ops.Value, """{"body":"Sent today"}"""), 200);

        Assert.Equal([new TicketOpened(ticket, "PAYOUT_QUERY", null, Guid.Parse(@operator)),
            new TicketAnswered(ticket, "PAYOUT_QUERY", null, Guid.Parse(@operator))], api.Recorder.Of(ticket).Select(e => e.Event));
    }

    /// <summary>The application with a handler that records every ticket event and whether it ran inside the request's transaction.</summary>
    public sealed class Api : IAsyncLifetime
    {
        public RecordingFactory Factory { get; } = new();

        public Recorder Recorder => Factory.Recorder;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await Factory.DisposeAsync();
    }

    public sealed class RecordingFactory : ZawedApiFactory
    {
        public Recorder Recorder { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Recorder);
                services.AddScoped<IDomainEventHandler<TicketOpened>, RecordingHandler>();
                services.AddScoped<IDomainEventHandler<TicketAnswered>, RecordingHandler>();
            });
        }
    }

    public sealed record Recorded(Guid TicketId, object Event, bool InTransaction);

    public sealed class Recorder
    {
        private readonly ConcurrentQueue<Recorded> events = new();

        public void Add(Recorded recorded) => events.Enqueue(recorded);

        public List<Recorded> Of(Guid ticket) => [.. events.Where(e => e.TicketId == ticket)];
    }

    private sealed class RecordingHandler(Recorder recorder, IDbSession session) : IDomainEventHandler<TicketOpened>, IDomainEventHandler<TicketAnswered>
    {
        public async Task HandleAsync(TicketOpened domainEvent, CancellationToken ct) =>
            recorder.Add(new Recorded(domainEvent.TicketId, domainEvent, await SeesTicketAsync(domainEvent.TicketId, ct)));

        public async Task HandleAsync(TicketAnswered domainEvent, CancellationToken ct) =>
            recorder.Add(new Recorded(domainEvent.TicketId, domainEvent, await SeesTicketAsync(domainEvent.TicketId, ct)));

        /// <summary>The ticket row is visible in the publisher's transaction, before it commits.</summary>
        private async Task<bool> SeesTicketAsync(Guid ticket, CancellationToken ct) =>
            await session.Current.QuerySingleAsync<bool>("select exists (select 1 from tickets where id = @ticket)", new { ticket }, ct);
    }
}
