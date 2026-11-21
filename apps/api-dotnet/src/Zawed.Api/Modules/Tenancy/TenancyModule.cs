using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Tenancy;

/// <summary>Companies and departments (Java package <c>tenancy</c>). Controllers and EF configurations are discovered.</summary>
public sealed class TenancyModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Companies>();
        services.AddScoped<ICompanyDirectory, CompanyDirectory>();
    }
}
