using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Zawed.Api.Platform.Crypto;

/// <summary>
/// Records the deployment's data key state in <c>data_key_state</c> (V20, ADR 0027) at every start, right
/// after the migrations and on the owner's connection (the runtime role has no grant on that table). Port
/// of the Java <c>DataKeyState</c> Flyway callback:
/// <list type="bullet">
/// <item>the first start records an HMAC fingerprint of the lookup key; every later start must present the
/// same fingerprint, or it does not start (a wrong lookup key would silently break every tag lookup,
/// activation code, receipt link and device tag digest);</item>
/// <item>the current data key id is set to this instance's; the database accepts a re-encryption only into
/// that id.</item>
/// </list>
/// </summary>
public static class DataKeyState
{
    public const string FingerprintNamespace = "lookup-key-fingerprint";

    public const string Mismatch = "ZAWED_LOOKUP_KEY is not the key this database's lookup hashes were made with "
        + "(its fingerprint differs from the one recorded in data_key_state at the first start). Set it to the "
        + "original ZAWED_DATA_KEY (ADR 0027); starting with it would break every tag lookup, activation code, "
        + "receipt link and device tag digest";

    public static string Fingerprint(LookupHasher lookup) => lookup.Hash(FingerprintNamespace, "zawed");

    public static async Task RecordAsync(string ownerConnectionString, int currentKeyId, string fingerprint, ILogger logger,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // Two instances starting at once on an empty table: one insert wins, both then compare with it.
        await using (var insert = new NpgsqlCommand("""
            insert into data_key_state (singleton, current_key_id, lookup_key_fingerprint, fingerprint_recorded_at, updated_at)
            values (true, @id, @fingerprint, now(), now())
            on conflict (singleton) do nothing
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("id", currentKeyId);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            if (await insert.ExecuteNonQueryAsync(ct) == 1)
            {
                await tx.CommitAsync(ct);
                logger.LogInformation("Data key state: lookup key fingerprint recorded, current data key {KeyId}", currentKeyId);
                return;
            }
        }
        string recorded;
        int recordedKeyId;
        await using (var select = new NpgsqlCommand("select lookup_key_fingerprint, current_key_id from data_key_state for update", connection, tx))
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            recorded = reader.GetString(0);
            recordedKeyId = reader.GetInt32(1);
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(recorded), Encoding.ASCII.GetBytes(fingerprint)))
        {
            throw new InvalidOperationException(Mismatch);
        }
        if (recordedKeyId != currentKeyId)
        {
            await using var update = new NpgsqlCommand("update data_key_state set current_key_id = @id, updated_at = now()", connection, tx);
            update.Parameters.AddWithValue("id", currentKeyId);
            await update.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Data key state: current data key {KeyId} (was {Previous})", currentKeyId, recordedKeyId);
        }
        await tx.CommitAsync(ct);
    }
}
