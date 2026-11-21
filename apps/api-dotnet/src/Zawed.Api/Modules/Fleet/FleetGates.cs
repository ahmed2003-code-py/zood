using System.Security.Cryptography;
using System.Text;
using Zawed.Api.Modules.Fleet.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;
using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fleet;

/// <summary><see cref="ITagGate"/> on the caller's transaction, with the Java statements (Java <c>FleetTagGate</c>).</summary>
internal sealed class FleetTagGate(IDbSession session, LookupHasher lookup, TimeProvider clock) : ITagGate
{
    private DbTx Tx => session.Current;

    public async Task<BoundTag?> FindAsync(string? rawUid, CancellationToken ct = default)
    {
        if (Tags.NormalizeUid(rawUid) is not { } uid)
        {
            return null;
        }
        var found = (await Tx.QueryAsync<(Guid Id, Guid CompanyId)>("select id, company_id from rfid_tags where uid_lookup = @lookup",
            new { lookup = lookup.Hash(Tags.LookupNamespace, uid) }, ct)).ToList();
        return found is [var tag] ? new BoundTag(tag.Id, tag.CompanyId) : null;
    }

    public async Task<TagAtPump> LockAsync(Guid tagId, CancellationToken ct = default)
    {
        // Vehicle before tag, the order the fleet's own changes take (vehicle FOR UPDATE, then its tags): a tag reported
        // stolen while in use waits instead of deadlocking (B4 review 3). A tag never changes vehicle, so reading its vehicle
        // id first is safe.
        await Tx.QueryAsync<Guid>("select v.id from vehicles v join rfid_tags t on t.vehicle_id = v.id where t.id = @id for share of v",
            new { id = tagId }, ct);
        var row = (await Tx.QueryAsync<AtPumpRow>("""
            select t.id as TagId, t.vehicle_id as VehicleId, t.company_id as CompanyId, t.status as TagStatus,
                   t.cooldown_until as CooldownUntil, v.plate as Plate, v.fuel_type as FuelType, v.status as VehicleStatus,
                   v.tier as Tier, v.tank_capacity_ml as TankCapacityMl, v.vip_monthly_ceiling_ml as VipMonthlyCeilingMl
            from rfid_tags t join vehicles v on v.id = t.vehicle_id
            where t.id = @id
            for update of t
            """, new { id = tagId }, ct)).SingleOrDefault() ?? throw ApiException.NotFound("Tag");
        return new TagAtPump(row.TagId, row.VehicleId, row.CompanyId, row.TagStatus == "ACTIVE", Utc(row.CooldownUntil), row.Plate,
            Enum.Parse<FuelType>(row.FuelType), row.VehicleStatus == "ACTIVE", row.Tier == "VIP", row.TankCapacityMl, row.VipMonthlyCeilingMl);
    }

    public async Task<IReadOnlyList<TagLimit>> ActiveLimitsAsync(Guid vehicleId, CancellationToken ct = default) =>
        (await Tx.QueryAsync<(string Kind, string Period, long MaxValue)>(
            "select kind, period, max_value from limit_rules where vehicle_id = @vehicle and active", new { vehicle = vehicleId }, ct))
        .Select(r => new TagLimit(Enum.Parse<FuelingRules.LimitKind>(r.Kind), Enum.Parse<Periods.Period>(r.Period), r.MaxValue)).ToList();

    public Task StartCooldownAsync(Guid tagId, DateTimeOffset until, CancellationToken ct = default) =>
        Tx.ExecuteAsync("update rfid_tags set cooldown_until = @until, updated_at = @now where id = @id",
            new { id = tagId, until = until.UtcDateTime, now = clock.GetUtcNow().UtcDateTime }, ct);

    internal static DateTimeOffset? Utc(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed record AtPumpRow(Guid TagId, Guid VehicleId, Guid CompanyId, string TagStatus, DateTime? CooldownUntil, string Plate,
        string FuelType, string VehicleStatus, string Tier, long TankCapacityMl, long? VipMonthlyCeilingMl);
}

/// <summary><see cref="IFleetSnapshot"/> on the caller's transaction (Java <c>FleetSnapshotReader</c>).</summary>
internal sealed class FleetSnapshotReader(IDbSession session, DataEncryptor encryptor, LookupHasher lookup, ILogger<FleetSnapshotReader> log)
    : IFleetSnapshot
{
    /// <summary>At most this many ids per log line, so a retired key cannot flood the log on every device poll.</summary>
    private const int LoggedIds = 20;

    private DbTx Tx => session.Current;

    public async Task<SnapshotFleet> SnapshotAsync(byte[] digestKey, CancellationToken ct = default)
    {
        using var mac = new HMACSHA256(digestKey);
        var tags = new List<SnapshotTag>();
        var vehicles = new Dictionary<Guid, SnapshotVehicle>();
        var order = new List<Guid>();
        var unreadable = new List<Guid>();
        var mismatched = new List<Guid>();
        // A tag that can fuel, or that is waiting for its replacement (the device then says why it is refused); its vehicle
        // not decommissioned.
        var rows = await Tx.QueryAsync<TagRow>("""
            select t.id as Id, t.vehicle_id as VehicleId, t.uid_encrypted as UidEncrypted, t.uid_lookup as UidLookup, t.status as Status,
                   t.cooldown_until as CooldownUntil, v.company_id as CompanyId, v.plate as Plate, v.fuel_type as FuelType, v.tier as Tier,
                   v.tank_capacity_ml as TankCapacityMl, v.status as VehicleStatus
            from rfid_tags t join vehicles v on v.id = t.vehicle_id
            where t.status in ('ACTIVE', 'REPLACEMENT_PENDING') and v.status <> 'DECOMMISSIONED'
            """, null, ct);
        foreach (var row in rows)
        {
            byte[] uid;
            try
            {
                uid = encryptor.Decrypt(row.UidEncrypted, Tags.AssociatedData(row.Id));
            }
            catch (ArgumentException)
            {
                // A key this instance does not hold, or a value that does not verify: that tag cannot be fueled offline, but
                // every other device still gets every other tag.
                unreadable.Add(row.Id);
                continue;
            }
            // The ciphertext must be the UID the tag was issued with. uid_lookup is made with the lookup key, which neither a
            // data key nor the runtime role can change (V14, V20), so a UID swapped in under a data key, or a lookup key that is
            // not the original, never reaches a device.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(lookup.Hash(Tags.LookupNamespace, Encoding.ASCII.GetString(uid))),
                Encoding.ASCII.GetBytes(row.UidLookup)))
            {
                mismatched.Add(row.Id);
                continue;
            }
            if (uid.Length < IFleetSnapshot.ShortestUidInSnapshot)
            {
                continue;
            }
            var digest = Convert.ToHexStringLower(mac.ComputeHash(uid))[..IFleetSnapshot.DigestHexLength];
            tags.Add(new SnapshotTag(digest, row.VehicleId, row.Status, FleetTagGate.Utc(row.CooldownUntil)));
            if (vehicles.TryAdd(row.VehicleId, new SnapshotVehicle(row.VehicleId, row.CompanyId, row.Plate, Enum.Parse<FuelType>(row.FuelType),
                row.Tier == "VIP", row.TankCapacityMl, row.VehicleStatus == "ACTIVE")))
            {
                order.Add(row.VehicleId);
            }
        }
        if (unreadable.Count > 0 || mismatched.Count > 0)
        {
            // Ids and counts only, never a value. An error, so it reaches Sentry once a DSN is set.
            log.LogError("Offline snapshot: {Left} tags left out; {Unreadable} could not be decrypted {UnreadableIds}, {Mismatched} do not "
                + "match their lookup hash {MismatchedIds}", unreadable.Count + mismatched.Count, unreadable.Count, FirstIds(unreadable),
                mismatched.Count, FirstIds(mismatched));
        }
        var limits = (await Tx.QueryAsync<(Guid VehicleId, string Kind, string Period, long MaxValue)>(
                "select vehicle_id, kind, period, max_value from limit_rules where active", null, ct))
            .Where(l => vehicles.ContainsKey(l.VehicleId))
            .Select(l => new SnapshotLimit(l.VehicleId, Enum.Parse<FuelingRules.LimitKind>(l.Kind), Enum.Parse<Periods.Period>(l.Period), l.MaxValue))
            .ToList();
        return new SnapshotFleet(tags, [.. order.Select(id => vehicles[id])], limits);
    }

    private static string FirstIds(List<Guid> ids) => ids.Count <= LoggedIds
        ? "[" + string.Join(", ", ids) + "]"
        : "[" + string.Join(", ", ids.Take(LoggedIds)) + "] and " + (ids.Count - LoggedIds) + " more";

    private sealed record TagRow(Guid Id, Guid VehicleId, byte[] UidEncrypted, string UidLookup, string Status, DateTime? CooldownUntil,
        Guid CompanyId, string Plate, string FuelType, string Tier, long TankCapacityMl, string VehicleStatus);
}
