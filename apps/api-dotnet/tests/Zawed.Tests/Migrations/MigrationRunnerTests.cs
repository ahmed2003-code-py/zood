using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zawed.Api.Platform.Persistence.Migrations;
using Zawed.Tests.Support;

namespace Zawed.Tests.Migrations;

/// <summary>
/// The migration runner continues Flyway's work exactly (ADR 0029): same checksums, same history rows, same
/// schema, and it picks up a database real Flyway migrated. The Flyway side of every comparison was
/// produced by real Flyway 12.4 (fixtures), not by this code.
/// </summary>
public sealed class MigrationRunnerTests
{
    private static readonly IReadOnlyList<Migration> Embedded = MigrationRunner.Embedded();

    private static IReadOnlyList<string[]> FlywayHistory() =>
        Fixtures.Text("flyway-12.4-history.tsv").Split('\n')
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .ToList();

    [Fact]
    public void EveryEmbeddedMigrationHasTheChecksumAndDescriptionFlywayRecorded()
    {
        var flyway = FlywayHistory();
        Assert.Equal(flyway.Count, Embedded.Count);
        for (var i = 0; i < flyway.Count; i++)
        {
            Assert.Equal(flyway[i][1], Embedded[i].Version);
            Assert.Equal(flyway[i][2], Embedded[i].Description);
            Assert.Equal(flyway[i][4], Embedded[i].Script);
            Assert.Equal(int.Parse(flyway[i][5], System.Globalization.CultureInfo.InvariantCulture), Embedded[i].Checksum);
        }
    }

    [Fact]
    public void TheEmbeddedFilesAreTheJavaApisFilesByteForByte()
    {
        var java = Path.Combine(Fixtures.RepositoryRoot(), "apps", "api", "src", "main", "resources", "db", "migration");
        if (!Directory.Exists(java))
        {
            return; // after the cutover the Java folder is gone and this folder is the only source
        }
        var dotnet = Path.Combine(Fixtures.RepositoryRoot(), "apps", "api-dotnet", "db", "migration");
        var javaFiles = Directory.GetFiles(java, "*.sql").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(javaFiles, Directory.GetFiles(dotnet, "*.sql").Select(Path.GetFileName).Order().ToList());
        foreach (var file in javaFiles)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(java, file!)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dotnet, file!))),
                $"{file} differs between apps/api and apps/api-dotnet: add a migration to both folders in the same commit");
        }
    }

    [Theory]
    [InlineData("a\nb\n", "a\r\nb\r\n")]
    [InlineData("a\nb", "a\rb")]
    [InlineData("﻿a\nb", "a\nb")]
    public void TheChecksumIgnoresLineEndingsAndAByteOrderMarkLikeFlyway(string one, string other)
    {
        var a = MigrationRunner.Parse("V1__x.sql", System.Text.Encoding.UTF8.GetBytes(one));
        var b = MigrationRunner.Parse("V1__x.sql", System.Text.Encoding.UTF8.GetBytes(other));
        Assert.Equal(a.Checksum, b.Checksum);
    }

    [Fact]
    public void VersionsAreOrderedNumericallyPartByPart()
    {
        var shuffled = Embedded.OrderBy(m => m.Script.GetHashCode(StringComparison.Ordinal)).ToList();
        Assert.Equal(Embedded.Select(m => m.Version), MigrationRunner.Ordered(shuffled).Select(m => m.Version));
        Assert.Equal(new[] { "5", "6", "6.1", "7", "7.1", "8", "8.1", "9", "10" }, Embedded.Skip(4).Take(9).Select(m => m.Version).ToArray());
    }

    [Fact]
    public async Task AFreshDatabaseGetsTheSameHistoryAndSchemaAsFlywayGave()
    {
        var database = await TestDatabase.NewDatabaseAsync("fresh");
        var owner = await TestDatabase.OwnerConnectionStringAsync(database);

        var applied = await new MigrationRunner(owner, Embedded, NullLogger.Instance).MigrateAsync();

        Assert.Equal(25, applied);
        var rows = await History(owner);
        var flyway = FlywayHistory();
        Assert.Equal(flyway.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(string.Join('|', flyway[i]), string.Join('|', rows[i].InstalledRank, rows[i].Version, rows[i].Description,
                rows[i].Type, rows[i].Script, rows[i].Checksum));
            Assert.True(rows[i].Success);
        }
        Assert.Equal(Normalise(Fixtures.Text("flyway-12.4-at-v23.schema.sql")), Normalise(await TestDatabase.SchemaDumpAsync(database)));
        Assert.Equal("postgres", await Scalar<string>(owner, "select distinct installed_by from flyway_schema_history"));
    }

    [Fact]
    public async Task ADatabaseRealFlywayMigratedToV16ContinuesFromItsHistory()
    {
        var database = await TestDatabase.NewDatabaseAsync("from_flyway");
        await TestDatabase.PsqlFileAsync(database, Fixtures.Path("flyway-12.4-at-v16.sql"));
        var owner = await TestDatabase.OwnerConnectionStringAsync(database);
        var before = await History(owner);
        Assert.Equal(19, before.Count);

        var applied = await new MigrationRunner(owner, Embedded, NullLogger.Instance).MigrateAsync();

        Assert.Equal(6, applied); // 17, 19, 20, 21, 22, 23: V18 is never used
        var after = await History(owner);
        Assert.Equal(before, after.Take(19)); // Flyway's own rows untouched
        Assert.Equal(new[] { "17", "19", "20", "21", "22", "23" }, after.Skip(19).Select(r => r.Version!).ToArray());
        Assert.Equal(Enumerable.Range(20, 6), after.Skip(19).Select(r => r.InstalledRank));
        Assert.Equal(Normalise(Fixtures.Text("flyway-12.4-at-v23.schema.sql")), Normalise(await TestDatabase.SchemaDumpAsync(database)));

        Assert.Equal(0, await new MigrationRunner(owner, Embedded, NullLogger.Instance).MigrateAsync());
    }

    [Fact]
    public async Task TwoInstancesStartingAtOnceApplyEachMigrationOnce()
    {
        var database = await TestDatabase.NewDatabaseAsync("race");
        var owner = await TestDatabase.OwnerConnectionStringAsync(database);

        var counts = await Task.WhenAll(
            new MigrationRunner(owner, Embedded, NullLogger.Instance).MigrateAsync(),
            new MigrationRunner(owner, Embedded, NullLogger.Instance).MigrateAsync());

        Assert.Equal(25, counts.Sum());
        Assert.Equal(25, (await History(owner)).Count);
    }

    [Fact]
    public async Task AChangedMigrationStopsTheStartBeforeAnythingIsApplied()
    {
        var database = await TestDatabase.NewDatabaseAsync("changed");
        var owner = await TestDatabase.OwnerConnectionStringAsync(database);
        await new MigrationRunner(owner, Embedded.Take(5).ToList(), NullLogger.Instance).MigrateAsync();
        var edited = Embedded.Select((m, i) => i == 2 ? m with { Checksum = m.Checksum + 1 } : m).ToList();

        var error = await Assert.ThrowsAsync<MigrationValidationException>(() => new MigrationRunner(owner, edited, NullLogger.Instance).MigrateAsync());

        Assert.Contains("V3__runtime_role_and_rls.sql was changed", error.Message);
        Assert.Equal(5, (await History(owner)).Count);
    }

    [Fact]
    public async Task AFailedRowStopsTheStart()
    {
        var database = await TestDatabase.NewDatabaseAsync("failed");
        var owner = await TestDatabase.OwnerConnectionStringAsync(database);
        await new MigrationRunner(owner, Embedded.Take(2).ToList(), NullLogger.Instance).MigrateAsync();
        await Execute(owner, "update flyway_schema_history set success = false where installed_rank = 2");

        var error = await Assert.ThrowsAsync<MigrationValidationException>(() => new MigrationRunner(owner, Embedded, NullLogger.Instance).MigrateAsync());

        Assert.Contains("failed earlier", error.Message);
    }

    [Fact]
    public async Task AFailingMigrationRollsBackWithItsHistoryRow()
    {
        var database = await TestDatabase.NewDatabaseAsync("broken");
        var owner = await TestDatabase.OwnerConnectionStringAsync(database);
        var broken = MigrationRunner.Parse("V1__broken.sql", "create table t (id int);\nselect no_such_function();\n"u8.ToArray());

        await Assert.ThrowsAsync<MigrationValidationException>(() => new MigrationRunner(owner, [broken], NullLogger.Instance).MigrateAsync());

        Assert.Empty(await History(owner));
        Assert.False(await Scalar<bool>(owner, "select to_regclass('public.t') is not null"));
    }

    [Fact]
    public void AFutureMigrationFromANewerBuildIsIgnoredAndAGapIsRefused()
    {
        var runner = new MigrationRunner("", Embedded, NullLogger.Instance);
        var history = Embedded.Select((m, i) => new AppliedMigration(i + 1, m.Version, m.Description, "SQL", m.Script, m.Checksum, true)).ToList();

        var future = history.Append(new AppliedMigration(26, "24", "from a newer build", "SQL", "V24__x.sql", 1, true)).ToList();
        Assert.Empty(runner.Plan(future));

        var gap = history.Where(h => h.Version != "6.1").ToList();
        var error = Assert.Throws<MigrationValidationException>(() => runner.Plan(gap));
        Assert.Contains("out of order", error.Message);

        var unknown = history.Append(new AppliedMigration(26, "18", "never shipped", "SQL", "V18__x.sql", 1, true)).ToList();
        Assert.Contains("not among this build's migrations", Assert.Throws<MigrationValidationException>(() => runner.Plan(unknown)).Message);
    }

    /// <summary>pg_dump output without what differs run to run (the restrict key) or by tool version comments.</summary>
    private static string Normalise(string dump) => string.Join('\n', dump.Split('\n')
        .Where(l => !l.StartsWith("\\restrict", StringComparison.Ordinal) && !l.StartsWith("\\unrestrict", StringComparison.Ordinal)
            && !l.StartsWith("-- Dumped", StringComparison.Ordinal))
        .Select(l => l.TrimEnd()));

    private static async Task<List<AppliedMigration>> History(string owner)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        return (await MigrationRunner.History(connection, CancellationToken.None)).ToList();
    }

    private static async Task Execute(string owner, string sql)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(string owner, string sql)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
