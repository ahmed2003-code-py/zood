using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.RateLimit;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Network;

/// <summary>A device's answer to "who are you" on each request.</summary>
public sealed record DeviceRef(Guid DeviceId, string Code, Guid StationId, Guid OperatorId);

/// <summary>
/// The device channel (ADR 0010; Java <c>network.DeviceChannel</c>): a POS redeems its one-time activation code and
/// registers its own public key; from then on it gets short-lived device tokens by signing a timestamp and a nonce with the
/// private key, which never leaves the device. No shared secret ships in the app.
/// <para>A device has no user and so no scope. Every database step here runs in a transaction of its own under an explicit
/// platform scope (<see cref="Platform"/>), entered only for the device's own row and only after, or exactly while, the
/// step's proof (the activation code, the signature) is what is being checked. This is the one escalation the device
/// channel uses; it is named in the B4 contract and in <c>ArchitectureTests</c>. Refusals are thrown, as in Java: a wrong
/// activation code's attempt is counted in a transaction that commits before the 401 is thrown.</para>
/// </summary>
public sealed class DeviceChannel(ITransactions transactions, LookupHasher lookup, DeviceTokens tokens, IAuditLog audit, IRateLimiter limiter,
    TimeProvider clock)
{
    public const int MaxActivationAttempts = 5;

    /// <summary>How far a token request's timestamp may be from server time.</summary>
    public static readonly TimeSpan ClockWindow = TimeSpan.FromMinutes(5);

    public const string KeyAlgorithm = "EC_P256";
    private const int ActivationsPerMinute = 10;
    private const int TokensPerMinute = 20;

    /// <summary>A device that may work: ACTIVE, and its operator not suspended (B4 review 1).</summary>
    private const string ActiveDevice = """
        select {0} from pos_devices d join station_operators o on o.id = d.operator_id
        where d.id = @id and d.status = 'ACTIVE' and o.status = 'ACTIVE'
        """;

    /// <summary>The order of the P-256 group: a key on any other curve is refused.</summary>
    private static readonly BigInteger P256Order = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);

    private const string P256Oid = "1.2.840.10045.3.1.7";

    private sealed class ActivationRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public Guid Station_Id { get; set; }
        public Guid Operator_Id { get; set; }
        public string Status { get; set; } = "";
        public string? Activation_Code_Hash { get; set; }
        public DateTime? Activation_Expires_At { get; set; }
        public int Activation_Attempts { get; set; }
    }

    private sealed class DeviceRefRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public Guid Station_Id { get; set; }
        public Guid Operator_Id { get; set; }
    }

    // -------------------------------------------------------------------------------------------------- activate

    /// <summary>
    /// Redeems an activation code: the device becomes ACTIVE with the given public key, and the code can never be used
    /// again. A wrong code counts an attempt; after <see cref="MaxActivationAttempts"/> the code is dead and Ops must issue a
    /// new one. An unknown device code, a wrong, expired or used activation code and a revoked device all answer the same,
    /// so the endpoint reveals nothing.
    /// </summary>
    public async Task<DeviceRef> ActivateAsync(string? deviceCode, string? activationCode, string? publicKeyBase64, string? clientAddress,
        CancellationToken ct)
    {
        limiter.Acquire("pos-activate:" + SecureTokens.Sha256Hex(clientAddress ?? "null"), ActivationsPerMinute, TimeSpan.FromMinutes(1));
        var publicKey = ParseP256(publicKeyBase64);
        var code = deviceCode is null ? "" : Rows.JavaStrip(deviceCode).ToUpperInvariant();
        var hash = lookup.Hash(Devices.ActivationNamespace, activationCode is null ? "" : Rows.JavaStrip(activationCode).ToUpperInvariant());
        var now = clock.GetUtcNow();
        var activated = await transactions.RunAsync(Platform(), async tx =>
        {
            var device = await tx.QuerySingleOrDefaultAsync<ActivationRow>("""
                select id, code, station_id, operator_id, status, activation_code_hash, activation_expires_at,
                       activation_attempts
                from pos_devices where code = @code for update
                """, new { code }, ct);
            if (device is null)
            {
                return null;
            }
            var usable = device.Status == "PENDING_ACTIVATION" && device.Activation_Code_Hash is not null
                && device.Activation_Expires_At is { } expires && Rows.Utc(expires) > now
                && device.Activation_Attempts < MaxActivationAttempts;
            var matches = usable && SecureTokens.ConstantTimeEquals(hash, device.Activation_Code_Hash);
            if (!matches)
            {
                if (usable)
                {
                    await tx.ExecuteAsync("update pos_devices set activation_attempts = activation_attempts + 1 where id = @id",
                        new { id = device.Id }, ct);
                }
                return null;
            }
            await tx.ExecuteAsync("""
                update pos_devices set status = 'ACTIVE', public_key = @key, key_algorithm = @alg, bound_at = @now,
                       activation_code_hash = null, activation_expires_at = null, updated_at = @now
                where id = @id
                """, new { id = device.Id, key = publicKey, alg = KeyAlgorithm, now }, ct);
            await audit.RecordAsync(tx, AuditActor.Anonymous, "DEVICE_ACTIVATED", "POS_DEVICE", device.Id, Scope.Operator(device.Operator_Id),
                new Dictionary<string, object?> { ["code"] = code, ["keyAlgorithm"] = KeyAlgorithm }, ct);
            return new DeviceRef(device.Id, code, device.Station_Id, device.Operator_Id);
        }, ct: ct);
        return activated ?? throw new ApiException(401, "ACTIVATION_FAILED", "The device code or activation code is not valid");
    }

    // ----------------------------------------------------------------------------------------------------- token

    /// <summary>The message a device signs to get a token. Fixed layout, one field per line.</summary>
    public static string TokenMessage(Guid deviceId, long timestampSeconds, string nonce) =>
        "zawed-device-token\n" + deviceId + "\n" + timestampSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + nonce;

    /// <summary>
    /// A device token for a device that proves it holds its private key: an ECDSA (P-256, SHA-256, DER) signature over
    /// <see cref="TokenMessage"/>, a timestamp within <see cref="ClockWindow"/> of server time, and a nonce never seen
    /// before for this device.
    /// </summary>
    public async Task<IssuedDeviceToken> TokenAsync(Guid deviceId, long timestampSeconds, string? nonce, string? signatureBase64,
        string? clientAddress, CancellationToken ct)
    {
        // Before the proof, by address: a device id is not a secret, so a limit keyed on it alone would let anyone starve
        // that device of tokens (B4 review 5). Per device after the proof.
        limiter.Acquire("pos-token-ip:" + SecureTokens.Sha256Hex(clientAddress ?? "null"), TokensPerMinute * 3, TimeSpan.FromMinutes(1));
        var now = clock.GetUtcNow();
        var serverTime = now.ToUnixTimeSeconds();
        if (Int128.Abs((Int128)serverTime - timestampSeconds) > (Int128)ClockWindow.TotalSeconds)
        {
            throw new ApiException(401, "DEVICE_CLOCK_SKEW", "The device clock is too far from server time", null,
                new Dictionary<string, object> { ["serverTime"] = serverTime });
        }
        if (nonce is null || nonce.Length < 16 || nonce.Length > 128)
        {
            throw Refused();
        }
        var ok = await transactions.RunAsync(Platform(), async tx =>
        {
            var key = await tx.QuerySingleOrDefaultAsync<byte[]>(string.Format(System.Globalization.CultureInfo.InvariantCulture, ActiveDevice, "d.public_key"),
                new { id = deviceId }, ct);
            if (key is null || !Verify(key, TokenMessage(deviceId, timestampSeconds, nonce), signatureBase64))
            {
                return false;
            }
            try
            {
                await tx.ExecuteAsync("insert into device_nonces (device_id, nonce, created_at) values (@device, @nonce, @now)",
                    new { device = deviceId, nonce, now }, ct);
            }
            catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
            {
                throw new ApiException(401, "DEVICE_REPLAY", "This signed request was already used");
            }
            return true;
        }, ct: ct);
        if (!ok)
        {
            throw Refused();
        }
        limiter.Acquire("pos-token:" + deviceId, TokensPerMinute, TimeSpan.FromMinutes(1));
        return tokens.Issue(deviceId);
    }

    // ---------------------------------------------------------------------------------------------------- verify

    /// <summary>The device a token names, if it is still ACTIVE. Read on every request, so revocation is immediate.</summary>
    public async Task<DeviceRef?> VerifyAsync(string deviceToken, CancellationToken ct)
    {
        if (await tokens.DeviceOfAsync(deviceToken) is not { } deviceId)
        {
            return null;
        }
        var row = await transactions.RunAsync(Platform(), tx => tx.QuerySingleOrDefaultAsync<DeviceRefRow>(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, ActiveDevice, "d.id, d.code, d.station_id, d.operator_id"),
            new { id = deviceId }, ct), ct: ct);
        return row is null ? null : new DeviceRef(row.Id, row.Code, row.Station_Id, row.Operator_Id);
    }

    /// <summary>Records the device's last contact, at most once a minute (the sync monitor's "last seen").</summary>
    public Task TouchAsync(Guid deviceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return transactions.RunAsync(Platform(), tx => tx.ExecuteAsync("""
            update pos_devices set last_sync_at = @now
            where id = @id and (last_sync_at is null or last_sync_at < @stale)
            """, new { id = deviceId, now, stale = now - TimeSpan.FromMinutes(1) }, ct), ct: ct);
    }

    // --------------------------------------------------------------------------------------------------- helpers

    /// <summary>The platform scope of the device channel: entered only here, for the device's own row, after its proof.</summary>
    private static DbScopeContext Platform() => DbScopeContext.Escalate(Scope.Platform, null);

    /// <summary>
    /// A P-256 public key as X.509 SubjectPublicKeyInfo, base64, re-encoded as stored. Anything else is refused, as Java's
    /// <c>KeyFactory("EC")</c> refused it: another algorithm, another curve, a compressed point, trailing bytes.
    /// </summary>
    public static byte[] ParseP256(string? publicKeyBase64)
    {
        try
        {
            var encoded = JavaBase64(publicKeyBase64 is null ? "" : Rows.JavaStrip(publicKeyBase64)) ?? throw InvalidKey();
            if (!UncompressedPoint(encoded))
            {
                throw InvalidKey();
            }
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(encoded, out var read);
            if (read != encoded.Length)
            {
                throw InvalidKey();
            }
            var curve = ec.ExportParameters(false).Curve;
            var p256 = curve.IsNamed ? curve.Oid.Value == P256Oid
                : curve.Order is { } order && new BigInteger(order, isUnsigned: true, isBigEndian: true) == P256Order;
            if (!p256)
            {
                throw InvalidKey();
            }
            return ec.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            throw InvalidKey();
        }
    }

    /// <summary>The subject public key is an uncompressed point (<c>04 x y</c>), the only form Java's EC key factory reads.</summary>
    private static bool UncompressedPoint(byte[] spki)
    {
        try
        {
            var outer = new AsnReader(spki, AsnEncodingRules.DER).ReadSequence();
            outer.ReadSequence();
            var point = outer.ReadBitString(out _);
            return point.Length > 0 && point[0] == 0x04;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool Verify(byte[] publicKey, string message, string? signatureBase64)
    {
        try
        {
            var signature = JavaBase64(signatureBase64 is null ? "" : Rows.JavaStrip(signatureBase64));
            if (signature is null)
            {
                return false;
            }
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(message), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Java's basic <c>Base64.getDecoder()</c>: the standard alphabet only (no white space), padding optional but correct
    /// when present. Null when Java would throw.
    /// </summary>
    internal static byte[]? JavaBase64(string text)
    {
        var padding = text.Length - text.TrimEnd('=').Length;
        var body = text[..(text.Length - padding)];
        if (body.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/')) || padding > 2 || body.Length % 4 == 1
            || (padding > 0 && (body.Length + padding) % 4 != 0))
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(body + new string('=', (4 - body.Length % 4) % 4));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ApiException InvalidKey() => new(400, "INVALID_DEVICE_KEY",
        "The public key must be an EC P-256 key in X.509 SubjectPublicKeyInfo form, base64", "publicKey");

    private static ApiException Refused() => new(401, "DEVICE_UNAUTHENTICATED", "The device could not be authenticated");
}
