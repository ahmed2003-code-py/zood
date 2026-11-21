using Npgsql;
using Testcontainers.PostgreSql;

namespace Zawed.Tests.Support;

/// <summary>
/// One real PostgreSQL 18 for the whole test run (Testcontainers, image <c>postgres:18</c>), started on
/// first use. Testcontainers' reaper removes the container when the test process ends, so a run leaves no
/// container behind. The Java suite used embedded PostgreSQL binaries (ADR 0013); the .NET suite uses
/// Docker, which the developer machine and CI have (ADR 0029). Row locks, constraints and row-level
/// security therefore behave as in production.
/// <para>The shared database <see cref="SharedName"/> is migrated by the first application started on it
/// and used by every test that does not need its own; a test that must not share calls
/// <see cref="NewDatabaseAsync"/>.</para>
/// </summary>
public static class TestDatabase
{
    public const string User = "postgres";
    public const string Password = "postgres";
    public const string SharedName = "zawed";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? container;

    public static async Task<PostgreSqlContainer> ContainerAsync()
    {
        if (container is not null)
        {
            return container;
        }
        await Gate.WaitAsync();
        try
        {
            if (container is null)
            {
                var started = new PostgreSqlBuilder("postgres:18")
                    .WithUsername(User)
                    .WithPassword(Password)
                    .WithDatabase(SharedName)
                    .WithCommand("-c", "max_connections=300")
                    .Build();
                await started.StartAsync();
                container = started;
            }
            return container;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<string> HostAsync() => (await ContainerAsync()).Hostname;

    public static async Task<int> PortAsync() => (await ContainerAsync()).GetMappedPublicPort(5432);

    /// <summary>A connection string as the owner (superuser): arranging data and inspecting results only.</summary>
    public static async Task<string> OwnerConnectionStringAsync(string database = SharedName) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = await HostAsync(),
            Port = await PortAsync(),
            Database = database,
            Username = User,
            Password = Password,
            Pooling = false,
        }.ConnectionString;

    /// <summary>A new, empty database on the same server, for a test that must not share the main one.</summary>
    public static async Task<string> NewDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 60)].ToLowerInvariant();
        await using var connection = new NpgsqlConnection(await OwnerConnectionStringAsync("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"create database {name}", connection);
        await command.ExecuteNonQueryAsync();
        return name;
    }

    /// <summary>Runs a SQL file inside the container with <c>psql</c> (for pg_dump fixtures, which use meta-commands).</summary>
    public static async Task PsqlFileAsync(string database, string hostPath)
    {
        var db = await ContainerAsync();
        var target = "/tmp/" + Path.GetFileName(hostPath);
        await db.CopyAsync(await File.ReadAllBytesAsync(hostPath), target);
        var result = await db.ExecAsync(["psql", "-v", "ON_ERROR_STOP=1", "-q", "-U", User, "-d", database, "-f", target]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"psql failed ({result.ExitCode}): {result.Stderr}");
        }
    }

    /// <summary><c>pg_dump --schema-only</c> of a database, from inside the container (same version as the server).</summary>
    public static async Task<string> SchemaDumpAsync(string database)
    {
        var db = await ContainerAsync();
        var result = await db.ExecAsync(["pg_dump", "-U", User, "-d", database, "--schema-only"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"pg_dump failed ({result.ExitCode}): {result.Stderr}");
        }
        return result.Stdout;
    }
}
