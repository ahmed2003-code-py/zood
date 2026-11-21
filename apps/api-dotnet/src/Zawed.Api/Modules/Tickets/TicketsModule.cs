using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Tickets;

/// <summary>
/// Support tickets (Java package <c>tickets</c>). Publishes <see cref="Contracts.TicketOpened"/> and
/// <see cref="Contracts.TicketAnswered"/> for the notifications module. Controllers are discovered.
/// </summary>
public sealed class TicketsModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddScoped<Tickets>();
}
