using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Zawed.Api.Platform.Persistence.Migrations;

/// <summary>A versioned SQL migration, as Flyway names it: <c>V{version}__{description}.sql</c>.</summary>
public sealed record Migration(string Version, string Description, string Script, string Sql, int Checksum)
{
    /// <summary>Flyway's ordering: numeric parts compared one by one (<c>6 &lt; 6.1 &lt; 7</c>).</summary>
    public IReadOnlyList<long> VersionParts { get; } = Version.Split('.').Select(long.Parse).ToList();
}

/// <summary>One row of <c>flyway_schema_history</c>.</summary>
public sealed record AppliedMigration(int InstalledRank, string? Version, string Description, string Type, string Script,
    int? Checksum, bool Success);

/// <summary>
/// Applies the SQL migrations the way Flyway 12 does, on Flyway's own history table, so that the .NET API
/// continues a database the Java API migrated and the Java API could still read one the .NET API
/// migrated (ADR 0029, "Migrations"):
/// <list type="bullet">
/// <item><b>History</b>: <c>flyway_schema_history</c> with Flyway's exact DDL; one row per migration with
/// the same <c>version</c>, <c>description</c>, <c>type</c> (<c>SQL</c>), <c>script</c>, <c>installed_by</c>
/// (<c>current_user</c>) and <c>checksum</c>.</item>
/// <item><b>Checksum</b>: Flyway's CRC32: the file read line by line (line breaks removed, a leading BOM
/// dropped), each line's UTF-8 bytes fed to one CRC32, stored as a signed 32-bit integer. Verified against
/// the rows real Flyway 12.4 wrote for all 25 files (<c>MigrationRunnerTests</c>).</item>
/// <item><b>Validation</b>, before anything is applied, with Flyway's defaults: a failed row stops the
/// start (Flyway would need a repair); an applied migration whose file changed (checksum or description)
/// stops it; an applied version missing locally stops it unless it is newer than every local file (a
/// "future" migration from a newer build, ignored as Flyway's <c>*:future</c> does); a pending version
/// lower than the latest applied stops it (out of order is off; V18 is never used).</item>
/// <item><b>Apply</b>: each migration in its own transaction together with its history row, under a session
/// advisory lock so two instances starting at once migrate one after the other.</item>
/// </list>
/// It runs as the database owner (<see cref="DataSources.OwnerConnectionString"/>), never as <c>zawed_app</c>.
/// </summary>
public sealed partial class MigrationRunner(string ownerConnectionString, IReadOnlyList<Migration> migrations, ILogger logger)
{
    public const string HistoryTable = "flyway_schema_history";

    /// <summary>One key for "migrating ZAWED"; any instance of the .NET API waits on it.</summary>
    private const long LockKey = 0x5A415745_44000001; // "ZAWED" + 1

    [GeneratedRegex(@"^V(?<version>\d+(?:_\d+)*)__(?<description>.+)\.sql$")]
    private static partial Regex FileName();

    /// <summary>The migrations embedded in this assembly (<c>db/migration/*.sql</c>), in Flyway order.</summary>
    public static IReadOnlyList<Migration> Embedded(Assembly? assembly = null)
    {
        assembly ??= typeof(MigrationRunner).Assembly;
        var list = new List<Migration>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("migrations/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            list.Add(Parse(name["migrations/".Length..], memory.ToArray()));
        }
        return Ordered(list);
    }

    public static IReadOnlyList<Migration> Ordered(IEnumerable<Migration> migrations) =>
        migrations.OrderBy(m => m, Comparer<Migration>.Create(Compare)).ToList();

    public static Migration Parse(string script, byte[] content)
    {
        var match = FileName().Match(script);
        if (!match.Success)
        {
            throw new InvalidOperationException($"{script} is not named V<version>__<description>.sql");
        }
        var sql = new UTF8Encoding(false, true).GetString(content);
        if (sql.Length > 0 && sql[0] == '﻿')
        {
            sql = sql[1..];
        }
        return new Migration(match.Groups["version"].Value.Replace('_', '.'), match.Groups["description"].Value.Replace('_', ' '),
            script, sql, Checksum(sql));
    }

    /// <summary>Flyway's checksum of a SQL file (see the class comment).</summary>
    public static int Checksum(string sql)
    {
        // java.util.zip.CRC32 (IEEE 802.3, reflected, init and final xor 0xFFFFFFFF), fed line by line.
        var crc = 0xFFFFFFFFu;
        foreach (var line in Lines(sql))
        {
            foreach (var b in Encoding.UTF8.GetBytes(line))
            {
                crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
        }
        return unchecked((int)(crc ^ 0xFFFFFFFFu));
    }

    private static readonly uint[] Crc32Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c;
    }).ToArray();

    /// <summary>The lines as Java's <c>BufferedReader.readLine</c> returns them (\n, \r and \r\n end a line).</summary>
    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\n' or '\r')
            {
                yield return text[start..i];
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    public static int Compare(Migration a, Migration b) => CompareVersions(a.VersionParts, b.VersionParts);

    public static int CompareVersions(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        return 0;
    }

    private static IReadOnlyList<long> Parts(string version) => version.Split('.').Select(long.Parse).ToList();

    /// <summary>Validates the history and applies what is pending. Returns how many migrations were applied.</summary>
    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        await Execute(connection, null, $"select pg_advisory_lock({LockKey})", ct);
        try
        {
            await EnsureHistoryTable(connection, ct);
            var applied = await History(connection, ct);
            var pending = Plan(applied);
            var rank = applied.Count == 0 ? 0 : applied.Max(a => a.InstalledRank);
            foreach (var migration in pending)
            {
                rank++;
                await Apply(connection, migration, rank, ct);
            }
            var current = migrations.Count == 0 ? "none" : (pending.LastOrDefault() ?? Latest(applied))?.Version ?? "none";
            logger.LogInformation("Schema is at version {Version}; applied {Count} migration(s)", current, pending.Count);
            return pending.Count;
        }
        finally
        {
            await Execute(connection, null, $"select pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private Migration? Latest(IReadOnlyList<AppliedMigration> applied)
    {
        var versions = applied.Where(a => a is { Success: true, Version: not null }).Select(a => a.Version!).ToHashSet();
        return migrations.LastOrDefault(m => versions.Contains(m.Version));
    }

    /// <summary>
    /// Checks the history against the files (Flyway's validate) and returns the pending migrations in order.
    /// Throws <see cref="MigrationValidationException"/> on any mismatch.
    /// </summary>
    public IReadOnlyList<Migration> Plan(IReadOnlyList<AppliedMigration> applied)
    {
        var failed = applied.FirstOrDefault(a => !a.Success);
        if (failed is not null)
        {
            throw new MigrationValidationException(
                $"Migration {failed.Script} failed earlier (flyway_schema_history rank {failed.InstalledRank}). Repair the database by hand before starting.");
        }
        var local = migrations.ToDictionary(m => m.Version);
        var versioned = applied.Where(a => a.Version is not null && a.Type == "SQL").ToList();
        var highestLocal = migrations.Count == 0 ? [] : migrations[^1].VersionParts;
        foreach (var row in versioned)
        {
            if (!local.TryGetValue(row.Version!, out var file))
            {
                if (CompareVersions(Parts(row.Version!), highestLocal) > 0)
                {
                    logger.LogWarning("Applied migration {Version} ({Script}) is newer than this build; ignored as a future migration",
                        row.Version, row.Script);
                    continue;
                }
                throw new MigrationValidationException($"Applied migration {row.Version} ({row.Script}) is not among this build's migrations.");
            }
            if (row.Checksum != file.Checksum)
            {
                throw new MigrationValidationException(
                    $"Migration {file.Script} was changed after it was applied: checksum {file.Checksum} here, {row.Checksum} in the database.");
            }
            if (row.Description != file.Description)
            {
                throw new MigrationValidationException(
                    $"Migration {row.Version} has description '{file.Description}' here and '{row.Description}' in the database.");
            }
        }
        var appliedVersions = versioned.Select(a => a.Version!).ToHashSet();
        var latestApplied = versioned.Select(a => Parts(a.Version!)).Where(p => CompareVersions(p, highestLocal) <= 0)
            .DefaultIfEmpty([]).Max(Comparer<IReadOnlyList<long>>.Create(CompareVersions))!;
        var pending = migrations.Where(m => !appliedVersions.Contains(m.Version)).ToList();
        var outOfOrder = pending.FirstOrDefault(m => CompareVersions(m.VersionParts, latestApplied) < 0);
        if (outOfOrder is not null)
        {
            throw new MigrationValidationException(
                $"Migration {outOfOrder.Script} is older than the latest applied version and was never applied (out of order is not allowed).");
        }
        return pending;
    }

    private async Task Apply(NpgsqlConnection connection, Migration migration, int rank, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await Execute(connection, tx, migration.Sql, ct);
            await using var insert = new NpgsqlCommand(
                $"""
                insert into {HistoryTable} (installed_rank, version, description, type, script, checksum, installed_by, execution_time, success)
                values (@rank, @version, @description, 'SQL', @script, @checksum, current_user, @time, true)
                """, connection, tx);
            insert.Parameters.AddWithValue("rank", rank);
            insert.Parameters.AddWithValue("version", migration.Version);
            insert.Parameters.AddWithValue("description", migration.Description);
            insert.Parameters.AddWithValue("script", migration.Script);
            insert.Parameters.AddWithValue("checksum", migration.Checksum);
            insert.Parameters.AddWithValue("time", (int)watch.ElapsedMilliseconds);
            await insert.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Applied migration {Script} in {Ms} ms", migration.Script, watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new MigrationValidationException($"Migration {migration.Script} failed and was rolled back: {ex.Message}", ex);
        }
    }

    /// <summary>Flyway 12's DDL for its history table, so either tool can read the other's.</summary>
    private static async Task EnsureHistoryTable(NpgsqlConnection connection, CancellationToken ct)
    {
        var exists = await Scalar<bool>(connection, $"select to_regclass('public.{HistoryTable}') is not null", ct);
        if (exists)
        {
            return;
        }
        await Execute(connection, null, $"""
            CREATE TABLE public.{HistoryTable} (
                installed_rank integer NOT NULL,
                version character varying(50),
                description character varying(200) NOT NULL,
                type character varying(20) NOT NULL,
                script character varying(1000) NOT NULL,
                checksum integer,
                installed_by character varying(100) NOT NULL,
                installed_on timestamp without time zone DEFAULT now() NOT NULL,
                execution_time integer NOT NULL,
                success boolean NOT NULL
            );
            ALTER TABLE ONLY public.{HistoryTable} ADD CONSTRAINT {HistoryTable}_pk PRIMARY KEY (installed_rank);
            CREATE INDEX {HistoryTable}_s_idx ON public.{HistoryTable} USING btree (success);
            """, ct);
    }

    public static async Task<IReadOnlyList<AppliedMigration>> History(NpgsqlConnection connection, CancellationToken ct)
    {
        var rows = new List<AppliedMigration>();
        await using var command = new NpgsqlCommand(
            $"select installed_rank, version, description, type, script, checksum, success from {HistoryTable} order by installed_rank",
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AppliedMigration(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetBoolean(6)));
        }
        return rows;
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }
}

public sealed class MigrationValidationException(string message, Exception? inner = null) : Exception(message, inner);
