using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Health;
using Zawed.Api.Platform.Jobs;
using Zawed.Api.Platform.Modules;
using Zawed.Api.Platform.OpenApi;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Persistence.Migrations;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform;

/// <summary>
/// The platform's services and request pipeline, in one place. Modules plug in through
/// <see cref="IZawedModule"/>; nothing here names a business module.
/// </summary>
public static class PlatformSetup
{
    public static WebApplicationBuilder AddZawed(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddOptions<ZawedOptions>().Bind(configuration.GetSection(ZawedOptions.Section));
        services.AddSingleton<IValidateOptions<ZawedOptions>, ZawedOptionsValidator>();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section));
        services.AddSingleton(TimeProvider.System);

        // Logging: one JSON object per line when deployed (the Java ECS console format), with the request id scope.
        if (ZawedConfiguration.IsDeployedProfile(configuration))
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });
        }
        else
        {
            builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });
        }

        // Database
        services.AddSingleton(sp => DataSources.Runtime(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value));
        services.AddSingleton<ITransactions, Transactions>();
        services.AddScoped<DbSession>();
        services.AddScoped<IDbSession>(sp => sp.GetRequiredService<DbSession>());
        services.AddScoped<TransactionFilter>();
        services.AddScoped<ZawedDbContext>(sp => sp.GetRequiredService<IDbSession>().Current.Db());
        services.AddScoped<IdempotencyKeys>();
        services.AddScoped<Events.IDomainEvents, Events.DomainEvents>();

        // Crypto
        services.AddSingleton<DataEncryptor>();
        services.AddSingleton<LookupHasher>();
        services.AddSingleton<PasswordHasher>();

        // Jobs and health
        services.AddSingleton<Jobs.Jobs>();
        services.AddSingleton<JobsHealth>();
        services.AddSingleton<JobsHealthCache>();
        services.AddSingleton<ReadinessState>();
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

        // Web
        services.AddControllers(o =>
            {
                o.Filters.AddService<TransactionFilter>(int.MinValue);
                o.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
                o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            })
            .AddJsonOptions(o => ZawedJson.Configure(o.JsonSerializerOptions))
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ModelStateErrors.Respond);
        services.ConfigureHttpJsonOptions(o => ZawedJson.Configure(o.SerializerOptions));
        services.AddExceptionHandler<ZawedExceptionHandler>();
        services.AddProblemDetails();
        services.AddZawedOpenApi();
        services.AddZawedAuthentication();
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Behind Railway's proxy: trust X-Forwarded-For/-Proto only from the private ranges Tomcat's
            // RemoteIpValve trusts by default, Railway's 100.64.0.0/10 among them; never from the client.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var network in TrustedProxyNetworks)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
            o.ForwardLimit = null;
        });

        // Modules first, then the platform's defaults for what no module supplied.
        foreach (var module in ZawedModules.Discover(typeof(PlatformSetup).Assembly))
        {
            module.AddServices(services, configuration);
        }
        services.TryAddScoped<ISessionVerifier, NoSessions>();
        return builder;
    }

    /// <summary>The proxies whose forwarded headers are believed (Tomcat's <c>internalProxies</c> default).</summary>
    public static readonly string[] TrustedProxyNetworks =
    [
        "10.0.0.0/8", "192.168.0.0/16", "169.254.0.0/16", "127.0.0.0/8", "100.64.0.0/10", "172.16.0.0/12", "::1/128",
        "fc00::/7", "fe80::/10",
    ];

    /// <summary>
    /// Before the first request: the configuration checks (<see cref="DeploymentGuard"/>), the migrations as the
    /// owner, and the data key state. A failure stops the start, as a failed Flyway migration does.
    /// </summary>
    public static async Task PrepareAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ZawedOptions>>().Value;
        DeploymentGuard.Check(options, ZawedConfiguration.IsDeployedProfile(app.Configuration));
        var database = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Zawed.Migrations");
        var owner = DataSources.OwnerConnectionString(database);
        if (database.MigrateOnStartup)
        {
            await new MigrationRunner(owner, MigrationRunner.Embedded(), logger).MigrateAsync();
        }
        await DataKeyState.RecordAsync(owner, options.Security.CurrentDataKeyId(),
            DataKeyState.Fingerprint(app.Services.GetRequiredService<LookupHasher>()), logger);
    }

    public static WebApplication UseZawed(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ZawedOptions>>().Value;
        if (ZawedConfiguration.IsDeployedProfile(app.Configuration))
        {
            app.UseForwardedHeaders();
        }
        app.UseMiddleware<RequestIds>();
        app.UseMiddleware<SecurityHeaders>();
        app.UseExceptionHandler();
        app.UseMiddleware<CanonicalPath>();
        app.UseMiddleware<Cors>();
        app.UseStatusCodePages(async context =>
        {
            // A bare status (a 404 or 405 from routing, a 415 from MVC) still gets the envelope.
            var http = context.HttpContext;
            if (!http.Response.HasStarted && http.Response.ContentLength is null && string.IsNullOrEmpty(http.Response.ContentType))
            {
                await ApiErrors.WriteStatusAsync(http, http.Response.StatusCode);
            }
        });
        // The interactive reference (static pages, public like the Java one): before routing, so the
        // deny-by-default fallback route never sees it. ZAWED_API_DOCS=false turns it off.
        if (options.ApiDocs)
        {
            app.UseSwaggerUI(ui =>
            {
                ui.RoutePrefix = "api/v1/docs";
                ui.SwaggerEndpoint(ZawedOpenApi.DocumentPath, "ZAWED API");
                ui.EnablePersistAuthorization();
                ui.DisplayRequestDuration();
                ui.EnableTryItOutByDefault();
            });
        }
        app.UseRouting();
        app.UseAuthentication();
        app.UseInvalidBearerRefusal();
        app.UsePasswordChangeGate();
        app.UseAuthorization();

        app.MapOpenApi(ZawedOpenApi.DocumentPath).AllowAnonymous();
        app.MapZawedHealth();
        app.MapControllers();
        // Deny by default: a request no endpoint matches is 401 for an anonymous caller (the fallback policy
        // applies to it too) and, signed in, falls through to a bare 404 that the status code pages turn into
        // the envelope; a known path with another method is 405 the same way.
        return app;
    }
}
