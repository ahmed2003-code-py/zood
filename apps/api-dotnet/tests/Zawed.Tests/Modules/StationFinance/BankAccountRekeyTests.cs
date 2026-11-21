using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zawed.Api.Modules.StationFinance;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Tests.Platform;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.StationFinance;

/// <summary>
/// The bank account column in a data-key rotation (ADR 0027; the bank account lines of Java
/// <c>DataKeyRotationTests</c>, whose whole port is wave 5's): on a database of its own where key 2 is current, an
/// account number written under key 1 is moved to key 2 by the job through the column this module registers, stays
/// readable with key 2 alone under <see cref="BankAccounts.Binding"/>, and the database writes exactly one
/// <c>BANK_ACCOUNT_REKEYED</c> row for it, with the key ids and no actor.
/// </summary>
public sealed class BankAccountRekeyTests(RotationApi api) : IClassFixture<RotationApi>
{
    private static readonly DataEncryptor Key1Only = new(1, new Dictionary<int, byte[]> { [1] = Convert.FromBase64String(RotationApi.Key1) });
    private static readonly DataEncryptor Key2Only = new(2, new Dictionary<int, byte[]> { [2] = Convert.FromBase64String(RotationApi.Key2) });

    [Fact]
    public async Task AnAccountUnderTheOldKeyIsRekeyedByTheJobStaysReadableAndIsAuditedOnce()
    {
        var f = new FinanceTestSupport(api.Factory);
        var ops = await f.OpsAsync();
        var pump = await f.Fueling.PumpAsync();
        var change = Guid.Parse(await f.RequestBankChangeAsync(ops, pump.StationId));
        var owner = api.Owner;
        Assert.Equal(2, await owner.QueryOneAsync<int>("select data_key_id(account_encrypted) from station_bank_accounts where id = @change",
            new { change }));

        // Back under key 1, as the owner with the triggers off (the V20 guard rightly refuses a lower key id).
        var current = await owner.QueryOneAsync<byte[]>("select account_encrypted from station_bank_accounts where id = @change", new { change });
        var number = Key2Only.Decrypt(current!, BankAccounts.Binding(change));
        Assert.Equal(IbanKey, Encoding.UTF8.GetString(number));
        await owner.ExecuteAsync("""
            set session_replication_role = replica;
            update station_bank_accounts set account_encrypted = @old where id = @change;
            set session_replication_role = origin;
            """, new { old = Key1Only.Encrypt(number, BankAccounts.Binding(change)), change });

        var column = Assert.Single(api.Factory.Services.GetServices<EncryptedColumn>(), c => c.Name == "station_bank_accounts.account_encrypted");
        var rotation = new DataKeyRotation(api.Factory.Services.GetRequiredService<ITransactions>(),
            api.Factory.Services.GetRequiredService<DataEncryptor>(), [column], NullLogger<DataKeyRotation>.Instance);
        var run = await rotation.RunAsync();

        Assert.Equal(0, run.Failed);
        Assert.True(run.Reencrypted >= 1);
        var rekeyed = await owner.QueryOneAsync<byte[]>("select account_encrypted from station_bank_accounts where id = @change", new { change });
        Assert.Equal(2, DataEncryptor.KeyIdOf(rekeyed!));
        Assert.Equal(number, Key2Only.Decrypt(rekeyed!, BankAccounts.Binding(change)));
        Assert.Equal("PENDING", await owner.QueryOneAsync<string>("select status from station_bank_accounts where id = @change", new { change }));

        var audits = await owner.QueryAsync<(Guid? Actor, string ScopeType, Guid ScopeId, string Metadata)>("""
            select actor_id, scope_type, scope_id, metadata::text from audit_log
            where action = 'BANK_ACCOUNT_REKEYED' and entity_type = 'STATION_BANK_ACCOUNT' and entity_id = @change
            """, new { change });
        var audit = Assert.Single(audits);
        Assert.Null(audit.Actor);
        Assert.Equal(("OPERATOR", Guid.Parse(pump.OperatorId)), (audit.ScopeType, audit.ScopeId));
        var metadata = JsonDocument.Parse(audit.Metadata).RootElement;
        Assert.Equal((1, 2, pump.StationId), (metadata.GetProperty("fromKeyId").GetInt32(), metadata.GetProperty("toKeyId").GetInt32(),
            metadata.GetProperty("stationId").GetString()));
        Assert.DoesNotContain(IbanKey, audit.Metadata, StringComparison.Ordinal);
        Assert.Equal(0, (await rotation.RunAsync()).Reencrypted);
    }

    private const string IbanKey = "EG380019000500000000263180002";
}
