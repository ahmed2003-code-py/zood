using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Fueling;

/// <summary>
/// The online fueling engine, fuelings, hard alerts, receipts, the activity calendar and the CSV exports (Java package
/// <c>fueling</c>, wave 3A). Raises the low-balance alert on the ledger's <see cref="WalletLowBalance"/>, publishes
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
        services.AddScoped<Receipts>();
        services.AddScoped<Activity>();
        services.AddScoped<FuelingExports>();
    }
}
