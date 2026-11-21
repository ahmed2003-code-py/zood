using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Sync;

/// <summary>
/// The POS snapshot and the sync monitor (Java package <c>sync</c>, wave 4A). The offline ingest itself
/// (<c>POST /pos/sync</c>) is the fueling module's <c>OfflineSync</c>, as in Java.
/// </summary>
public sealed class SyncModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Read at boot, as Java's @Value did.
        services.AddSingleton(SyncSettings.From(configuration));
        services.AddScoped<Snapshots>();
        services.AddScoped<SyncMonitor>();
    }
}
