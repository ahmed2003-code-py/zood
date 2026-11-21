using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Audit;

public sealed class AuditModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuditLog, AuditLog>();
    }
}
