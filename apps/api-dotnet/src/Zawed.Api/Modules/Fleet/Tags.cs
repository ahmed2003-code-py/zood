using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// RFID tags (PRD §4.4, §7.3; Java <c>Tags</c>). A tag is the vehicle's key at the pump, so it is handled like a
/// credential:
/// <list type="bullet">
/// <item>The UID is never stored in clear. It is kept encrypted (AES-GCM, bound to the tag id) and as a keyed hash that
/// finds it; responses show the last four characters only.</item>
/// <item>A physical tag is bound once, ever. A lost or stolen tag cannot be bound again, to this vehicle or any other.</item>
/// <item>A vehicle has at most one active tag. Replacing a tag carries the cooldown over, so swapping tags cannot be used
/// to fuel again before the cooling-off period ends.</item>
/// <item>Lost, stolen and deactivated are final.</item>
/// </list>
/// Lock order: the vehicle (<see cref="Vehicles.LockAsync"/>), then its tags.
/// </summary>
public sealed partial class Tags(IDbSession session, TimeProvider clock, Vehicles vehicles, DataEncryptor encryptor, LookupHasher lookup,
    IAuditLog audit)
{
    public const string LookupNamespace = "rfid-uid";

    private static readonly Dictionary<TagStatus, HashSet<TagStatus>> Transitions = new()
    {
        [TagStatus.ACTIVE] = [TagStatus.LOST, TagStatus.STOLEN, TagStatus.DEACTIVATED, TagStatus.REPLACEMENT_PENDING],
        [TagStatus.REPLACEMENT_PENDING] = [TagStatus.ACTIVE, TagStatus.LOST, TagStatus.STOLEN, TagStatus.DEACTIVATED],
    };

    private DbTx Tx => session.Current;
    private ZawedDbContext Db => Tx.Db();

    /// <summary>
    /// Binds a new tag to an active vehicle. A tag waiting for replacement is closed in the same transaction; an active
    /// tag must be reported first, so a working tag is never silently retired.
    /// </summary>
    internal async Task<Result<TagResponse>> IssueAsync(ZawedPrincipal actor, Guid vehicleId, string rawUid, CancellationToken ct)
    {
        if (NormalizeUid(rawUid) is not { } uid)
        {
            return InvalidUid<TagResponse>();
        }
        if (await vehicles.LockAsync(vehicleId, ct) is not { } vehicle)
        {
            return Result<TagResponse>.NotFound("Vehicle");
        }
        if (vehicle.Status != VehicleStatus.ACTIVE)
        {
            return Result<TagResponse>.Conflict("VEHICLE_NOT_ACTIVE", "Tags are issued to active vehicles only");
        }
        var current = await Db.Set<RfidTag>()
            .Where(t => t.VehicleId == vehicleId && (t.Status == TagStatus.ACTIVE || t.Status == TagStatus.REPLACEMENT_PENDING))
            .Select(t => t.Status).ToListAsync(ct);
        if (current.Contains(TagStatus.ACTIVE))
        {
            return Result<TagResponse>.Conflict("VEHICLE_HAS_ACTIVE_TAG", "Report the current tag lost, stolen or for replacement first");
        }
        var uidLookup = lookup.Hash(LookupNamespace, uid);
        if (await Tx.QuerySingleAsync<bool>("select exists (select 1 from rfid_tags where uid_lookup = @lookup)", new { lookup = uidLookup }, ct))
        {
            return AlreadyBound<TagResponse>();
        }
        var now = clock.GetUtcNow();
        // The cooldown belongs to the vehicle's last fueling, not to the plastic: carry it over.
        var cooldown = await Tx.QuerySingleOrDefaultAsync<DateTime?>(
            "select max(cooldown_until) from rfid_tags where vehicle_id = @vehicle", new { vehicle = vehicleId }, ct);
        await Tx.ExecuteAsync("""
            update rfid_tags set status = 'DEACTIVATED', status_reason = 'Replaced', updated_at = @now,
                   updated_by = @actor
            where vehicle_id = @vehicle and status = 'REPLACEMENT_PENDING'
            """, new { vehicle = vehicleId, now = now.UtcDateTime, actor = actor.UserId }, ct);
        var id = Guid.CreateVersion7(now);
        var last4 = uid[^4..];
        try
        {
            await Tx.ExecuteAsync("""
                insert into rfid_tags (id, company_id, vehicle_id, uid_encrypted, uid_lookup, uid_last4, status,
                                       cooldown_until, created_at, created_by, updated_at, updated_by)
                values (@id, @company, @vehicle, @encrypted, @lookup, @last4, 'ACTIVE', @cooldown, @now, @actor,
                        @now, @actor)
                """, new
            {
                id, company = vehicle.CompanyId, vehicle = vehicleId,
                encrypted = encryptor.Encrypt(Encoding.ASCII.GetBytes(uid), AssociatedData(id)), lookup = uidLookup, last4,
                cooldown = cooldown is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : (DateTime?)null, now = now.UtcDateTime,
                actor = actor.UserId,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return AlreadyBound<TagResponse>(); // a concurrent bind of the same tag
        }
        await audit.RecordAsync(Tx, Vehicles.ActorOf(actor), "TAG_ISSUED", "RFID_TAG", id, Scope.Company(vehicle.CompanyId),
            new Dictionary<string, object?> { ["uidLast4"] = last4, ["replaced"] = current.Count }, ct);
        return Result<TagResponse>.Created(TagResponse.Of(await Db.Set<RfidTag>().SingleAsync(t => t.Id == id, ct)));
    }

    internal async Task<Result<TagResponse>> SetStatusAsync(ZawedPrincipal actor, Guid tagId, TagStatus status, string reason,
        CancellationToken ct)
    {
        if (await FindAsync(tagId, ct) is not { } found)
        {
            return Result<TagResponse>.NotFound("Tag");
        }
        if (await vehicles.LockAsync(found.VehicleId, ct) is not { } vehicle)
        {
            return Result<TagResponse>.NotFound("Vehicle");
        }
        var tag = (await FindAsync(tagId, ct))!; // read again under the vehicle lock
        if (tag.Status == status)
        {
            return TagResponse.Of(tag);
        }
        if (!Transitions.TryGetValue(tag.Status, out var allowed) || !allowed.Contains(status))
        {
            return Result<TagResponse>.Conflict("INVALID_TAG_TRANSITION", $"A {tag.Status} tag cannot become {status}");
        }
        if (status == TagStatus.ACTIVE && vehicle.Status != VehicleStatus.ACTIVE)
        {
            return Result<TagResponse>.Conflict("VEHICLE_NOT_ACTIVE", "The vehicle is not active");
        }
        try
        {
            await Tx.ExecuteAsync("""
                update rfid_tags set status = @status, status_reason = @reason, updated_at = @now, updated_by = @actor
                where id = @id
                """, new { id = tagId, status = status.ToString(), reason, now = clock.GetUtcNow().UtcDateTime, actor = actor.UserId }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return Result<TagResponse>.Conflict("VEHICLE_HAS_ACTIVE_TAG", "The vehicle already has an active tag");
        }
        await audit.RecordAsync(Tx, Vehicles.ActorOf(actor), "TAG_STATUS_CHANGED", "RFID_TAG", tagId, Scope.Company(vehicle.CompanyId),
            new Dictionary<string, object?> { ["from"] = tag.Status.ToString(), ["to"] = status.ToString(), ["reason"] = reason }, ct);
        return TagResponse.Of((await FindAsync(tagId, ct))!);
    }

    private Task<RfidTag?> FindAsync(Guid id, CancellationToken ct) => Db.Set<RfidTag>().SingleOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>
    /// Readers print UIDs with spaces, colons or dashes, in either case. Null when the result is not 8 to 32 hex
    /// characters (4-byte to 10-byte tags, ISO 14443 single, double and triple size, and EPC-96). Java's <c>\s</c> is
    /// ASCII whitespace only, so the separators are listed.
    /// </summary>
    public static string? NormalizeUid(string? raw)
    {
        var uid = Separators().Replace(raw ?? "", "").ToUpperInvariant();
        return Uid().IsMatch(uid) ? uid : null;
    }

    /// <summary>The associated data a tag UID is encrypted under: <c>rfid_tags.uid:</c> and the tag id (Java <c>Tags.associatedData</c>).</summary>
    public static byte[] AssociatedData(Guid tagId) => Encoding.ASCII.GetBytes("rfid_tags.uid:" + tagId.ToString("D"));

    private static Result<T> InvalidUid<T>() =>
        Result<T>.Fail(400, "INVALID_TAG_UID", "A tag UID is 8 to 32 hex characters", "uid");

    private static Result<T> AlreadyBound<T>() => Result<T>.Fail(409, "TAG_ALREADY_BOUND", "This tag has been bound before", "uid");

    [GeneratedRegex("[ \t\n\u000B\f\r:\\-]", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"\A[0-9A-F]{8,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Uid();
}
