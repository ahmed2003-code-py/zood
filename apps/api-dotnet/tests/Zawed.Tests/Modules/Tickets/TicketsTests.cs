using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Network.PosTestSupport;

namespace Zawed.Tests.Modules.Tickets;

/// <summary>
/// Support tickets: each side sees only its own, Operations answers and keeps internal notes to itself, a ticket links only to
/// records its author can see, and a closed ticket takes no replies (Java <c>TicketsTests</c>, first three tests). Then the B6
/// rules the Java class did not pin: each portal's writes check the caller's kind of scope, a reply to another tenant's ticket
/// is a 404, fuelings and stations are links in the caller's scope, status changes and their audit rows, validation codes.
/// </summary>
public sealed class TicketsTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly PosTestSupport pos = new(api.Factory);

    private IdentityTestSupport Id => pos.Id;

    private Task<string> Ops => pos.Ops.Value;

    // ---------------------------------------------------------------------------------------------- the Java tests

    [Fact]
    public async Task ACompanyAsksAndOperationsAnswers()
    {
        var company = await Id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company);
        var otherFleet = await CompanyUserAsync(await Id.SeedCompanyAsync());

        var opened = await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            """{"type":"LOST_TAG","subject":"Tag lost on the road","body":"The tag of car 12 fell off."}""");
        var openedBody = await Json(opened, 201);
        var id = openedBody["ticket"]!["id"]!.GetValue<string>();
        Assert.StartsWith("T-", openedBody["ticket"]!["reference"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("OPEN", openedBody["ticket"]!["status"]!.GetValue<string>());

        // Another company neither lists nor reads it.
        Assert.Equal(404, (int)(await Id.AuthedAsync("GET", $"/api/v1/company/tickets/{id}", otherFleet)).StatusCode);
        var theirs = await IdsAsync(await Id.AuthedAsync("GET", "/api/v1/company/tickets", otherFleet));
        Assert.DoesNotContain(id, theirs);

        // Operations answers (which takes the ticket up) and adds a note the company never sees.
        var answer = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{id}/messages", await Ops, """{"body":"A new tag is on its way."}"""), 200);
        Assert.Equal("IN_PROGRESS", answer["ticket"]!["status"]!.GetValue<string>());
        await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{id}/messages", await Ops,
            """{"body":"Check the tag stock first","internal":true}"""), 200);

        var seenByCompany = await Json(await Id.AuthedAsync("GET", $"/api/v1/company/tickets/{id}", fleet), 200);
        var internalFlags = seenByCompany["messages"]!.AsArray().Select(m => m!["internal"]!.GetValue<bool>()).ToList();
        Assert.Equal(2, internalFlags.Count);
        Assert.All(internalFlags, Assert.False);
        var seenByOps = await Json(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets/{id}", await Ops), 200);
        var opsFlags = seenByOps["messages"]!.AsArray().Select(m => m!["internal"]!.GetValue<bool>()).ToList();
        Assert.Equal(3, opsFlags.Count);
        Assert.Contains(true, opsFlags);

        // Resolved and then closed: no more replies.
        var closed = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{id}/status", await Ops, """{"status":"CLOSED"}"""), 200);
        Assert.Equal("CLOSED", closed["ticket"]!["status"]!.GetValue<string>());
        Assert.NotNull(closed["ticket"]!["resolvedAt"]);
        var late = await Id.AuthedAsync("POST", $"/api/v1/company/tickets/{id}/messages", fleet, """{"body":"One more thing"}""");
        await Http.AssertErrorAsync(late, 409, "TICKET_CLOSED");
    }

    [Fact]
    public async Task EachSideAsksOnlyWhatItMayAsk()
    {
        var fleet = await CompanyUserAsync(await Id.SeedCompanyAsync());
        var wrong = await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet, """{"type":"DEVICE_FAULT","subject":"x","body":"y"}""");
        var refused = await Http.AssertErrorAsync(wrong, 400, "TICKET_TYPE_NOT_ALLOWED");
        Assert.Equal("type", refused.GetProperty("field").GetString());

        var @operator = await pos.OperatorAsync();
        var manager = await StationUserAsync(@operator);
        var fault = await Json(await Id.AuthedAsync("POST", "/api/v1/station/tickets", manager,
            """{"type":"DEVICE_FAULT","subject":"Reader dead","body":"Pump 3 does not read tags."}"""), 201);
        Assert.Equal(@operator, fault["ticket"]!["operatorId"]!.GetValue<string>());
        var listed = await IdsAsync(await Id.AuthedAsync("GET", "/api/v1/station/tickets", manager));
        Assert.Equal([fault["ticket"]!["id"]!.GetValue<string>()], listed);

        // And the station may not ask what only a company asks.
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", "/api/v1/station/tickets", manager,
            """{"type":"LOST_TAG","subject":"x","body":"y"}"""), 400, "TICKET_TYPE_NOT_ALLOWED");
    }

    [Fact]
    public async Task ATicketLinksOnlyToRecordsItsAuthorCanSee()
    {
        var mine = await Id.SeedCompanyAsync();
        var theirVehicle = await VehicleAsync(await Id.SeedCompanyAsync());
        var fleet = await CompanyUserAsync(mine);

        var foreign = await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            $$"""{"type":"VEHICLE_CHANGE","subject":"x","body":"y","vehicleId":"{{theirVehicle}}"}""");
        Assert.Equal("vehicleId", (await Http.AssertErrorAsync(foreign, 400, "TICKET_LINK_NOT_FOUND")).GetProperty("field").GetString());

        // A company sees no stations, so no station is a link it may make: an operator's real station answers exactly as one
        // that does not exist (B7 W2 sweep; they were 201 and a 500 before).
        foreach (var station in new[] { await pos.StationAsync(await pos.OperatorAsync()), Guid.NewGuid().ToString() })
        {
            var linked = await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
                $$"""{"type":"DISPUTE","subject":"x","body":"y","stationId":"{{station}}"}""");
            var body = await Http.AssertErrorAsync(linked, 400, "TICKET_LINK_NOT_FOUND");
            Assert.Equal("stationId", body.GetProperty("field").GetString());
        }

        var myVehicle = await VehicleAsync(mine);
        var own = await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            $$"""{"type":"VEHICLE_CHANGE","subject":"New plate","body":"Plate changed","vehicleId":"{{myVehicle}}"}""");
        Assert.Equal(myVehicle, (await Json(own, 201))["ticket"]!["vehicleId"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------------------------------- beyond the Java class

    [Fact]
    public async Task AStationLinksItsOwnStationsAndNoVehicle()
    {
        var @operator = await pos.OperatorAsync();
        var station = await pos.StationAsync(@operator);
        var otherStation = await pos.StationAsync(await pos.OperatorAsync());
        var manager = await StationUserAsync(@operator);

        var own = await Id.AuthedAsync("POST", "/api/v1/station/tickets", manager,
            $$"""{"type":"DEVICE_FAULT","subject":"Reader","body":"Dead","stationId":"{{station}}"}""");
        Assert.Equal(station, (await Json(own, 201))["ticket"]!["stationId"]!.GetValue<string>());

        await AssertLinkRefusedAsync(await Id.AuthedAsync("POST", "/api/v1/station/tickets", manager,
            $$"""{"type":"DEVICE_FAULT","subject":"Reader","body":"Dead","stationId":"{{otherStation}}"}"""), "stationId");
        await AssertLinkRefusedAsync(await Id.AuthedAsync("POST", "/api/v1/station/tickets", manager,
            $$"""{"type":"DISPUTE","subject":"Car","body":"Which car","vehicleId":"{{await VehicleAsync(await Id.SeedCompanyAsync())}}"}"""), "vehicleId");
    }

    /// <summary>
    /// The fueling link through the stand-in (3A has no contract yet): the company that fueled and the operator it fueled at may
    /// link it; another company and another operator get the same answer as an id that does not exist.
    /// </summary>
    [Fact]
    public async Task AFuelingIsALinkForTheCompanyThatFueledAndTheOperatorItFueledAt()
    {
        var company = await Id.SeedCompanyAsync();
        var @operator = await pos.OperatorAsync();
        var fueling = await PlantFuelingAsync(company, @operator);
        var body = $$"""{"type":"DISPUTE","subject":"Charge","body":"Wrong volume","fuelingId":"{{fueling}}"}""";

        var byCompany = await Id.AuthedAsync("POST", "/api/v1/company/tickets", await CompanyUserAsync(company), body);
        Assert.Equal(fueling.ToString(), (await Json(byCompany, 201))["ticket"]!["fuelingId"]!.GetValue<string>());
        var byStation = await Id.AuthedAsync("POST", "/api/v1/station/tickets", await StationUserAsync(@operator), body);
        Assert.Equal(fueling.ToString(), (await Json(byStation, 201))["ticket"]!["fuelingId"]!.GetValue<string>());

        await AssertLinkRefusedAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets",
            await CompanyUserAsync(await Id.SeedCompanyAsync()), body), "fuelingId");
        await AssertLinkRefusedAsync(await Id.AuthedAsync("POST", "/api/v1/station/tickets",
            await StationUserAsync(await pos.OperatorAsync()), body), "fuelingId");
        await AssertLinkRefusedAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", await CompanyUserAsync(company),
            $$"""{"type":"DISPUTE","subject":"Charge","body":"Wrong volume","fuelingId":"{{Guid.NewGuid()}}"}"""), "fuelingId");
    }

    [Fact]
    public async Task EachPortalWritesOnlyForItsOwnKindOfScope()
    {
        var fleet = await CompanyUserAsync(await Id.SeedCompanyAsync());
        var manager = await StationUserAsync(await pos.OperatorAsync());
        var companyTicket = await OpenAsync(fleet, "/api/v1/company/tickets", "OTHER");
        var stationTicket = await OpenAsync(manager, "/api/v1/station/tickets", "OTHER");

        // TICKETS_OPEN is held by both, so the scope decides; the body is never read.
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", manager, """{"type":"OTHER","subject":"x","body":"y"}"""), 403, "FORBIDDEN");
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", "/api/v1/station/tickets", fleet, "{}"), 403, "FORBIDDEN");
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", $"/api/v1/company/tickets/{stationTicket}/messages", manager, """{"body":"x"}"""), 403, "FORBIDDEN");
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", $"/api/v1/station/tickets/{companyTicket}/messages", fleet, """{"body":"x"}"""), 403, "FORBIDDEN");
        // Operations answers tickets; it does not open them.
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", await Ops, """{"type":"OTHER","subject":"x","body":"y"}"""), 403, "FORBIDDEN");

        // The other side's ticket is unknown on the reading portals too.
        await Http.AssertErrorAsync(await Id.AuthedAsync("GET", $"/api/v1/station/tickets/{companyTicket}", manager), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await Id.AuthedAsync("GET", $"/api/v1/company/tickets/{stationTicket}", fleet), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task AReplyToAnotherTenantsTicketIsNotFoundAndWritesNothing()
    {
        var fleet = await CompanyUserAsync(await Id.SeedCompanyAsync());
        var otherFleet = await CompanyUserAsync(await Id.SeedCompanyAsync());
        var ticket = await OpenAsync(fleet, "/api/v1/company/tickets", "TOPUP_QUERY");
        var managerA = await StationUserAsync(await pos.OperatorAsync());
        var managerB = await StationUserAsync(await pos.OperatorAsync());
        var stationTicket = await OpenAsync(managerA, "/api/v1/station/tickets", "PAYOUT_QUERY");

        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", $"/api/v1/company/tickets/{ticket}/messages", otherFleet, """{"body":"Mine now"}"""), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", $"/api/v1/station/tickets/{stationTicket}/messages", managerB, """{"body":"Mine now"}"""), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await Id.AuthedAsync("GET", $"/api/v1/station/tickets/{stationTicket}", managerB), 404, "NOT_FOUND");
        Assert.Equal(1L, await MessageCountAsync(ticket));
        Assert.Equal(1L, await MessageCountAsync(stationTicket));

        // Unknown everywhere answers the same.
        await Http.AssertErrorAsync(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets/{Guid.NewGuid()}", await Ops), 404, "NOT_FOUND");
        await Http.AssertErrorAsync(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{Guid.NewGuid()}/status", await Ops, """{"status":"RESOLVED"}"""), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task ACompanyOrStationReplyIsNeverAnInternalNoteAndLeavesTheStatus()
    {
        var fleet = await CompanyUserAsync(await Id.SeedCompanyAsync());
        var ticket = await OpenAsync(fleet, "/api/v1/company/tickets", "OTHER");
        var reply = await Json(await Id.AuthedAsync("POST", $"/api/v1/company/tickets/{ticket}/messages", fleet,
            """{"body":"  Also this  ","internal":true}"""), 200);
        Assert.Equal("OPEN", reply["ticket"]!["status"]!.GetValue<string>());
        var last = reply["messages"]!.AsArray()[^1]!;
        Assert.False(last["internal"]!.GetValue<bool>());
        Assert.Equal("Also this", last["body"]!.GetValue<string>());
        Assert.Equal("FLEET_MANAGER", last["authorRole"]!.GetValue<string>());
    }

    [Fact]
    public async Task OperationsMovesATicketAlongAndEveryChangeIsAudited()
    {
        var company = await Id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company);
        var opened = await Json(await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            """{"type":"LIMIT_CHANGE","subject"  :"  Raise the limit  ","body":"Please"}"""), 201);
        var ticket = opened["ticket"]!["id"]!.GetValue<string>();
        var reference = opened["ticket"]!["reference"]!.GetValue<string>();
        Assert.Equal("Raise the limit", opened["ticket"]!["subject"]!.GetValue<string>());
        Assert.Equal("COMPANY", opened["ticket"]!["scopeType"]!.GetValue<string>());
        Assert.Null(opened["ticket"]!["operatorId"]);
        Assert.Null(opened["ticket"]!["assignedTo"]);
        Assert.Equal(["id", "reference", "scopeType", "companyId", "operatorId", "type", "subject", "status", "vehicleId", "stationId",
            "fuelingId", "createdAt", "updatedAt", "assignedTo", "resolvedAt"], opened["ticket"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "authorRole", "body", "internal", "createdAt"], opened["messages"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(1L, await api.Factory.Owner.QueryOneAsync<long>("""
            select count(*) from audit_log where entity_id = @id::uuid and action = 'TICKET_OPENED' and entity_type = 'TICKET'
              and scope_type = 'COMPANY' and scope_id = @company and metadata->>'reference' = @reference and metadata->>'type' = 'LIMIT_CHANGE'
            """, new { id = ticket, company, reference }));

        // Taken by an Ops user, resolved, then closed: resolvedAt is kept from the first.
        var opsUser = await Id.SeedAsync(Role.OPS);
        var opsToken = (await Id.SignInAsync(opsUser)).AccessToken;
        var taken = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", opsToken,
            """{"status":"IN_PROGRESS","assignToMe":true}"""), 200);
        Assert.Equal(opsUser.Id.ToString(), taken["ticket"]!["assignedTo"]!.GetValue<string>());
        Assert.Null(taken["ticket"]!["resolvedAt"]);
        var resolved = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", await Ops, """{"status":"RESOLVED"}"""), 200);
        var resolvedAt = resolved["ticket"]!["resolvedAt"]!.GetValue<string>();
        Assert.Equal(opsUser.Id.ToString(), resolved["ticket"]!["assignedTo"]!.GetValue<string>());
        var closed = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", await Ops, """{"status":"CLOSED"}"""), 200);
        Assert.Equal(resolvedAt, closed["ticket"]!["resolvedAt"]!.GetValue<string>());

        // Reopened: no longer resolved, and replies are taken again.
        var reopened = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", await Ops, """{"status":"OPEN"}"""), 200);
        Assert.Null(reopened["ticket"]!["resolvedAt"]);
        await Json(await Id.AuthedAsync("POST", $"/api/v1/company/tickets/{ticket}/messages", fleet, """{"body":"Thanks"}"""), 200);

        var changes = await api.Factory.Owner.QueryAsync<(string, string)>("""
            select metadata->>'from', metadata->>'to' from audit_log
            where entity_id = @id::uuid and action = 'TICKET_STATUS_CHANGED' and scope_type = 'COMPANY' and scope_id = @company
            order by occurred_at
            """, new { id = ticket, company });
        Assert.Equal([("OPEN", "IN_PROGRESS"), ("IN_PROGRESS", "RESOLVED"), ("RESOLVED", "CLOSED"), ("CLOSED", "OPEN")], changes);
    }

    [Fact]
    public async Task AnAnswerTakesUpAnOpenTicketAndKeepsItsAssignee()
    {
        var manager = await StationUserAsync(await pos.OperatorAsync());
        var ticket = await OpenAsync(manager, "/api/v1/station/tickets", "PRICE_QUERY");
        var first = await Id.SeedAsync(Role.OPS);
        var firstToken = (await Id.SignInAsync(first)).AccessToken;
        var answered = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", firstToken, """{"body":"Looking"}"""), 200);
        Assert.Equal("IN_PROGRESS", answered["ticket"]!["status"]!.GetValue<string>());
        Assert.Equal(first.Id.ToString(), answered["ticket"]!["assignedTo"]!.GetValue<string>());

        // Reopened, then answered by someone else: the assignee stays.
        await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", await Ops, """{"status":"OPEN"}"""), 200);
        var again = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", await Ops, """{"body":"Fixed"}"""), 200);
        Assert.Equal("IN_PROGRESS", again["ticket"]!["status"]!.GetValue<string>());
        Assert.Equal(first.Id.ToString(), again["ticket"]!["assignedTo"]!.GetValue<string>());

        // An internal note on an open ticket leaves it open.
        var quiet = await OpenAsync(manager, "/api/v1/station/tickets", "OTHER");
        var noted = await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{quiet}/messages", await Ops, """{"body":"n","internal":true}"""), 200);
        Assert.Equal("OPEN", noted["ticket"]!["status"]!.GetValue<string>());
        Assert.Null(noted["ticket"]!["assignedTo"]);
        Assert.True(noted["messages"]!.AsArray()[^1]!["internal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OperationsListsAndFiltersEveryTicket()
    {
        var company = await Id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company);
        var @operator = await pos.OperatorAsync();
        var manager = await StationUserAsync(@operator);
        var lost = await OpenAsync(fleet, "/api/v1/company/tickets", "LOST_TAG");
        var dispute = await OpenAsync(fleet, "/api/v1/company/tickets", "DISPUTE");
        var fault = await OpenAsync(manager, "/api/v1/station/tickets", "DEVICE_FAULT");
        await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{dispute}/status", await Ops, """{"status":"RESOLVED"}"""), 200);

        // Newest first is by id (UUIDv7); PostgreSQL orders uuids as their text.
        var newestFirst = new[] { dispute, lost }.OrderByDescending(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal(newestFirst, await IdsAsync(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets?companyId={company}", await Ops)));
        Assert.Equal([fault], await IdsAsync(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets?operatorId={@operator}", await Ops)));
        Assert.Equal([lost], await IdsAsync(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets?companyId={company}&type=LOST_TAG", await Ops)));
        Assert.Equal([dispute], await IdsAsync(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets?companyId={company}&status=RESOLVED", await Ops)));
        Assert.Equal([lost], await IdsAsync(await Id.AuthedAsync("GET", "/api/v1/company/tickets?status=OPEN", fleet)));

        // Pages of one, newest first, by cursor.
        var page = await Json(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets?companyId={company}&limit=1", await Ops), 200);
        Assert.Equal(newestFirst[0], page["items"]![0]!["id"]!.GetValue<string>());
        var cursor = page["nextCursor"]!.GetValue<string>();
        var next = await Json(await Id.AuthedAsync("GET", $"/api/v1/ops/tickets?companyId={company}&limit=1&cursor={cursor}", await Ops), 200);
        Assert.Equal(newestFirst[1], next["items"]![0]!["id"]!.GetValue<string>());
        Assert.Null(next["nextCursor"]);

        await Http.AssertErrorAsync(await Id.AuthedAsync("GET", "/api/v1/ops/tickets?cursor=nope", await Ops), 400, "INVALID_CURSOR");
        Assert.Equal(400, (int)(await Id.AuthedAsync("GET", "/api/v1/ops/tickets?type=FLAT_TYRE", await Ops)).StatusCode);
    }

    [Fact]
    public async Task ABadRequestNamesItsField()
    {
        var fleet = await CompanyUserAsync(await Id.SeedCompanyAsync());
        await AssertInvalidAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet, """{"subject":"x","body":"y"}"""), "type", "NOT_NULL");
        await AssertInvalidAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet, """{"type":"OTHER","subject":"  ","body":"y"}"""), "subject", "NOT_BLANK");
        await AssertInvalidAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            JsonSerializer.Serialize(new { type = "OTHER", subject = new string('s', 201), body = "y" })), "subject", "SIZE");
        await AssertInvalidAsync(await Id.AuthedAsync("POST", "/api/v1/company/tickets", fleet,
            JsonSerializer.Serialize(new { type = "OTHER", subject = "s", body = new string('b', 4001) })), "body", "SIZE");
        var ticket = await OpenAsync(fleet, "/api/v1/company/tickets", "OTHER");
        await AssertInvalidAsync(await Id.AuthedAsync("POST", $"/api/v1/company/tickets/{ticket}/messages", fleet, "{}"), "body", "NOT_BLANK");
        await AssertInvalidAsync(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/status", await Ops, "{}"), "status", "NOT_NULL");
        Assert.Equal(1L, await MessageCountAsync(ticket));
    }

    /// <summary>What row-level security alone does, as the runtime role: the API's filters are not the only guard.</summary>
    [Fact]
    public async Task RowLevelSecurityKeepsTicketsAndNotesToTheirSide()
    {
        var company = await Id.SeedCompanyAsync();
        var fleet = await CompanyUserAsync(company);
        var ticket = Guid.Parse(await OpenAsync(fleet, "/api/v1/company/tickets", "OTHER"));
        await Json(await Id.AuthedAsync("POST", $"/api/v1/ops/tickets/{ticket}/messages", await Ops, """{"body":"note","internal":true}"""), 200);
        var owner = api.Factory.Owner;

        Assert.Equal(1L, await owner.AsRuntimeRoleAsync("COMPANY", company, null, (c, tx) =>
            c.ExecuteScalarAsync<long>("select count(*) from ticket_messages where ticket_id = @ticket", new { ticket }, tx)));
        Assert.Equal(2L, await owner.AsRuntimeRoleAsync("PLATFORM", null, null, (c, tx) =>
            c.ExecuteScalarAsync<long>("select count(*) from ticket_messages where ticket_id = @ticket", new { ticket }, tx)));
        Assert.Equal(0L, await owner.AsRuntimeRoleAsync("COMPANY", Guid.NewGuid(), null, (c, tx) =>
            c.ExecuteScalarAsync<long>("select count(*) from tickets where id = @ticket", new { ticket }, tx)));
        // Only Operations changes a status: the company's update passes the column grant and meets no row.
        Assert.Equal(0, await owner.AsRuntimeRoleAsync("COMPANY", company, null, (c, tx) =>
            c.ExecuteAsync("update tickets set status = 'IN_PROGRESS' where id = @ticket", new { ticket }, tx)));
    }

    // ------------------------------------------------------------------------------------------------------- helpers

    private async Task<string> CompanyUserAsync(Guid company) =>
        (await Id.SignInAsync(await Id.SeedAsync(Role.FLEET_MANAGER, Scope.Company(company), false))).AccessToken;

    private async Task<string> StationUserAsync(string @operator) =>
        (await Id.SignInAsync(await Id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.Parse(@operator)), false))).AccessToken;

    private async Task<string> OpenAsync(string token, string path, string type)
    {
        var opened = await Id.AuthedAsync("POST", path, token, $$"""{"type":"{{type}}","subject":"About {{type}}","body":"Please look"}""");
        return (await Json(opened, 201))["ticket"]!["id"]!.GetValue<string>();
    }

    private async Task<string> VehicleAsync(Guid company)
    {
        var plate = "TK " + Guid.NewGuid().ToString()[..8].ToUpperInvariant();
        var created = await Id.AuthedAsync("POST", "/api/v1/ops/vehicles", await Ops,
            $$$"""{"companyId":"{{{company}}}","vehicle":{"plate":"{{{plate}}}","make":"Toyota","model":"Hilux","fuelType":"GAS_92","tankCapacityMl":60000,"tier":"NORMAL"}}""");
        return (await Json(created, 201))["id"]!.GetValue<string>();
    }

    /// <summary>
    /// A declined fueling (no money moved) of a vehicle of <paramref name="company"/> at a station of <paramref name="operator"/>,
    /// written as the owner: the fueling engine is wave 3A.
    /// </summary>
    private async Task<Guid> PlantFuelingAsync(Guid company, string @operator)
    {
        var vehicle = await VehicleAsync(company);
        var issued = await Id.AuthedAsync("POST", $"/api/v1/ops/vehicles/{vehicle}/tags", await Ops,
            $$"""{"uid":"{{Guid.NewGuid().ToString("N")[..14].ToUpperInvariant()}}"}""");
        Assert.True((int)issued.StatusCode is 200 or 201, await issued.Content.ReadAsStringAsync());
        var station = await pos.StationAsync(@operator);
        var device = await pos.RegisterDeviceAsync(station);
        var (workerId, _) = await pos.AgentAsync(station);
        var owner = api.Factory.Owner;
        var tag = await owner.QueryOneAsync<Guid>("select id from rfid_tags where vehicle_id = @vehicle::uuid", new { vehicle });
        var agent = await owner.QueryOneAsync<Guid>("select id from agents where worker_id = @workerId and station_id = @station::uuid",
            new { workerId, station });
        var id = Guid.CreateVersion7();
        await owner.ExecuteAsync("""
            insert into fuelings (id, client_txn_id, reference, status, decline_reason, origin, company_id, vehicle_id, tag_id, operator_id,
                                  station_id, device_id, agent_id, fuel_type, volume_ml, unit_price_piastres, amount_piastres, billable,
                                  evaluated_at, device_at, received_at, receipt_locale, plate, company_name_ar, company_name_en,
                                  station_name_ar, station_name_en, agent_worker_id)
            values (@id, @txn, @reference, 'DECLINED', 'COOLDOWN', 'ONLINE', @company, @vehicle::uuid, @tag, @operator::uuid,
                    @station::uuid, @device::uuid, @agent, 'GAS_92', 1000, 0, 0, false,
                    now(), now(), now(), 'en', 'TK', 'شركة', 'Company', 'محطة', 'Station', @workerId)
            """, new
        {
            id, txn = Guid.NewGuid(), reference = "F-T" + id.ToString("N")[..12], company, vehicle, tag, @operator, station,
            device = device.Id, agent, workerId,
        });
        return id;
    }

    private Task<long> MessageCountAsync(string ticket) =>
        api.Factory.Owner.QueryOneAsync<long>("select count(*) from ticket_messages where ticket_id = @ticket::uuid", new { ticket });

    private static async Task<List<string>> IdsAsync(HttpResponseMessage response) =>
        [.. (await Json(response, 200))["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>())];

    private static async Task AssertLinkRefusedAsync(HttpResponseMessage response, string field)
    {
        var body = await Http.AssertErrorAsync(response, 400, "TICKET_LINK_NOT_FOUND");
        Assert.Equal(field, body.GetProperty("field").GetString());
    }

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string field, string code)
    {
        var body = await Http.AssertErrorAsync(response, 400, "VALIDATION_FAILED");
        var issue = Assert.Single(body.GetProperty("fields").EnumerateArray());
        Assert.Equal(field, issue.GetProperty("field").GetString());
        Assert.Equal(code, issue.GetProperty("code").GetString());
    }
}
