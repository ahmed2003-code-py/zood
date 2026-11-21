namespace Zawed.Api.Platform.Events;

/// <summary>
/// Handles one kind of domain event, inside the publisher's transaction (the Java
/// <c>@EventListener</c> with <c>Propagation.MANDATORY</c>): <c>CompanyCreated</c> opens the wallet,
/// <c>WalletLowBalance</c> raises an alert, the notification triggers write their rows. A handler that
/// throws fails the publisher's transaction unless the handler protects itself with
/// <see cref="Persistence.DbTx.SavepointAsync{T}"/> (the B7 W5 rule for notifications).
/// Event types live in the publishing module's <c>Contracts</c> namespace, so the publisher never knows
/// who listens and module dependencies stay one-way.
/// </summary>
public interface IDomainEventHandler<in TEvent>
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}

/// <summary>Publishes a domain event to every handler registered for it, now, in the caller's transaction.</summary>
public interface IDomainEvents
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : notnull;
}

internal sealed class DomainEvents(IServiceProvider services) : IDomainEvents
{
    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : notnull
    {
        foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
        {
            await handler.HandleAsync(domainEvent, ct);
        }
    }
}
