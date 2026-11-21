using Zawed.Api.Modules.Identity.Contracts;
using Zawed.Api.Platform.Modules;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Identity;

/// <summary>
/// The identity module (Java package <c>identity</c>): sign-in for every role, sessions and refresh, MFA, the
/// password-change gate's other half, user administration, the first Super Admin, and the session retention job.
/// Its <see cref="Sessions"/> is the platform's <see cref="ISessionVerifier"/>.
/// </summary>
public sealed class IdentityModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IdentityStore>();
        services.AddSingleton<AccessTokens>();
        services.AddSingleton<AuthCookies>();
        services.AddSingleton<CsrfRule>();
        services.AddScoped<Sessions>();
        services.AddScoped<ISessionVerifier>(sp => sp.GetRequiredService<Sessions>());
        services.AddScoped<SignIn>();
        services.AddScoped<Account>();
        services.AddScoped<UserAdmin>();
        services.AddScoped<IActorDirectory, ActorDirectory>();
        services.AddSingleton<SessionRetention>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionRetention>());
    }
}
