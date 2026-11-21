using Zawed.Api.Modules.Pricing.Contracts;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Pricing;

/// <summary>Fuel prices (Java package <c>pricing</c>). Controllers and EF configurations are discovered.</summary>
public sealed class PricingModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Prices>();
        services.AddScoped<IPriceBook>(sp => sp.GetRequiredService<Prices>());
    }
}
