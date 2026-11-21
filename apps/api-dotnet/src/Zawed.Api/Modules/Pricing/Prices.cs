using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Zawed.Api.Modules.Pricing.Contracts;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Domain;

namespace Zawed.Api.Modules.Pricing;

/// <summary>
/// Fuel prices (roadmap delta M1; Java <c>pricing.Prices</c>). Prices are insert-only and effective-dated: a change is
/// a new row scheduled for a time, so the price that applied to any past fueling can always be recomputed. Devices
/// receive scheduled prices ahead and switch at the effective time, offline too.
/// <para>The <see cref="IPriceBook"/> reads price fuelings, so they keep the Java SQL as it is (through
/// <c>FromSql</c>); the Ops screens use LINQ.</para>
/// </summary>
public sealed class Prices(IDbSession session, TimeProvider clock, IAuditLog audit, IOperatorDirectory operators) : IPriceBook
{
    /// <summary>A few seconds of clock difference between Ops' browser and the server is not backdating.</summary>
    public static readonly TimeSpan BackdateTolerance = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxLead = TimeSpan.FromDays(366);

    private ZawedDbContext Db => session.Current.Db();

    private static readonly Expression<Func<FuelPrice, PriceEntryResponse>> ToEntry = p =>
        new PriceEntryResponse(p.Id, p.FuelType, p.StationId, p.PricePiastres, p.EffectiveFrom, p.Note, p.CreatedAt);

    // ---------------------------------------------------------------------------------------------- price book

    public async Task<Price?> PriceAtAsync(Guid stationId, FuelType fuelType, DateTimeOffset at, CancellationToken ct = default) =>
        AsPrice(await FirstAsync($"""
            select * from fuel_prices
            where fuel_type = {fuelType.ToString()} and effective_from <= {at}
              and (station_id is null or station_id = {stationId})
            order by effective_from desc, station_id nulls last
            limit 1
            """, ct));

    public async Task<IReadOnlyList<Price>> EntriesForAsync(Guid stationId, FuelType fuelType, DateTimeOffset now, DateTimeOffset until,
        CancellationToken ct = default)
    {
        var entries = new List<Price>();
        if (await NationalAsync(fuelType, now, ct) is { } national)
        {
            entries.Add(national);
        }
        if (AsPrice(await FirstAsync($"""
            select * from fuel_prices
            where fuel_type = {fuelType.ToString()} and station_id = {stationId} and effective_from <= {now}
            order by effective_from desc limit 1
            """, ct)) is { } own)
        {
            entries.Add(own);
        }
        var changes = await Db.Set<FuelPrice>().FromSql($"""
            select * from fuel_prices
            where fuel_type = {fuelType.ToString()} and (station_id is null or station_id = {stationId})
              and effective_from > {now} and effective_from <= {until}
            order by effective_from, station_id nulls first
            """).ToListAsync(ct);
        entries.AddRange(changes.Select(p => AsPrice(p)!));
        return entries;
    }

    // ------------------------------------------------------------------------------------------------- Ops reads

    /// <summary>The price in force now for every fuel, at one station or nationally.</summary>
    public async Task<Result<IReadOnlyList<PriceResponse>>> CurrentAsync(Guid? stationId, CancellationToken ct)
    {
        if (stationId is { } station && !await StationExistsAsync(station, ct))
        {
            return Result<IReadOnlyList<PriceResponse>>.NotFound("Station");
        }
        var now = clock.GetUtcNow();
        var prices = new List<PriceResponse>();
        foreach (var fuel in Enum.GetValues<FuelType>())
        {
            var price = stationId is { } at ? await PriceAtAsync(at, fuel, now, ct) : await NationalAsync(fuel, now, ct);
            if (price is not null)
            {
                prices.Add(new PriceResponse(price.FuelType, price.StationId, price.PricePiastresPerLitre, price.EffectiveFrom));
            }
        }
        return prices;
    }

    /// <summary>Every price, past and scheduled, newest first.</summary>
    public async Task<Result<Page<PriceEntryResponse>>> HistoryAsync(FuelType? fuelType, Guid? stationId, bool nationalOnly, string? cursor,
        int? limit, CancellationToken ct)
    {
        var size = Page<PriceEntryResponse>.Limit(limit);
        var after = Cursors.Parse(cursor);
        var prices = Db.Set<FuelPrice>().AsQueryable();
        if (fuelType is { } fuel)
        {
            prices = prices.Where(p => p.FuelType == fuel);
        }
        if (stationId is { } station)
        {
            prices = prices.Where(p => p.StationId == station);
        }
        if (nationalOnly)
        {
            prices = prices.Where(p => p.StationId == null);
        }
        if (after is { } last)
        {
            prices = prices.Where(p => p.Id < last);
        }
        var rows = await prices.OrderByDescending(p => p.Id).Take(size + 1).Select(ToEntry).ToListAsync(ct);
        return Page<PriceEntryResponse>.Of(rows, size, p => p.Id);
    }

    // ------------------------------------------------------------------------------------------------- schedule

    public async Task<Result<PriceEntryResponse>> ScheduleAsync(ZawedPrincipal actor, ScheduleRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var from = request.EffectiveFrom ?? now;
        if (from < now - BackdateTolerance)
        {
            return Result<PriceEntryResponse>.Fail(400, "PRICE_BACKDATED",
                "A price cannot take effect in the past: past fuelings keep the price they had", "effectiveFrom");
        }
        // Within the tolerance the request is accepted, but the price never takes effect before now: a fueling
        // already priced in the last minute must keep its price.
        if (from < now)
        {
            from = now;
        }
        if (from > now + MaxLead)
        {
            return Result<PriceEntryResponse>.Fail(400, "PRICE_TOO_FAR_AHEAD", "Schedule at most a year ahead", "effectiveFrom");
        }
        if (request.StationId is { } station && !await StationExistsAsync(station, ct))
        {
            return Result<PriceEntryResponse>.Fail(400, "STATION_NOT_FOUND", "No such station", "stationId");
        }
        var fuelType = request.FuelType!.Value;
        var pricePiastres = request.PricePiastres!.Value;
        var price = new FuelPrice
        {
            Id = Guid.CreateVersion7(now), FuelType = fuelType, StationId = request.StationId, PricePiastres = pricePiastres,
            EffectiveFrom = from, Note = Sql.Clean(request.Note), CreatedAt = now, CreatedBy = actor.UserId,
        };
        Db.Add(price);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, FuelPriceConfiguration.OnePerInstantIndex))
        {
            return Result<PriceEntryResponse>.Fail(409, "PRICE_EXISTS", "A price for this fuel and place already takes effect at that time",
                "effectiveFrom");
        }
        var details = new Dictionary<string, object?>
        {
            ["fuelType"] = fuelType.ToString(), ["pricePiastres"] = pricePiastres, ["effectiveFrom"] = JavaInstant(from),
        };
        if (request.StationId is { } overridden)
        {
            details["stationId"] = overridden.ToString();
        }
        await audit.RecordAsync(session.Current, ActorOf(actor), "PRICE_SCHEDULED", "FUEL_PRICE", price.Id, Scope.Platform, details, ct);
        return Result<PriceEntryResponse>.Created(await Db.Set<FuelPrice>().Where(p => p.Id == price.Id).Select(ToEntry).SingleAsync(ct));
    }

    // -------------------------------------------------------------------------------------------------- helpers

    private async Task<Price?> NationalAsync(FuelType fuel, DateTimeOffset at, CancellationToken ct) =>
        AsPrice(await FirstAsync($"""
            select * from fuel_prices
            where fuel_type = {fuel.ToString()} and station_id is null and effective_from <= {at}
            order by effective_from desc limit 1
            """, ct));

    /// <summary>The one row of a <c>limit 1</c> statement, sent as written (nothing composed around it).</summary>
    private async Task<FuelPrice?> FirstAsync(FormattableString sql, CancellationToken ct) =>
        (await Db.Set<FuelPrice>().FromSql(sql).ToListAsync(ct)).SingleOrDefault();

    private static Price? AsPrice(FuelPrice? p) => p is null ? null : new Price(p.FuelType, p.StationId, p.PricePiastres, p.EffectiveFrom);

    /// <summary>Whether the station exists, whatever its status (Java <c>OperatorDirectory.stationExists</c>).</summary>
    private Task<bool> StationExistsAsync(Guid stationId, CancellationToken ct) => operators.StationExistsAsync(stationId, ct);

    /// <summary>Java's <c>Instant.toString()</c>: seconds always, the fraction in groups of three digits when there is one.</summary>
    internal static string JavaInstant(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond * 100; // nanoseconds
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += fraction % 1_000_000 == 0 ? "." + (fraction / 1_000_000).ToString("D3", CultureInfo.InvariantCulture)
                : fraction % 1_000 == 0 ? "." + (fraction / 1_000).ToString("D6", CultureInfo.InvariantCulture)
                : "." + fraction.ToString("D9", CultureInfo.InvariantCulture);
        }
        return text + "Z";
    }

    private static AuditActor ActorOf(ZawedPrincipal actor) => new(actor.UserId, actor.Role);
}
