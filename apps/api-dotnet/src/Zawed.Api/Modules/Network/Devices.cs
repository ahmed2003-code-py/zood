using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>A device with the activation code that was just generated for it. The code is not stored.</summary>
public sealed record RegisteredDevice(DeviceResponse Device, string ActivationCode);

/// <summary>
/// POS devices (PRD §6.3 Register POS Device, §7.1; Java <c>network.Devices</c>). A device is registered to one station
/// and starts PENDING_ACTIVATION with a one-time activation code that Ops reads to the installer. The device redeems it
/// (<see cref="DeviceChannel"/>) and registers its own key pair.
/// <para>The activation code is 12 characters from a 31-letter alphabet (about 59 bits), valid for 15 minutes and five
/// attempts. With that much entropy a keyed hash is enough to store it (an offline guess against the hash is impossible
/// without the server key) and it lets the device be found by its code. It is shown once and can only be replaced.</para>
/// </summary>
public sealed class Devices(IDbSession session, Operators operators, LookupHasher lookup, IAuditLog audit, TimeProvider clock)
{
    public const string ActivationNamespace = "pos-activation";
    public static readonly TimeSpan ActivationTtl = TimeSpan.FromMinutes(15);
    public const int ActivationCodeLength = 12;
    private const int DeviceCodeLength = 8;
    private const int CodeAttempts = 5;

    private DbTx Tx => session.Current;

    public async Task<Result<Page<DeviceResponse>>> ListAsync(Guid? operatorId, Guid? stationId, DeviceStatus? status, string? cursor,
        int? limit, CancellationToken ct)
    {
        var size = Page<DeviceResponse>.Limit(limit);
        var now = clock.GetUtcNow();
        var rows = (await Tx.QueryAsync<DeviceRow>("""
            select * from pos_devices
            where (cast(@operator as uuid) is null or operator_id = @operator)
              and (cast(@station as uuid) is null or station_id = @station)
              and (cast(@status as text) is null or status = @status)
              and (cast(@cursor as uuid) is null or id < @cursor)
            order by id desc limit @limit
            """, new { @operator = operatorId, station = stationId, status = status?.ToString(), cursor = Cursors.Parse(cursor), limit = size + 1 }, ct))
            .Select(d => d.ToResponse(now)).ToList();
        return Page<DeviceResponse>.Of(rows, size, d => d.Id);
    }

    public async Task<Result<DeviceResponse>> GetAsync(Guid id, CancellationToken ct) =>
        await ReadAsync(id, ct) is { } row ? row.ToResponse(clock.GetUtcNow()) : Result<DeviceResponse>.NotFound("Device");

    public async Task<Result<RegisteredDevice>> RegisterAsync(ZawedPrincipal actor, Guid stationId, string? label, CancellationToken ct)
    {
        if (await operators.LockStationAsync(stationId, ct) is not { } station)
        {
            return Result<RegisteredDevice>.NotFound("Station");
        }
        if (station.StationStatus == StationStatus.CLOSED)
        {
            return Result<RegisteredDevice>.Conflict("STATION_CLOSED", "The station is closed");
        }
        var now = clock.GetUtcNow();
        var activationCode = SecureTokens.Code(ActivationCodeLength);
        var id = Guid.CreateVersion7(now);
        string? code = null;
        for (var attempt = 0; attempt < CodeAttempts && code is null; attempt++)
        {
            var candidate = "POS-" + SecureTokens.Code(DeviceCodeLength);
            if (!await Tx.QuerySingleAsync<bool>("select exists (select 1 from pos_devices where code = @code)", new { code = candidate }, ct))
            {
                code = candidate;
            }
        }
        if (code is null)
        {
            throw new InvalidOperationException("Could not generate a unique device code");
        }
        try
        {
            await Tx.ExecuteAsync("""
                insert into pos_devices (id, operator_id, station_id, code, label, status, activation_code_hash,
                                         activation_expires_at, created_at, created_by, updated_at, updated_by)
                values (@id, @operator, @station, @code, @label, 'PENDING_ACTIVATION', @hash, @expires, @now,
                        @actor, @now, @actor)
                """, new
            {
                id, @operator = station.Operator_Id, station = stationId, code, label = Sql.Clean(label),
                hash = lookup.Hash(ActivationNamespace, activationCode), expires = now + ActivationTtl, now, actor = actor.UserId,
            }, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return Result<RegisteredDevice>.Conflict("CONCURRENT_CHANGE", "Try again");
        }
        await audit.RecordAsync(Tx, Rows.Actor(actor), "DEVICE_REGISTERED", "POS_DEVICE", id, Scope.Operator(station.Operator_Id),
            new Dictionary<string, object?> { ["code"] = code, ["stationId"] = stationId.ToString() }, ct);
        return Result<RegisteredDevice>.Created(new RegisteredDevice((await ReadAsync(id, ct))!.ToResponse(clock.GetUtcNow()), activationCode));
    }

    /// <summary>A fresh activation code for a device that has not been activated yet; the old one stops working.</summary>
    public async Task<Result<RegisteredDevice>> ReissueActivationAsync(ZawedPrincipal actor, Guid id, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } device)
        {
            return Result<RegisteredDevice>.NotFound("Device");
        }
        if (device.DeviceStatus != DeviceStatus.PENDING_ACTIVATION)
        {
            return Result<RegisteredDevice>.Conflict("DEVICE_NOT_PENDING",
                "Only a device waiting for activation gets a new code; revoke and register a new device instead");
        }
        var now = clock.GetUtcNow();
        var activationCode = SecureTokens.Code(ActivationCodeLength);
        await Tx.ExecuteAsync("""
            update pos_devices set activation_code_hash = @hash, activation_expires_at = @expires,
                   activation_attempts = 0, updated_at = @now, updated_by = @actor
            where id = @id
            """, new { id, hash = lookup.Hash(ActivationNamespace, activationCode), expires = now + ActivationTtl, now, actor = actor.UserId }, ct);
        await audit.RecordAsync(Tx, Rows.Actor(actor), "DEVICE_ACTIVATION_REISSUED", "POS_DEVICE", id, Scope.Operator(device.Operator_Id),
            new Dictionary<string, object?>(), ct);
        return new RegisteredDevice((await ReadAsync(id, ct))!.ToResponse(clock.GetUtcNow()), activationCode);
    }

    /// <summary>Final: a revoked device can never sync or fuel again (PRD §7.1 lost or stolen device).</summary>
    public async Task<Result<DeviceResponse>> RevokeAsync(ZawedPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        if (await LockAsync(id, ct) is not { } device)
        {
            return Result<DeviceResponse>.NotFound("Device");
        }
        if (device.DeviceStatus == DeviceStatus.REVOKED)
        {
            return device.ToResponse(clock.GetUtcNow());
        }
        var now = clock.GetUtcNow();
        await Tx.ExecuteAsync("""
            update pos_devices set status = 'REVOKED', activation_code_hash = null, activation_expires_at = null,
                   revoked_at = @now, revoked_reason = @reason, updated_at = @now, updated_by = @actor
            where id = @id
            """, new { id, reason, now, actor = actor.UserId }, ct);
        await audit.RecordAsync(Tx, Rows.Actor(actor), "DEVICE_REVOKED", "POS_DEVICE", id, Scope.Operator(device.Operator_Id),
            new Dictionary<string, object?> { ["from"] = device.Status, ["reason"] = reason }, ct);
        return (await ReadAsync(id, ct))!.ToResponse(clock.GetUtcNow());
    }

    private Task<DeviceRow?> ReadAsync(Guid id, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<DeviceRow>("select * from pos_devices where id = @id", new { id }, ct);

    private Task<DeviceRow?> LockAsync(Guid id, CancellationToken ct) =>
        Tx.QuerySingleOrDefaultAsync<DeviceRow>("select * from pos_devices where id = @id for update", new { id }, ct);
}
