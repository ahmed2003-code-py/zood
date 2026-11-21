# .NET reference material

Brought in by the owner on 2026-11-21: the backend's new responsible developer works in ASP.NET Core.
These two documents are his house conventions, used as the input for the study of moving the ZAWED
API from Spring Boot to .NET and for choosing design patterns.

- `dotnet-backend-playbook/`: the architecture, patterns and engineering rules he applies in every
  .NET backend (3-tier layering, Unit of Work, `Result<T>`, per-request transactions, idempotency,
  concurrency, multi-tenancy, state machines, data access).
- `SERVICES_CATALOG.md`: ready-made services from his earlier projects (payments, PDF, e-mail, SMS,
  notifications, background jobs, files, Excel, AI).

They are reference material, not ZAWED's own decisions. The migration plan that maps them onto ZAWED
is written separately in `docs/architecture/`.
