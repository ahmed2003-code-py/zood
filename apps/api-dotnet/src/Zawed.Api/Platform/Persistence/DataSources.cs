using Npgsql;
using Zawed.Api.Platform.Config;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// The two ways the API reaches PostgreSQL, as the Java API does (application.yaml, V3, V5):
/// <list type="bullet">
/// <item><b>Runtime</b>: a pool whose every physical connection switches to <c>zawed_app</c>
/// (<c>SET ROLE</c>, Hikari's <c>connection-init-sql</c>), a NOLOGIN role that owns nothing, holds only
/// its grants and is subject to row-level security. <c>No Reset On Close</c> keeps the role: Npgsql's
/// default reset (<c>DISCARD ALL</c>) would return a pooled connection to the owner.
/// <see cref="DbTx"/> checks <c>current_user</c> at every transaction start as the second line.</item>
/// <item><b>Owner</b>: the same credentials without the role switch, un-pooled, for the migrations and the
/// data-key state only (the Flyway connection).</item>
/// </list>
/// </summary>
public static class DataSources
{
    public static string OwnerConnectionString(DatabaseOptions db) => new NpgsqlConnectionStringBuilder
    {
        Host = db.Host,
        Port = db.Port,
        Database = db.Name,
        Username = db.User,
        Password = db.Password,
        Pooling = false,
        ApplicationName = "zawed-api-migrations",
    }.ConnectionString;

    public static NpgsqlDataSource Runtime(DatabaseOptions db)
    {
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder
        {
            Host = db.Host,
            Port = db.Port,
            Database = db.Name,
            Username = db.User,
            Password = db.Password,
            MaxPoolSize = db.MaxPoolSize,
            NoResetOnClose = true,
            ApplicationName = "zawed-api",
        }.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using var command = new NpgsqlCommand($"SET ROLE {DbTx.RuntimeRole}", connection);
                command.ExecuteNonQuery();
            },
            async connection =>
            {
                await using var command = new NpgsqlCommand($"SET ROLE {DbTx.RuntimeRole}", connection);
                await command.ExecuteNonQueryAsync();
            });
        return builder.Build();
    }
}
