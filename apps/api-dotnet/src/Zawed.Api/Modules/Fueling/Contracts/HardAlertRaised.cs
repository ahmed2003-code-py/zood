namespace Zawed.Api.Modules.Fueling.Contracts;

/// <summary>
/// Published, synchronously and inside the transaction that raised it, when a hard alert (PRD §10) is written (Java
/// <c>fueling.HardAlertRaised</c>). Not published when the database already holds the same alert (a rejected sync item
/// sent again, V8). Null ids do not apply to the alert. A handler that must not fail the fueling (a notification) writes
/// in a savepoint (<c>DbTx.SavepointAsync</c>, B7 W5).
/// </summary>
/// <param name="Type">the alert type's name, such as <c>LOW_BALANCE</c> or <c>SYNC_CONFLICT</c></param>
/// <param name="Severity"><c>WARNING</c> or <c>CRITICAL</c></param>
public sealed record HardAlertRaised(Guid AlertId, string Type, string Severity, Guid? CompanyId, Guid? OperatorId, Guid? StationId,
    Guid? FuelingId);
