using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Persistence;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>
/// The application in the middle of a rotation, with key 2 current, key 1 kept to read and the lookup key pinned to
/// key 1 (ADR 0027), on a database of its own: the job re-encrypts every value it can see, and the other test classes
/// run with key 1 only.
/// </summary>
public sealed class RotationApi : IAsyncLifetime
{
    /// <summary>Key 1: the committed development key, the one every other test class writes with.</summary>
    public const string Key1 = "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=";

    public const string Key2 = "bmV3LWRhdGEta2V5LWZvci1yb3RhdGlvbi10ZXN0cyE=";

    public ZawedApiFactory Factory { get; private set; } = null!;

    public Owner Owner { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var database = await TestDatabase.NewDatabaseAsync("rotation");
        Factory = new ZawedApiFactory(database, new Dictionary<string, string?>
        {
            ["Zawed:Security:DataKey"] = Key2,
            ["Zawed:Security:DataKeyId"] = "2",
            ["Zawed:Security:DataKeysPrevious"] = "1:" + Key1,
            ["Zawed:Security:LookupKey"] = Key1,
            ["Zawed:DataKeyRotation:Enabled"] = "false",
        });
        Factory.CreateClient().Dispose(); // starts the application: migrations, then the key state
        Owner = await Owner.OfAsync(database);
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// The data-key rotation job's own behaviour (ADR 0027; the job half of Java <c>DataKeyRotationTests</c>): values move
/// to the current key and nothing else in the row changes; a row another transaction holds is skipped, not waited for,
/// and taken on the next run; a run is resumable and parallel runs never re-encrypt a value twice; an instance with
/// another current key does nothing; an unreadable value is left as it is and counted without being logged.
/// <para>The rotation runs here over a test column (<c>users.mfa_secret_encrypted</c> with a binding of this class's
/// own). The end-to-end half of the Java class, which reads the real columns where they are used (TOTP, the snapshot,
/// a fueling, bank accounts), moves to wave 5 with the modules that register them (migration plan, 1B).</para>
/// </summary>
public sealed class DataKeyRotationJobTests(RotationApi api) : IClassFixture<RotationApi>
{
    private const string Mfa = "users.mfa_secret_encrypted";

    private static readonly DataEncryptor Key1Only = new(1, new Dictionary<int, byte[]> { [1] = Convert.FromBase64String(RotationApi.Key1) });
    private static readonly DataEncryptor Key2Only = new(2, new Dictionary<int, byte[]> { [2] = Convert.FromBase64String(RotationApi.Key2) });

    private static readonly EncryptedColumn MfaColumn = new("users", "mfa_secret_encrypted", Binding);

    private readonly CapturingLogger<DataKeyRotation> log = new();

    private Owner Owner => api.Owner;

    private static byte[] Binding(Guid id) => Encoding.UTF8.GetBytes("rotation-test-user:" + id);

    private DataKeyRotation Rotation(params EncryptedColumn[] columns) =>
        new(api.Factory.Services.GetRequiredService<ITransactions>(), api.Factory.Services.GetRequiredService<DataEncryptor>(),
            columns.Length == 0 ? [MfaColumn] : columns, log);

    /// <summary>Users with a TOTP secret under key 1, written straight into the table. Returns id to secret.</summary>
    private async Task<Dictionary<Guid, byte[]>> OldUsers(int count)
    {
        var users = new Dictionary<Guid, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
            await Owner.ExecuteAsync("""
                insert into users (id, email, full_name, password_hash, role, scope_type, status, mfa_secret_encrypted, mfa_enabled, created_at, updated_at)
                values (@id, @email, 'Old user', 'not-a-hash', 'OPS', 'PLATFORM', 'ACTIVE', @value, true, now() - interval '3 days', now() - interval '2 days')
                """, new { id, email = $"old-{id:N}@zawed.test", value = Key1Only.Encrypt(secret, Binding(id)) });
            users[id] = secret;
        }
        return users;
    }

    private Task<byte[]> Value(Guid id) => Owner.QueryOneAsync<byte[]>("select mfa_secret_encrypted from users where id = @id", new { id })!;

    private async Task<int[]> KeyIdsOf(IEnumerable<Guid> ids) =>
        (await Owner.QueryAsync<int>("select data_key_id(mfa_secret_encrypted) from users where id = any(@ids)", new { ids = ids.ToArray() }))
        .Distinct().Order().ToArray();

    private async Task AssertSecrets(Dictionary<Guid, byte[]> users)
    {
        foreach (var (id, secret) in users)
        {
            Assert.Equal(secret, Key2Only.Decrypt(await Value(id), Binding(id)));
        }
    }

    [Fact]
    public async Task EveryOldValueIsMovedToTheNewKeyAndNothingElseInTheRowChanges()
    {
        await Rotation().RunAsync(); // whatever earlier tests left
        var users = await OldUsers(5);
        const string rowWithoutCiphertext = "select id, email, status, mfa_enabled, created_at, updated_at from users where id = any(@ids) order by id";
        var before = await Owner.QueryAsync<(Guid, string, string, bool, DateTime, DateTime)>(rowWithoutCiphertext, new { ids = users.Keys.ToArray() });

        var run = await Rotation().RunAsync();

        Assert.Equal(5, run.Reencrypted);
        Assert.Equal(0, run.Failed);
        Assert.Equal(0, run.Remaining);
        Assert.False(run.KeyOverlap);
        Assert.Equal(2, run.CurrentKeyId);
        Assert.Equal(new[] { 2 }, await KeyIdsOf(users.Keys));
        // Readable with key 2 alone, the same plaintext, bound to the same row: key 1 can be retired.
        await AssertSecrets(users);
        Assert.Equal(before, await Owner.QueryAsync<(Guid, string, string, bool, DateTime, DateTime)>(rowWithoutCiphertext, new { ids = users.Keys.ToArray() }));
        Assert.Equal(0, (await Rotation().RunAsync()).Reencrypted);
        Assert.Contains(log.Lines, l => l.Contains("every encrypted value is under the current key 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARowAnotherTransactionHoldsIsSkippedAndTakenOnTheNextRunAndAKeyShareLockDoesNotStopTheJob()
    {
        await Rotation().RunAsync();
        var users = await OldUsers(2);
        var referenced = users.Keys.First();
        var locked = users.Keys.Last();

        await using (var other = new NpgsqlConnection(Owner.ConnectionString))
        {
            await other.OpenAsync();
            await using var tx = await other.BeginTransactionAsync();
            // What a fueling's foreign key check holds on its tag until the fueling commits, and what an application
            // transaction that changes the row holds.
            await using (var share = new NpgsqlCommand("select id from users where id = @id for key share", other, tx))
            {
                share.Parameters.AddWithValue("id", referenced);
                await share.ExecuteScalarAsync();
            }
            await using (var update = new NpgsqlCommand("select id from users where id = @id for update", other, tx))
            {
                update.Parameters.AddWithValue("id", locked);
                await update.ExecuteScalarAsync();
            }

            var run = await Rotation().RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, run.Reencrypted);
            Assert.Equal(new Dictionary<int, long> { [1] = 1 }, run.Column(Mfa).RemainingByKeyId);
            Assert.Equal(2, DataEncryptor.KeyIdOf(await Value(referenced))); // a key share lock does not stop the job
            Assert.Equal(1, DataEncryptor.KeyIdOf(await Value(locked))); // a row another transaction holds is skipped, not waited for
            await tx.RollbackAsync();
        }

        await Rotation().RunAsync();
        Assert.Equal(2, DataEncryptor.KeyIdOf(await Value(locked))); // and taken on the next run
        await AssertSecrets(users);
    }

    [Fact]
    public async Task ARunIsResumableAndTwoRunsAtOnceNeverReencryptAValueTwice()
    {
        await Rotation().RunAsync();
        var first = await OldUsers(25);

        var partial = await Rotation().RunAsync(10, 1);
        Assert.Equal(10, partial.Reencrypted);
        Assert.Equal(new Dictionary<int, long> { [1] = 15 }, partial.Column(Mfa).RemainingByKeyId);
        Assert.Equal(10, (await Rotation().RunAsync(10, 1)).Reencrypted);
        var rest = await Rotation().RunAsync(10, 100);
        Assert.Equal(5, rest.Reencrypted);
        Assert.Equal(0, rest.Remaining);
        Assert.Equal(0, (await Rotation().RunAsync()).Reencrypted);
        await AssertSecrets(first);

        var second = await OldUsers(60);
        using var start = new Barrier(4);
        var together = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            return Rotation().RunAsync(5, 100);
        })));

        Assert.Equal(60, together.Sum(r => r.Reencrypted));
        Assert.Equal(0, together.Sum(r => r.Failed));
        Assert.Equal(new[] { 2 }, await KeyIdsOf(second.Keys));
        await AssertSecrets(second);
    }

    [Fact]
    public async Task TheKeyStateIsTheOwnersAndAnInstanceWithAnotherCurrentKeyDoesNothing()
    {
        Assert.Equal(2, await Owner.QueryOneAsync<int>("select current_key_id::integer from data_key_state"));
        var fingerprint = await Owner.QueryOneAsync<string>("select lookup_key_fingerprint from data_key_state");
        Assert.Equal(DataKeyState.Fingerprint(new LookupHasher(Convert.FromBase64String(RotationApi.Key1))), fingerprint);
        Assert.DoesNotContain(RotationApi.Key1, fingerprint);
        foreach (var write in new[] { "update data_key_state set current_key_id = 3", "delete from data_key_state" })
        {
            Assert.Contains("permission denied", (await RuntimeConnections.RefusalAsync(Owner, "PLATFORM", write))!.MessageText);
        }
        Assert.Equal(2, await Owner.AsRuntimeRoleAsync("PLATFORM", null, null,
            async (c, tx) => (int)(await new NpgsqlCommand("select data_key_current_id()", c, tx).ExecuteScalarAsync())!));

        await Rotation().RunAsync();
        var users = await OldUsers(1);
        await Owner.ExecuteAsync("update data_key_state set current_key_id = 3");
        try
        {
            var run = await Rotation().RunAsync();
            Assert.True(run.KeyOverlap);
            Assert.Equal(0, run.Reencrypted);
            Assert.Equal(new[] { 1 }, await KeyIdsOf(users.Keys));
            Assert.Contains(log.Lines, l => l.Contains("this instance's current key 2 is not the deployment's current key 3", StringComparison.Ordinal));

            // The job records the overlap as a skip, so an overlap that lasts turns its health DOWN.
            var services = api.Factory.Services;
            var jobs = new Jobs(services.GetRequiredService<ITransactions>(), TimeProvider.System, services.GetRequiredService<IConfiguration>(),
                services.GetRequiredService<ILogger<Jobs>>());
            var job = new DataKeyRotationJob(jobs, Rotation(), services.GetRequiredService<IConfiguration>());
            Assert.Equal(JobOutcome.SKIPPED_LOCKED, (await job.RunNowAsync()).Outcome);
            Assert.Equal("SKIPPED_LOCKED", await Owner.QueryOneAsync<string>(
                "select outcome from job_runs where job_name = 'data-key-rotation' order by started_at desc limit 1"));
        }
        finally
        {
            await Owner.ExecuteAsync("update data_key_state set current_key_id = 2");
        }
        Assert.True((await Rotation().RunAsync()).Reencrypted > 0);
        await AssertSecrets(users);
    }

    [Fact]
    public async Task AValueThatCannotBeReadIsLeftAsItIsAndCountedWithoutLoggingIt()
    {
        await Rotation().RunAsync();
        var users = await OldUsers(2);
        var ids = users.Keys.ToList();
        var unknown = await Value(ids[0]);
        unknown[0] = 9; // a key this instance does not hold
        await Owner.ExecuteAsync("update users set mfa_secret_encrypted = @v where id = @id", new { v = unknown, id = ids[0] });
        // Bound to another row, as a value copied from elsewhere would be: it does not verify here.
        var copied = Key1Only.Encrypt(users[ids[1]], Binding(Guid.CreateVersion7()));
        await Owner.ExecuteAsync("update users set mfa_secret_encrypted = @v where id = @id", new { v = copied, id = ids[1] });
        try
        {
            var run = await Rotation().RunAsync();

            Assert.Equal(0, run.Reencrypted);
            Assert.Equal(1, run.Column(Mfa).Failed);
            Assert.Equal(new Dictionary<int, long> { [1] = 1, [9] = 1 }, run.Column(Mfa).RemainingByKeyId);
            Assert.Equal(unknown, await Value(ids[0]));
            Assert.Equal(copied, await Value(ids[1]));
            var lines = string.Join("\n", log.Lines);
            Assert.Contains("Data key rotation: current key 2", lines);
            Assert.Contains("users.mfa_secret_encrypted key 1=1,key 9=1 failed=1", lines);
            foreach (var value in new[] { unknown, copied }.Concat(users.Values))
            {
                Assert.DoesNotContain(Convert.ToHexString(value), lines, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToBase64String(value), lines);
            }
        }
        finally
        {
            await Owner.ExecuteAsync("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = any(@ids)", new { ids = ids.ToArray() });
        }
    }

    [Fact]
    public async Task AColumnThatCannotBeProcessedDoesNotStopTheOthersAndARunWhereEveryColumnFailsIsAFailure()
    {
        await Rotation().RunAsync();
        var users = await OldUsers(1);
        var missing = new EncryptedColumn("no_such_table", "value_encrypted", Binding);

        var run = await Rotation(missing, MfaColumn).RunAsync();
        Assert.Equal(1, run.ColumnErrors);
        Assert.Equal(1, run.Reencrypted);
        await AssertSecrets(users);

        var services = api.Factory.Services;
        var jobs = new Jobs(services.GetRequiredService<ITransactions>(), TimeProvider.System, services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<ILogger<Jobs>>());
        var job = new DataKeyRotationJob(jobs, Rotation(missing), services.GetRequiredService<IConfiguration>());
        Assert.Equal(JobOutcome.FAILED, (await job.RunNowAsync()).Outcome);
    }

    [Theory]
    [InlineData("users; drop table users", "mfa_secret_encrypted")]
    [InlineData("users", "Mfa")]
    [InlineData("", "mfa_secret_encrypted")]
    [InlineData("users", "mfa_secret_encrypted\n")]
    public void AnEncryptedColumnIsNamedByPlainLowerCaseIdentifiers(string table, string column) =>
        Assert.Throws<ArgumentException>(() => new EncryptedColumn(table, column, Binding));

    /// <summary>Keeps every formatted log line, as the Java test's captured output.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> lines = new();

        public IReadOnlyList<string> Lines => [.. lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}
