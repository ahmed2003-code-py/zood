namespace Zawed.Api.Platform.Config;

/// <summary>
/// Refuses to start an internet-facing instance that is configured like a development machine. Port
/// of the Java <c>DeploymentGuard</c>, with the same three rules and messages:
/// <list type="number">
/// <item>the deployed profile with an environment that is not dev, staging or production;</item>
/// <item>production with the second factor switched off (<c>ZAWED_MFA_REQUIRED=false</c>);</item>
/// <item>a deployed environment without the deployed profile.</item>
/// </list>
/// It runs in <c>Program</c> before the host is built, so nothing is served.
/// </summary>
public static class DeploymentGuard
{
    public static void Check(ZawedOptions options, bool deployedProfile)
    {
        var environment = options.Environment;
        var isDeployed = environment is { } e && e.IsDeployed();
        if (deployedProfile && !isDeployed)
        {
            throw new InvalidOperationException(
                $"The '{ZawedConfiguration.DeployedProfile}' profile is active but zawed.environment is "
                + $"{environment?.ToString() ?? "not set"}. Set ZAWED_ENVIRONMENT to dev, staging or production.");
        }
        if (!options.Security.MfaRequired && environment == ZawedEnvironment.PRODUCTION)
        {
            throw new InvalidOperationException(
                "zawed.security.mfa-required is false in production. The second factor may be "
                + "switched off for testing on dev only (ZAWED_MFA_REQUIRED).");
        }
        if (!deployedProfile && isDeployed)
        {
            throw new InvalidOperationException(
                $"zawed.environment is {environment} but the '{ZawedConfiguration.DeployedProfile}' profile is not active. "
                + $"Set SPRING_PROFILES_ACTIVE={ZawedConfiguration.DeployedProfile}.");
        }
        var failures = ZawedOptionsValidator.Failures(options);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("The ZAWED configuration is not safe to start with: " + string.Join("; ", failures));
        }
    }
}

/// <summary>The PostgreSQL connection (Railway's <c>PG*</c> variables).</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "zawed";
    public string User { get; set; } = "zawed";
    public string Password { get; set; } = "";

    /// <summary>Apply pending migrations at startup, as the owner, before serving (the Flyway default).</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Pool ceiling of the runtime data source.</summary>
    public int MaxPoolSize { get; set; } = 20;
}
