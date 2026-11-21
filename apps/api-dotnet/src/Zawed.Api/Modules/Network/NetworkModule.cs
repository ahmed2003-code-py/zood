using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform;
using Zawed.Api.Platform.Modules;
using Zawed.Api.Platform.Security;

namespace Zawed.Api.Modules.Network;

/// <summary>
/// Station operators, stations, POS devices, agents and shifts, and the POS device channel (Java package <c>network</c>).
/// <see cref="PosCredentials"/> is the platform's <see cref="IPosAuthenticator"/>; the nonce retention job runs here.
/// </summary>
public sealed class NetworkModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Operators>();
        services.AddScoped<IOperatorDirectory>(sp => sp.GetRequiredService<Operators>());
        services.AddScoped<IStationDirectory, StationDirectory>();
        services.AddScoped<Devices>();
        services.AddScoped<Agents>();
        services.AddScoped<Shifts>();
        services.AddSingleton<DeviceTokens>();
        services.AddScoped<DeviceChannel>();
        services.AddScoped<AgentSessions>();
        services.AddScoped<IAgentShifts>(sp => sp.GetRequiredService<AgentSessions>());
        services.AddScoped<IPosAuthenticator, PosCredentials>();
        PlatformSetup.AddJob<NonceRetention>(services);

        // A coordinate is a JSON number in the document, as springdoc writes a BigDecimal: {"type": "number"}, inline.
        services.Configure<OpenApiOptions>("v1", options =>
        {
            var named = options.CreateSchemaReferenceId;
            options.CreateSchemaReferenceId = type =>
                (Nullable.GetUnderlyingType(type.Type) ?? type.Type) == typeof(Coordinate) ? null : named(type);
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if ((Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type) == typeof(Coordinate))
                {
                    schema.Type = JsonSchemaType.Number;
                    schema.Format = null;
                    schema.Properties = null;
                    schema.Required = null;
                }
                return Task.CompletedTask;
            });
        });
    }
}
