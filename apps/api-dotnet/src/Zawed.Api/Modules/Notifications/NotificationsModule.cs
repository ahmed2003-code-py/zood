using Zawed.Api.Modules.Fueling.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Tickets.Contracts;
using Zawed.Api.Platform;
using Zawed.Api.Platform.Events;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// In-app notifications (Java package <c>notifications</c>, B7 W5; owner decision 2026-11-19: in-app only, no e-mail
/// yet). No module depends on this one: producers publish their own domain events, and <see cref="NotificationTriggers"/>
/// turns each into a notification inside the publishing transaction, in a savepoint. Controllers are discovered.
/// </summary>
public sealed class NotificationsModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<NotificationMetrics>();
        services.AddScoped<Notifications>();
        services.AddScoped<NotificationTriggers>();
        Handles<WalletLowBalance>(services);
        Handles<TopUpCredited>(services);
        Handles<TopUpRejected>(services);
        Handles<TopUpAwaitingApproval>(services);
        Handles<WalletAdjustmentAwaitingApproval>(services);
        Handles<TicketOpened>(services);
        Handles<TicketAnswered>(services);
        Handles<HardAlertRaised>(services);
        PlatformSetup.AddJob<NotificationRetention>(services);
    }

    private static void Handles<TEvent>(IServiceCollection services) =>
        services.AddScoped<IDomainEventHandler<TEvent>>(sp => (IDomainEventHandler<TEvent>)(object)sp.GetRequiredService<NotificationTriggers>());
}
