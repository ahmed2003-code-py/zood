using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// The online fueling engine, fuelings, hard alerts, receipts, the activity calendar and the CSV exports (Java package
/// <c>fueling</c>, waves 3A and 4A: the offline half of the engine and <see cref="OfflineSync"/>). Raises the low-balance alert on the ledger's <see cref="WalletLowBalance"/>, publishes
/// <see cref="HardAlertRaised"/>, and offers <see cref="IFuelingApi"/> to sync, station finance and tickets.
/// </summary>
public sealed class FuelingModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Read at boot, as Java's @Value did.
        services.AddSingleton(FuelingSettings.From(configuration));
        services.AddScoped<Fuelings>();
        services.AddScoped<IFuelingApi>(sp => sp.GetRequiredService<Fuelings>());
        services.AddScoped<Alerts>();
        services.AddScoped<IDomainEventHandler<WalletLowBalance>>(sp => sp.GetRequiredService<Alerts>());
        services.AddScoped<FuelingEngine.Step>();
        services.AddScoped<FuelingEngine>();
        services.AddScoped<OfflineSync>();
        services.AddScoped<Receipts>();
        services.AddScoped<Activity>();
        services.AddScoped<FuelingExports>();

        // An offline item's volume and price are read as numbers (SyncNumber) but documented as springdoc wrote Java's
        // @Schema(implementation = Long.class) BigDecimal: {"type": "integer", "format": "int64"}, inline.
        services.Configure<OpenApiOptions>("v1", options =>
        {
            var named = options.CreateSchemaReferenceId;
            options.CreateSchemaReferenceId = type =>
                (Nullable.GetUnderlyingType(type.Type) ?? type.Type) == typeof(SyncNumber) ? null : named(type);
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if ((Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type) == typeof(SyncNumber))
                {
                    schema.Type = JsonSchemaType.Integer;
                    schema.Format = "int64";
                    schema.Properties = null;
                    schema.Required = null;
                }
                return Task.CompletedTask;
            });
        });
    }
}
