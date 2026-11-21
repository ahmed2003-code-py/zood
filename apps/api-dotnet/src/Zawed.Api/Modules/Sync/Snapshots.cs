using System.Globalization;
using Zawed.Api.Modules.Fleet.Contracts;
using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Modules.Pricing.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Sync;

/// <summary>Java <c>Snapshots.SnapshotWalletStatus</c>.</summary>
public enum SnapshotWalletStatus
{
    AVAILABLE,
    LOW,
    INSUFFICIENT,
    SUSPENDED,
}

/// <summary>What a device needs to decide fuelings offline (B5 contract §2; Java <c>Snapshots.PosSnapshot</c>).</summary>
public sealed record PosSnapshot(DateTimeOffset ServerTime, int Contract, bool VipEnabled, SnapshotStation Station,
    IReadOnlyList<SnapshotPrice> Prices, string TagKey, IReadOnlyList<SnapshotTag> Tags, IReadOnlyList<SnapshotVehicle> Vehicles,
    IReadOnlyList<SnapshotLimitUsage> Limits, IReadOnlyList<SnapshotCompany> Companies);

/// <param name="FuelTypes">the fuels the station sells, in the order of <see cref="FuelType"/> (Java's <c>EnumSet</c>)</param>
public sealed record SnapshotStation(Guid Id, string NameAr, string NameEn, string TimeZone, IReadOnlyList<FuelType> FuelTypes, bool Open);

public sealed record SnapshotPrice(FuelType FuelType, bool National, long PricePiastresPerLitre, DateTimeOffset EffectiveFrom);

/// <param name="Remaining">what the vehicle may still take in the period that contains serverTime</param>
public sealed record SnapshotLimitUsage(Guid VehicleId, FuelingRules.LimitKind Kind, Periods.Period Period, long Remaining,
    DateTimeOffset PeriodStart);

public sealed record SnapshotCompany(Guid Id, string NameAr, string NameEn, bool Active, int CooldownMinutes, SnapshotWalletStatus WalletStatus,
    long OfflineAllowancePiastres);

/// <summary>
/// The snapshot a POS caches to decide fuelings offline (B5 contract §2; Java <c>Snapshots</c>). What it leaves out is as
/// deliberate as what it carries: no tag UIDs (each tag is a digest under a key for this device only), no wallet balances
/// (an allowance capped by configuration), no PIN hashes.
/// <para>A device has no tenant scope; the snapshot is read in the platform scope after the POS chain has verified the
/// device (an escalation listed in <c>ArchitectureTests</c>).</para>
/// </summary>
public sealed class Snapshots(ITransactions transactions, IServiceScopeFactory scopes, LookupHasher keys, IRateLimiter limiter,
    TimeProvider clock, SyncSettings settings)
{
    /// <summary>How far ahead scheduled price changes are included.</summary>
    public static readonly TimeSpan PriceHorizon = TimeSpan.FromDays(7);

    public const string TagKeyNamespace = "pos-tag-digest";

    private const int SnapshotsPerMinute = 6;

    /// <summary>100 EGP: the allowance is shown in steps, never to the piastre.</summary>
    public const long AllowanceStep = 10_000;

    public async Task<PosSnapshot> SnapshotForAsync(PosPrincipal device, CancellationToken ct)
    {
        limiter.Acquire("pos-snapshot:" + device.DeviceId, SnapshotsPerMinute, TimeSpan.FromMinutes(1));
        // Microsecond precision, as Java's clock gives it.
        var utc = clock.GetUtcNow();
        var now = utc.AddTicks(-(utc.Ticks % 10));
        var tagKey = TagKey(device.DeviceId);
        return await transactions.RunAsync(DbScopeContext.Escalate(Scope.Platform, null), async tx =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            session.Attach(tx);
            try
            {
                return await ReadAsync(scope.ServiceProvider, device, now, tagKey, ct);
            }
            finally
            {
                session.Detach();
            }
        }, ct: ct);
    }

    private async Task<PosSnapshot> ReadAsync(IServiceProvider services, PosPrincipal device, DateTimeOffset now, byte[] tagKey, CancellationToken ct)
    {
        var stations = services.GetRequiredService<IStationDirectory>();
        var prices = services.GetRequiredService<IPriceBook>();
        var fleet = services.GetRequiredService<IFleetSnapshot>();
        var usage = services.GetRequiredService<IFuelingApi>();
        var wallets = services.GetRequiredService<IWalletBalances>();
        var companies = services.GetRequiredService<ICompanyDirectory>();

        var station = await stations.FindStationAsync(device.StationId, ct)
            ?? throw new ApiException(409, "STATION_NOT_ACTIVE", "The station is not open");
        var zone = station.Zone;

        var schedule = new List<SnapshotPrice>();
        foreach (var fuel in Enum.GetValues<FuelType>())
        {
            if (station.FuelTypes.Contains(fuel))
            {
                foreach (var p in await prices.EntriesForAsync(station.Id, fuel, now, now + PriceHorizon, ct))
                {
                    schedule.Add(new SnapshotPrice(p.FuelType, p.StationId is null, p.PricePiastresPerLitre, p.EffectiveFrom));
                }
            }
        }

        var snapshot = await fleet.SnapshotAsync(tagKey, ct);
        var day = Periods.Start(now, Periods.Period.DAY, zone);
        var week = Periods.Start(now, Periods.Period.WEEK, zone);
        var month = Periods.Start(now, Periods.Period.MONTH, zone);
        var used = await usage.UsageSinceAsync(day, week, month, ct);
        var limits = new List<SnapshotLimitUsage>();
        foreach (var rule in snapshot.Limits)
        {
            var u = used.GetValueOrDefault(rule.VehicleId, FuelingUsage.None);
            var volume = rule.Kind == FuelingRules.LimitKind.VOLUME;
            var spent = rule.Period switch
            {
                Periods.Period.DAY => volume ? u.DayMl : u.DayPiastres,
                Periods.Period.WEEK => volume ? u.WeekMl : u.WeekPiastres,
                _ => volume ? u.MonthMl : u.MonthPiastres,
            };
            var start = rule.Period switch
            {
                Periods.Period.DAY => day,
                Periods.Period.WEEK => week,
                _ => month,
            };
            // What is left, not the rule and its use: the use is the company's consumption at every station, which another
            // operator's terminal has no reason to see (B5 review 4).
            limits.Add(new SnapshotLimitUsage(rule.VehicleId, rule.Kind, rule.Period, Math.Max(0, rule.MaxValue - spent), start));
        }

        var companyIds = snapshot.Vehicles.Select(v => v.CompanyId).ToHashSet();
        var balances = await wallets.BalancesAsync(companyIds, ct);
        var companyList = (await companies.FindAllAsync(companyIds, ct))
            .Select(c => Company(c, balances.GetValueOrDefault(c.Id, 0L)))
            .ToList();

        return new PosSnapshot(now, ApiContract.PosContract, settings.VipEnabled,
            new SnapshotStation(station.Id, station.NameAr, station.NameEn, station.TimeZone, station.FuelTypes, station.Active),
            schedule, Convert.ToBase64String(tagKey), snapshot.Tags,
            settings.VipEnabled
                ? snapshot.Vehicles
                : snapshot.Vehicles.Select(v => v with { Vip = false }).ToList(),
            limits, companyList);
    }

    /// <summary>The wallet as a word and a capped allowance: enough to decide offline, never the balance.</summary>
    private SnapshotCompany Company(CompanyRef c, long balance)
    {
        var status = !c.Active ? SnapshotWalletStatus.SUSPENDED
            : balance <= 0 ? SnapshotWalletStatus.INSUFFICIENT
            : balance < c.LowBalanceThresholdPiastres ? SnapshotWalletStatus.LOW
            : SnapshotWalletStatus.AVAILABLE;
        // Rounded down to a step, so a balance under the cap is not revealed to the piastre (B5 review 4).
        var capped = c.Active ? Math.Max(0, Math.Min(balance, settings.OfflineAllowanceCapPiastres)) : 0;
        var allowance = capped - capped % AllowanceStep;
        return new SnapshotCompany(c.Id, c.NameAr, c.NameEn, c.Active, c.CooldownMinutes, status, allowance);
    }

    /// <summary>This device's tag-digest key: a different key per device, so one device's digests are useless on another.</summary>
    public byte[] TagKey(Guid deviceId) =>
        Convert.FromHexString(keys.Hash(TagKeyNamespace, deviceId.ToString("D", CultureInfo.InvariantCulture)));
}
