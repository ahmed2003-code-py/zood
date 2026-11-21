using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Identity;

/// <summary>The application with the second factor switched off (<c>ZAWED_MFA_REQUIRED=false</c>), on the shared database.</summary>
public sealed class NoMfaApi : IAsyncLifetime
{
    public ZawedApiFactory Factory { get; } = new(settings: new Dictionary<string, string?> { ["Zawed:Security:MfaRequired"] = "false" });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// The second factor switched off for testing (zawed-dev only, owner 2026-11-19; production refuses it, see
/// <c>ZawedOptionsTests</c>): every role signs in with its password alone, enrolled or not (Java <c>SignInWithoutMfaTests</c>).
/// </summary>
public sealed class SignInWithoutMfaTests(NoMfaApi api) : IClassFixture<NoMfaApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    [Fact]
    public async Task OpsWithoutAnAuthenticatorSignsInWithThePasswordAlone()
    {
        var ops = await id.SeedAsync(Role.OPS);
        await id.Owner.ExecuteAsync("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = @Id", new { ops.Id });

        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(ops.Email, Password)));
        Assert.Equal("AUTHENTICATED", login["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnEnrolledSuperAdminSignsInAtTheOneSignInPathWithoutACode()
    {
        var admin = await id.SeedAsync(Role.SUPER_ADMIN);

        var login = await id.PostAsync("/api/v1/auth/login", LoginBody(admin.Email, Password));
        var body = await JsonOf(login);
        Assert.Equal("AUTHENTICATED", body["status"]!.GetValue<string>());
        Assert.Equal("SUPER_ADMIN", body["session"]!["user"]!["role"]!.GetValue<string>());
        Assert.Contains(SetCookies(login), c => c.StartsWith("zawed_rt=", StringComparison.Ordinal) && c.Contains("Path=/api/v1/auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStationManagerSignsInWithThePasswordAlone()
    {
        var manager = await id.SeedAsync(Role.STATION_MANAGER, Scope.Operator(Guid.CreateVersion7()), false);

        var login = await JsonOf(await id.PostAsync("/api/v1/auth/login", LoginBody(manager.Email, Password)));
        Assert.Equal("AUTHENTICATED", login["status"]!.GetValue<string>());
    }
}
