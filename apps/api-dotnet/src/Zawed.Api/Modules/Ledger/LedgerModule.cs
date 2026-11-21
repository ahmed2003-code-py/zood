using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// The ledger (Java package <c>ledger</c>): accounts and postings (<see cref="ILedger"/>), company wallets and their
/// statements, top-ups and wallet adjustments under maker-checker, and the nightly reconciliation. Opens a company's
/// wallet when tenancy announces it (<see cref="CompanyCreated"/>).
/// </summary>
public sealed class LedgerModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Read at boot, as Java's @Value did: a malformed label list stops the start.
        services.AddSingleton(FinanceSettings.From(configuration));
        services.AddScoped<LedgerStore>();
        services.AddScoped<ILedger>(sp => sp.GetRequiredService<LedgerStore>());
        services.AddScoped<IWalletBalances, LedgerWalletBalances>();
        services.AddScoped<Wallets>();
        services.AddScoped<IDomainEventHandler<CompanyCreated>>(sp => sp.GetRequiredService<Wallets>());
        services.AddScoped<TopUps>();
        services.AddScoped<WalletAdjustments>();
        services.AddScoped<LedgerViews>();
        services.AddScoped<Reconciliation>();
        services.AddSingleton<ReconcileRunner>();
        PlatformSetup.AddJob<ReconciliationJob>(services);

        services.Configure<OpenApiOptions>("v1", options => options.AddOperationTransformer((operation, context, _) =>
        {
            foreach (var header in context.Description.ActionDescriptor.EndpointMetadata.OfType<DocumentedRequiredHeaderAttribute>())
            {
                foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
                {
                    if (parameter.In == ParameterLocation.Header && parameter.Name == header.Name)
                    {
                        parameter.Required = true;
                    }
                }
            }
            return Task.CompletedTask;
        }));
    }
}
