namespace Zawed.Api.Modules.Tenancy.Contracts;

/// <summary>
/// Published, synchronously and inside the onboarding transaction, when a company row has been written (Java
/// <c>tenancy.CompanyCreated</c>). A module that must set something up for every company (the ledger opens its
/// wallet) registers an <c>IDomainEventHandler&lt;CompanyCreated&gt;</c>: its work commits with the company or not at
/// all, and tenancy stays unaware of what that work is.
/// </summary>
public sealed record CompanyCreated(Guid CompanyId);
