using Npgsql;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Tests.Support;

/// <summary>
/// Connections as the application's runtime role, outside the application (Java tests' <c>app(scope, user)</c> helper
/// and their second <c>HikariDataSource</c>): for asserting what <c>zawed_app</c> may do, and for a second "instance"
/// of the application with a pool of its own.
/// </summary>
public static class RuntimeConnections
{
    /// <summary>
    /// An open transaction as <c>zawed_app</c>, told what <c>DbTx</c> would tell it. The caller commits or lets it roll
    /// back; one refused statement aborts it, so use one per attempt.
    /// </summary>
    public static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> OpenAsync(Owner owner, string? scopeType,
        Guid? userId = null)
    {
        var connection = new NpgsqlConnection(owner.ConnectionString);
        await connection.OpenAsync();
        var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            set local role zawed_app;
            select set_config('app.scope_type', @t, true), set_config('app.scope_id', @s, true), set_config('app.user_id', @u, true);
            """, connection, tx);
        command.Parameters.AddWithValue("t", scopeType ?? "");
        command.Parameters.AddWithValue("s", scopeType is null or "PLATFORM" ? "" : Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("u", userId?.ToString() ?? "");
        await command.ExecuteNonQueryAsync();
        return (connection, tx);
    }

    /// <summary>Runs one statement as the runtime role and returns the PostgreSQL refusal, or null when it ran.</summary>
    public static async Task<PostgresException?> RefusalAsync(Owner owner, string? scopeType, string sql, Guid? userId = null)
    {
        var (connection, tx) = await OpenAsync(owner, scopeType, userId);
        await using (connection)
        await using (tx)
        {
            try
            {
                await using var command = new NpgsqlCommand(sql, connection, tx);
                await command.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex;
            }
        }
    }

    /// <summary>A runtime pool of its own, as a second instance of the application would have.</summary>
    public static async Task<NpgsqlDataSource> PoolAsync(string database = TestDatabase.SharedName) =>
        DataSources.Runtime(new DatabaseOptions
        {
            Host = await TestDatabase.HostAsync(),
            Port = await TestDatabase.PortAsync(),
            Name = database,
            User = TestDatabase.User,
            Password = TestDatabase.Password,
            MaxPoolSize = 6,
        });

    /// <summary>The application's transactions over <paramref name="pool"/>.</summary>
    public static ITransactions TransactionsOver(NpgsqlDataSource pool) => new Transactions(pool);
}
