using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Fleet;
using Zawed.Api.Modules.Fleet.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Domain;
using Zawed.Domain.Rules;
using Zawed.Tests.Modules.Tenancy;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fleet.FleetTestSupport;

namespace Zawed.Tests.Modules.Fleet;

/// <summary>
/// What the fleet publishes for the fueling engine and offline sync (Java <c>TagGate</c> and <c>FleetSnapshot</c>, exercised
/// in Java by the fueling and sync tests, which come with waves 3 and 4): finding a tag by any spelling of its UID, the lock
/// read, the limits, the cooldown, and the offline digest that never carries a UID.
/// </summary>
public sealed class FleetGatesTests(ProbeApi api) : IClassFixture<ProbeApi>
{
    [Fact]
    public async Task TheGateFindsLocksAndCoolsDownATagWhateverTheUidSpelling()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var vehicle = (await (await api.CreateVehicleAsync(ops, company, UniquePlate(), "VIP", 300_000, "DIESEL", 80_000)).JsonAsync()).Text("id")!;
        var uid = RandomUid();
        var tag = Guid.Parse((await (await api.IssueTagAsync(ops, vehicle, uid)).JsonAsync()).Text("id")!);
        await api.PutAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/limits", """{"kind":"AMOUNT","period":"WEEK","maxValue":90000}""");
        var until = DateTimeOffset.UtcNow.AddHours(3);

        var (bound, none, garbage, atPump, limits) = await InPlatformScopeAsync(async services =>
        {
            var gate = services.GetRequiredService<ITagGate>();
            var spelled = string.Join("-", Enumerable.Range(0, uid.Length / 2).Select(i => uid.Substring(i * 2, 2).ToLowerInvariant()));
            var found = await gate.FindAsync(spelled);
            var unknown = await gate.FindAsync(RandomUid());
            var notAUid = await gate.FindAsync("not a uid");
            var locked = await gate.LockAsync(tag);
            var rules = await gate.ActiveLimitsAsync(Guid.Parse(vehicle));
            await gate.StartCooldownAsync(tag, until);
            var missing = await Assert.ThrowsAsync<ApiException>(() => gate.LockAsync(Guid.CreateVersion7()));
            Assert.Equal(404, missing.Status);
            return (found, unknown, notAUid, locked, rules);
        }, commit: true);

        Assert.Equal(new BoundTag(tag, company), bound);
        Assert.Null(none);
        Assert.Null(garbage);
        Assert.Equal(new TagAtPump(tag, Guid.Parse(vehicle), company, true, null, atPump.Plate, FuelType.DIESEL, true, true, 80_000, 300_000), atPump);
        Assert.Equal([new TagLimit(FuelingRules.LimitKind.AMOUNT, Periods.Period.WEEK, 90_000)], limits);
        var stored = await api.Owner.QueryOneAsync<DateTime>("select cooldown_until from rfid_tags where id = @tag", new { tag });
        Assert.True(Math.Abs((stored - until.UtcDateTime).TotalMilliseconds) < 1);
    }

    [Fact]
    public async Task TheSnapshotCarriesADigestNeverAUidAndLeavesOutShortAndTamperedTags()
    {
        var ops = await api.SeedAsync(Role.OPS);
        var company = await api.SeedCompanyAsync();
        var vehicle = (await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!;
        var shortVehicle = (await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!;
        var tamperedVehicle = (await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!;
        var uid = RandomUid();
        await api.IssueTagAsync(ops, vehicle, uid);
        await api.IssueTagAsync(ops, shortVehicle, RandomUid()[..8]);
        // Two tags written past the application: one whose ciphertext is bound to another tag id (it does not verify), one
        // whose lookup hash is another UID's (a swapped ciphertext). Both are left out; every other tag still goes out.
        var encryptor = api.Factory.Services.GetRequiredService<DataEncryptor>();
        var hasher = api.Factory.Services.GetRequiredService<LookupHasher>();
        var mismatchedVehicle = (await (await api.CreateVehicleAsync(ops, company, UniquePlate())).JsonAsync()).Text("id")!;
        foreach (var (target, boundTo, lookupOf) in new[] { (tamperedVehicle, Guid.CreateVersion7(), (string?)null), (mismatchedVehicle, (Guid?)null, RandomUid()) })
        {
            var id = Guid.CreateVersion7();
            var plain = RandomUid();
            await api.Owner.ExecuteAsync("""
                insert into rfid_tags (id, company_id, vehicle_id, uid_encrypted, uid_lookup, uid_last4, status, created_at, updated_at)
                values (@id, @company, @vehicle, @encrypted, @lookup, 'XXXX', 'ACTIVE', now(), now())
                """, new
            {
                id, company, vehicle = Guid.Parse(target),
                encrypted = encryptor.Encrypt(Encoding.ASCII.GetBytes(plain), Tags.AssociatedData(boundTo ?? id)),
                lookup = hasher.Hash(Tags.LookupNamespace, lookupOf ?? plain),
            });
        }
        await api.PutAsync(ops, $"/api/v1/ops/vehicles/{vehicle}/limits", """{"kind":"VOLUME","period":"DAY","maxValue":60000}""");
        var key = RandomNumberGenerator.GetBytes(32);

        var fleet = await InPlatformScopeAsync(services => services.GetRequiredService<IFleetSnapshot>().SnapshotAsync(key), commit: false);

        var digest = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(uid)))[..8];
        var mine = fleet.Tags.Where(t => t.VehicleId == Guid.Parse(vehicle)).ToList();
        Assert.Equal([new SnapshotTag(digest, Guid.Parse(vehicle), "ACTIVE", null)], mine);
        Assert.DoesNotContain(fleet.Tags, t => t.VehicleId == Guid.Parse(shortVehicle) || t.VehicleId == Guid.Parse(tamperedVehicle)
            || t.VehicleId == Guid.Parse(mismatchedVehicle));
        Assert.Contains(fleet.Vehicles, v => v.Id == Guid.Parse(vehicle) && v.CompanyId == company && v.Active && !v.Vip);
        Assert.Contains(new SnapshotLimit(Guid.Parse(vehicle), FuelingRules.LimitKind.VOLUME, Periods.Period.DAY, 60_000), fleet.Limits);
        Assert.DoesNotContain(fleet.Limits, l => l.VehicleId == Guid.Parse(shortVehicle));
    }

    [Fact]
    public void TagUidsAreRegisteredForTheDataKeyRotationWithTheJavaBinding()
    {
        var column = Assert.Single(api.Factory.Services.GetServices<EncryptedColumn>(), c => c.Table == "rfid_tags");
        Assert.Equal("uid_encrypted", column.Column);
        var id = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a8057");
        Assert.Equal("rfid_tags.uid:01890a5d-ac96-774b-bcce-b302099a8057"u8.ToArray(), column.Binding(id));
    }

    private async Task<T> InPlatformScopeAsync<T>(Func<IServiceProvider, Task<T>> work, bool commit)
    {
        await using var services = api.Factory.Services.CreateAsyncScope();
        var session = services.ServiceProvider.GetRequiredService<DbSession>();
        var ops = new ZawedPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), Role.OPS, Scope.Platform, false);
        await using var tx = await services.ServiceProvider.GetRequiredService<ITransactions>().BeginAsync(DbScopeContext.ForPrincipal(ops));
        session.Attach(tx);
        try
        {
            var result = await work(services.ServiceProvider);
            if (commit)
            {
                await tx.CommitAsync();
            }
            return result;
        }
        finally
        {
            session.Detach();
        }
    }
}
