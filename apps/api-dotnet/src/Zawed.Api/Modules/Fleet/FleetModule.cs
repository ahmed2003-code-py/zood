using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Fleet;

/// <summary>Vehicles, RFID tags, limits and bulk imports (Java package <c>fleet</c>). Controllers and EF configurations are discovered.</summary>
public sealed class FleetModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Vehicles>();
        services.AddScoped<Tags>();
    }
}
