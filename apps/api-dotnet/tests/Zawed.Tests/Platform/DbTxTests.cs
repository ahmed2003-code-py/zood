using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zawed.Api.Modules.Tenancy;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The transaction's own guarantees (wave 5B hardening): a commit never reports success for a transaction PostgreSQL
/// rolled back, a savepoint never leaves EF Core's tracker ahead of or behind the database, and a failure of the savepoint
/// machinery is told apart from a failure of the work inside it.
/// </summary>
public sealed class DbTxTests(SharedApi api) : IClassFixture<SharedApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    private Owner Owner => api.Factory.Owner;

    private Task<DbTx> BeginAsync() =>
        api.Factory.Services.GetRequiredService<ITransactions>().BeginAsync(DbScopeContext.Escalate(Scope.Platform, null));

    private Task<string?> NameOfAsync(Guid company) =>
        Owner.QueryOneAsync<string>("select name_en from companies where id = @company", new { company });

    // --------------------------------------------------------------------------------------------------- commit

    /// <summary>
    /// Why <see cref="DbTx.CommitAsync"/> probes: PostgreSQL answers COMMIT in an aborted transaction with ROLLBACK and
    /// no error, and Npgsql reports it as a success. If Npgsql ever throws here, the probe can go.
    /// </summary>
    [Fact]
    public async Task NpgsqlReportsSuccessForACommitPostgresTurnedIntoARollback()
    {
        var company = await id.SeedCompanyAsync();
        await using var connection = new NpgsqlConnection(Owner.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("update companies set name_en = 'Lost' where id = @company", new { company }, tx);
        await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("select 1/0", transaction: tx));

        await tx.CommitAsync(); // no exception

        Assert.Equal("Test Company", await NameOfAsync(company));
    }

    [Fact]
    public async Task ACommitOfAnAbortedTransactionThrowsAndWritesNothing()
    {
        var company = await id.SeedCompanyAsync();
        var ran = false;
        await using (var tx = await BeginAsync())
        {
            await tx.ExecuteAsync("update companies set name_en = 'Lost' where id = @company", new { company });
            tx.AfterCommit(() =>
            {
                ran = true;
                return Task.CompletedTask;
            });
            // A caller that caught a failed statement and went on, as if it had not aborted the transaction.
            await Assert.ThrowsAsync<PostgresException>(() => tx.ExecuteAsync("select 1/0"));

            var refused = await Assert.ThrowsAsync<TransactionAbortedException>(() => tx.CommitAsync());
            Assert.Equal(PostgresErrorCodes.InFailedSqlTransaction, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
            Assert.False(tx.IsFinished);
            await tx.RollbackAsync(); // the transaction was still open for the caller's rollback
        }
        Assert.False(ran);
        Assert.Equal("Test Company", await NameOfAsync(company));
    }

    [Fact]
    public async Task ACommitWritesRunsItsAfterCommitWorkAndFreesTheConnection()
    {
        var company = await id.SeedCompanyAsync();
        var ran = false;
        await using (var tx = await BeginAsync())
        {
            await tx.ExecuteAsync("update companies set name_en = 'Kept' where id = @company", new { company });
            tx.AfterCommit(() =>
            {
                ran = true;
                return Task.CompletedTask;
            });
            await tx.CommitAsync();
            Assert.True(tx.IsFinished);
            // The connection left the transaction: a new one can begin on it.
            await using var next = await tx.Connection.BeginTransactionAsync();
            Assert.Equal(1, await tx.Connection.ExecuteScalarAsync<int>("select 1", transaction: next));
        }
        Assert.True(ran);
        Assert.Equal("Kept", await NameOfAsync(company));
    }

    [Fact]
    public async Task ACommitSavesWhatEfCoreStillTracks()
    {
        var company = await id.SeedCompanyAsync();
        await using (var tx = await BeginAsync())
        {
            (await TrackAsync(tx, company)).NameEn = "Saved at commit";
            await tx.CommitAsync();
        }
        Assert.Equal("Saved at commit", await NameOfAsync(company));
    }

    // ------------------------------------------------------------------------------------------------- savepoints

    [Fact]
    public async Task ASavepointRefusesUnsavedEfChanges()
    {
        var company = await id.SeedCompanyAsync();
        await using var tx = await BeginAsync();
        (await TrackAsync(tx, company)).NameEn = "Pending";
        var ran = false;

        await Assert.ThrowsAsync<SavepointFailedException>(() => tx.SavepointAsync("probe", () =>
        {
            ran = true;
            return Task.FromResult(true);
        }));

        Assert.False(ran);
        Assert.Equal(EntityState.Modified, tx.Db().Entry(await TrackAsync(tx, company)).State); // left as the cause had it
    }

    [Fact]
    public async Task ARollbackToTheSavepointKeepsWhatTheCauseTrackedAndForgetsWhatTheWorkAdded()
    {
        var company = await id.SeedCompanyAsync();
        var added = Guid.CreateVersion7();
        await using (var tx = await BeginAsync())
        {
            var mine = await TrackAsync(tx, company);

            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.SavepointAsync<bool>("probe", async () =>
            {
                mine.NameEn = "Inside";
                tx.Db().Add(NewCompany(added));
                await tx.Db().SaveChangesAsync();
                throw new InvalidOperationException("the work fails after saving");
            }));

            var entry = tx.Db().Entry(mine);
            Assert.Equal(EntityState.Unchanged, entry.State);
            Assert.Equal("Test Company", mine.NameEn); // back to what the database holds again
            Assert.Single(tx.Db().ChangeTracker.Entries()); // the work's company is forgotten

            mine.NameEn = "After"; // the cause goes on with the entity it holds, and its save still writes
            await tx.Db().SaveChangesAsync();
            await tx.CommitAsync();
        }
        Assert.Equal("After", await NameOfAsync(company));
        Assert.Equal(0, await Owner.QueryOneAsync<long>("select count(*) from companies where id = @added", new { added }));
    }

    [Fact]
    public async Task ASavepointInAnAbortedTransactionIsAMachineryFailure()
    {
        var company = await id.SeedCompanyAsync();
        await using (var tx = await BeginAsync())
        {
            await tx.ExecuteAsync("update companies set name_en = 'Lost' where id = @company", new { company });
            await Assert.ThrowsAsync<PostgresException>(() => tx.ExecuteAsync("select 1/0"));
            var ran = false;

            var failed = await Assert.ThrowsAsync<SavepointFailedException>(() => tx.SavepointAsync("probe", () =>
            {
                ran = true;
                return tx.ExecuteAsync("select 1");
            }));

            Assert.False(ran); // SAVEPOINT is sent at once and fails before the work
            Assert.Equal(PostgresErrorCodes.InFailedSqlTransaction, Assert.IsType<PostgresException>(failed.InnerException).SqlState);
            await Assert.ThrowsAsync<TransactionAbortedException>(() => tx.CommitAsync());
        } // and the transaction is still there for the rollback
        Assert.Equal("Test Company", await NameOfAsync(company));
    }

    /// <summary>
    /// Why <see cref="DbTx.SavepointAsync{T}"/> does not use <c>NpgsqlTransaction.SaveAsync</c>: Npgsql sends that
    /// <c>SAVEPOINT</c> with the next command, and when it fails there (an aborted transaction), Npgsql ends the whole
    /// transaction: the next rollback to the savepoint finds the transaction disposed, and the connection is outside any
    /// transaction. If this ever changes, the plain SQL could go back to the Npgsql call.
    /// </summary>
    [Fact]
    public async Task NpgsqlEndsTheTransactionWhenItsDeferredSavepointFails()
    {
        await using var connection = new NpgsqlConnection(Owner.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("select 1/0", transaction: tx));
        await tx.SaveAsync("deferred");
        await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("select 1", transaction: tx));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => tx.RollbackAsync("deferred"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select 1")); // no transaction any more
    }

    [Fact]
    public async Task ARollbackNeverLandsOnAnOuterSavepointOfTheSameName()
    {
        var company = await id.SeedCompanyAsync();
        await using var tx = await BeginAsync();
        await tx.SavepointAsync("same", async () =>
        {
            await tx.ExecuteAsync("update companies set name_en = 'Outer' where id = @company", new { company });
            await Assert.ThrowsAsync<PostgresException>(() => tx.ExecuteAsync("select 1/0")); // the outer work's error

            // An inner savepoint of the same name in the aborted transaction: ROLLBACK TO must not reach the outer one.
            await Assert.ThrowsAsync<SavepointFailedException>(() => tx.SavepointAsync("same", () => tx.ExecuteAsync("select 1")));
            return true;
        }).ContinueWith(_ => { }); // the outer RELEASE fails too: the transaction stays aborted
        await Assert.ThrowsAsync<TransactionAbortedException>(() => tx.CommitAsync());
    }

    [Fact]
    public async Task AFailedRollbackToTheSavepointIsAMachineryFailure()
    {
        await using var tx = await BeginAsync();

        var failed = await Assert.ThrowsAsync<SavepointFailedException>(() => tx.SavepointAsync<bool>("probe", async () =>
        {
            await tx.ExecuteAsync("release savepoint probe_1"); // the savepoint is gone: ROLLBACK TO cannot find it
            throw new InvalidOperationException("the work fails");
        }));

        Assert.Equal(PostgresErrorCodes.InvalidSavepointSpecification, Assert.IsType<PostgresException>(failed.InnerException).SqlState);
        await Assert.ThrowsAsync<TransactionAbortedException>(() => tx.CommitAsync());
    }

    [Fact]
    public async Task AFailureOfTheWorkIsRethrownAsItIsAndTheTransactionGoesOn()
    {
        var company = await id.SeedCompanyAsync();
        await using (var tx = await BeginAsync())
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => tx.SavepointAsync("probe", async () =>
            {
                await tx.ExecuteAsync("update companies set name_en = 'Inside' where id = @company", new { company });
                return await tx.ExecuteAsync("select 1/0");
            }));
            Assert.Equal(PostgresErrorCodes.DivisionByZero, refused.SqlState);
            await tx.ExecuteAsync("update companies set name_en = 'Outside' where id = @company", new { company });
            await tx.CommitAsync();
        }
        Assert.Equal("Outside", await NameOfAsync(company));
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static async Task<Company> TrackAsync(DbTx tx, Guid company) =>
        await tx.Db().Set<Company>().FromSql($"select * from companies where id = {company} for update").AsTracking().SingleAsync();

    private static Company NewCompany(Guid id) => new()
    {
        Id = id,
        LegalName = "Inside Co",
        NameAr = "شركة",
        NameEn = "Inside Co",
        Status = CompanyStatus.ACTIVE,
        CooldownMinutes = 180,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
