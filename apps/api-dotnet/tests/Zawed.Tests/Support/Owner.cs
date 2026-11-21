using Dapper;
using Npgsql;

namespace Zawed.Tests.Support;

/// <summary>
/// Direct access to a test database as its owner, bypassing the application and row-level security
/// (Java <c>support.Owner</c>). For arranging data and inspecting results only; never for asserting what
/// the application is allowed to do (that goes through the API, or through <see cref="AsRuntimeRoleAsync"/>).
/// </summary>
public sealed class Owner(string connectionString)
{
    public static async Task<Owner> OfAsync(string database = TestDatabase.SharedName) =>
        new(await TestDatabase.OwnerConnectionStringAsync(database));

    public string ConnectionString { get; } = connectionString;

    public async Task<int> ExecuteAsync(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteAsync(sql, args);
    }

    public async Task<T?> QueryOneAsync<T>(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<T>(sql, args);
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return (await connection.QueryAsync<T>(sql, args)).ToList();
    }

    /// <summary>
    /// Runs <paramref name="work"/> as <c>zawed_app</c> in one transaction with the given scope, as the
    /// application would: for asserting what row-level security lets the runtime role see or do.
    /// </summary>
    public async Task<T> AsRuntimeRoleAsync<T>(string? scopeType, Guid? scopeId, Guid? userId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("set local role zawed_app", transaction: tx);
        await connection.ExecuteAsync(
            "select set_config('app.scope_type', @t, true), set_config('app.scope_id', @s, true), set_config('app.user_id', @u, true)",
            new { t = scopeType ?? "", s = scopeId?.ToString() ?? "", u = userId?.ToString() ?? "" }, tx);
        var result = await work(connection, tx);
        await tx.RollbackAsync();
        return result;
    }
}
