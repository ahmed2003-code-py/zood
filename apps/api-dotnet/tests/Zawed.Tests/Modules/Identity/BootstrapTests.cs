using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>
/// The first Super Admin, created once (Java <c>BootstrapTests</c>). "No Super Admin exists" is the precondition under
/// test, so this class runs on a database of its own: the Java class emptied the shared one's Super Admin rows, which
/// in a parallel .NET run would take the role away from other classes' users mid-test.
/// </summary>
public sealed class BootstrapTests : IAsyncLifetime
{
    private const string Token = "bootstrap-token-for-the-test-0123456789abcdef";

    private ZawedApiFactory factory = null!;
    private IdentityTestSupport id = null!;

    public async Task InitializeAsync()
    {
        var database = await TestDatabase.NewDatabaseAsync("bootstrap");
        factory = new ZawedApiFactory(database, new Dictionary<string, string?> { ["Zawed:Security:BootstrapToken"] = Token });
        id = new IdentityTestSupport(factory);
    }

    public async Task DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task TheFirstSuperAdminCanBeCreatedOnceAndThenTheDoorIsGone()
    {
        var wrongToken = await id.PostAsync("/api/v1/setup/super-admin", Body("not-the-token"));
        Assert.Equal(404, (int)wrongToken.StatusCode);
        Assert.Equal("SETUP_NOT_AVAILABLE", await CodeOf(wrongToken));

        var created = await id.PostAsync("/api/v1/setup/super-admin", Body(Token));
        Assert.True((int)created.StatusCode == 201, await created.Content.ReadAsStringAsync());
        var body = await JsonOf(created);
        Assert.Equal("SUPER_ADMIN", body["role"]!.GetValue<string>());
        Assert.False(body["scope"]!.AsObject().ContainsKey("id"));

        var again = await id.PostAsync("/api/v1/setup/super-admin", Body(Token));
        Assert.Equal(404, (int)again.StatusCode);
        Assert.Equal(1L, await id.Owner.QueryOneAsync<long>("select count(*) from users where role = 'SUPER_ADMIN'"));
        Assert.Equal(1L, await id.Owner.QueryOneAsync<long>("select count(*) from audit_log where action = 'SUPER_ADMIN_BOOTSTRAPPED'"));
    }

    [Fact]
    public async Task AWeakFirstPasswordIsRefusedAndNothingIsCreated()
    {
        var weak = await id.PostAsync("/api/v1/setup/super-admin",
            $"{{\"setupToken\":\"{Token}\",\"email\":\"root@zawed.test\",\"fullName\":\"First Admin\",\"password\":\"too short\"}}");
        Assert.Equal(400, (int)weak.StatusCode);
        Assert.Equal("PASSWORD_TOO_WEAK", await CodeOf(weak));
        Assert.Equal(0L, await id.Owner.QueryOneAsync<long>("select count(*) from users where role = 'SUPER_ADMIN'"));
    }

    private static string Body(string token) =>
        $"{{\"setupToken\":\"{token}\",\"email\":\"root-{Guid.NewGuid()}@zawed.test\",\"fullName\":\"First Admin\",\"password\":\"a long first admin password\"}}";
}
