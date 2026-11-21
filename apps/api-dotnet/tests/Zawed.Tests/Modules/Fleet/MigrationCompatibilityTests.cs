using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zawed.Api.Platform.Persistence.Migrations;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Fleet;

/// <summary>
/// The V14 half of the Java <c>MigrationCompatibilityTests</c>, on the .NET migration runner: rows written before V14 are
/// given canonical plate keys, and a plate collision stops the migration instead of merging two vehicles. (The ledger's
/// V12 backfills and the V23 contact numbers are the other halves, with the waves that own those tables.)
/// </summary>
public sealed class MigrationCompatibilityTests
{
    private static readonly Guid Company = Guid.NewGuid();

    [Fact]
    public async Task LegacyPlateKeysAreBackfilledCanonical()
    {
        var database = await TestDatabase.NewDatabaseAsync("zawed_mig");
        await MigrateAsync(database, upTo: 13);
        var vehicle = Guid.NewGuid();
        await PlantAsync(database, (vehicle, "طبع1234"));

        await MigrateAsync(database, upTo: null);

        var owner = await Owner.OfAsync(database);
        Assert.Equal("1234طبع", await owner.QueryOneAsync<string>("select plate_key from vehicles where id = @vehicle", new { vehicle }));
    }

    [Fact]
    public async Task APlateCollisionStopsTheMigrationInsteadOfMergingTwoVehicles()
    {
        var database = await TestDatabase.NewDatabaseAsync("zawed_mig");
        await MigrateAsync(database, upTo: 13);
        // The same plate typed in two orders: two rows before V14, one plate after it.
        await PlantAsync(database, (Guid.NewGuid(), "طبع1234"), (Guid.NewGuid(), "1234طبع"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(database, upTo: null));
        Assert.Contains("would share the plate key", failure.ToString());
        var owner = await Owner.OfAsync(database);
        Assert.Equal(2L, await owner.QueryOneAsync<long>("select count(*) from vehicles"));
    }

    private static async Task MigrateAsync(string database, int? upTo)
    {
        var migrations = MigrationRunner.Embedded().Where(m => upTo is null || m.VersionParts[0] <= upTo).ToList();
        await new MigrationRunner(await TestDatabase.OwnerConnectionStringAsync(database), migrations, NullLogger.Instance).MigrateAsync();
    }

    /// <summary>Legacy rows as they were written before V14, without the application around them.</summary>
    private static async Task PlantAsync(string database, params (Guid Id, string PlateKey)[] vehicles)
    {
        await using var connection = new NpgsqlConnection(await TestDatabase.OwnerConnectionStringAsync(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "set session_replication_role = replica; "
            + $"insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes, low_balance_threshold_piastres, created_at, "
            + $"updated_at) values ('{Company}', 'Legacy Co', 'ش', 'L', 'ACTIVE', 180, 0, now(), now()) on conflict do nothing; "
            + string.Concat(vehicles.Select(v => "insert into vehicles (id, company_id, plate, plate_key, make, model, fuel_type, tank_capacity_ml, "
                + $"tier, status, created_at, updated_at) values ('{v.Id}', '{Company}', '{v.PlateKey}', '{v.PlateKey}', 'M', 'M', 'DIESEL', 50000, "
                + "'NORMAL', 'ACTIVE', now(), now()); "));
        await command.ExecuteNonQueryAsync();
    }
}
