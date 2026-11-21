namespace Zawed.Api.Platform.Config;

/// <summary>
/// Reads the same environment variables as the Java API, so Railway's variables do not change at the
/// cutover (ADR 0029):
/// <list type="bullet">
/// <item><c>SPRING_PROFILES_ACTIVE=deployed</c> (or <c>ZAWED_PROFILE=deployed</c>) selects the deployed
/// profile: no development defaults at all, Secure cookies, forwarded headers, JSON logs.</item>
/// <item><c>ZAWED_*</c> map onto the <c>Zawed</c> section; <c>PG*</c> onto <c>Database</c>; <c>PORT</c> is
/// the HTTP port.</item>
/// </list>
/// Outside the deployed profile, the committed development values (the same as the Java
/// application.yaml, so a local database made by either API works with the other) sit underneath
/// everything else. <see cref="ZawedOptionsValidator"/> refuses all of them when deployed.
/// </summary>
public static class ZawedConfiguration
{
    public const string DeployedProfile = "deployed";

    /// <summary>Environment variable → configuration key. One table, so the README and this agree.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["ZAWED_ENVIRONMENT"] = "Zawed:Environment",
        ["ZAWED_CORS_ALLOWED_ORIGINS"] = "Zawed:Cors:AllowedOrigins",
        ["ZAWED_JWT_SECRET"] = "Zawed:Security:JwtSecret",
        ["ZAWED_DATA_KEY"] = "Zawed:Security:DataKey",
        ["ZAWED_DATA_KEY_ID"] = "Zawed:Security:DataKeyId",
        ["ZAWED_DATA_KEYS_PREVIOUS"] = "Zawed:Security:DataKeysPrevious",
        ["ZAWED_LOOKUP_KEY"] = "Zawed:Security:LookupKey",
        ["ZAWED_BOOTSTRAP_TOKEN"] = "Zawed:Security:BootstrapToken",
        ["ZAWED_MFA_REQUIRED"] = "Zawed:Security:MfaRequired",
        ["ZAWED_API_DOCS"] = "Zawed:ApiDocs",
        ["PGHOST"] = "Database:Host",
        ["PGPORT"] = "Database:Port",
        ["PGDATABASE"] = "Database:Name",
        ["PGUSER"] = "Database:User",
        ["PGPASSWORD"] = "Database:Password",
        ["SENTRY_DSN"] = "Sentry:Dsn",
    };

    /// <summary>The development values of the Java application.yaml (local and test only).</summary>
    public static readonly IReadOnlyDictionary<string, string?> LocalDefaults = new Dictionary<string, string?>
    {
        ["Zawed:Environment"] = "LOCAL",
        ["Zawed:Cors:AllowedOrigins"] = "http://localhost:5173,http://127.0.0.1:5173",
        ["Zawed:Security:JwtSecret"] = "local-only-jwt-secret-do-not-deploy-0123456789abcdef",
        ["Zawed:Security:DataKey"] = ZawedOptionsValidator.CommittedLocalDataKey,
        ["Zawed:Security:BootstrapToken"] = "local-only-bootstrap-token-do-not-deploy",
        ["Zawed:Security:CookieSecure"] = "false",
        ["Database:Host"] = "localhost",
        ["Database:Port"] = "5432",
        ["Database:Name"] = "zawed",
        ["Database:User"] = "zawed",
        ["Database:Password"] = "",
    };

    /// <summary>What the deployed profile fixes, whatever else is set.</summary>
    public static readonly IReadOnlyDictionary<string, string?> DeployedFixed = new Dictionary<string, string?>
    {
        ["Zawed:Security:CookieSecure"] = "true",
    };

    /// <summary>Whether the deployed profile is active (the Java <c>SPRING_PROFILES_ACTIVE</c> is honoured).</summary>
    public static bool IsDeployedProfile(IConfiguration configuration)
    {
        var profiles = configuration["ZAWED_PROFILE"] ?? configuration["SPRING_PROFILES_ACTIVE"] ?? "";
        return profiles.Split(',', StringSplitOptions.TrimEntries).Contains(DeployedProfile, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Layers the configuration: development defaults (not when deployed) underneath, then whatever the
    /// host already has (appsettings, test settings), then the environment variable aliases on top.
    /// </summary>
    public static void Apply(ConfigurationManager configuration)
    {
        var deployed = IsDeployedProfile(configuration);
        var sources = configuration.Sources.ToList();
        configuration.Sources.Clear();
        if (!deployed)
        {
            configuration.AddInMemoryCollection(LocalDefaults);
        }
        foreach (var source in sources)
        {
            configuration.Sources.Add(source);
        }
        configuration.AddInMemoryCollection(AliasedValues(Environment.GetEnvironmentVariables()));
        if (deployed)
        {
            configuration.AddInMemoryCollection(DeployedFixed);
        }
    }

    /// <summary>The aliased variables that are set, as configuration keys.</summary>
    public static Dictionary<string, string?> AliasedValues(System.Collections.IDictionary environment)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (variable, key) in Aliases)
        {
            if (environment[variable] is string value)
            {
                values[key] = value;
            }
        }
        return values;
    }
}
