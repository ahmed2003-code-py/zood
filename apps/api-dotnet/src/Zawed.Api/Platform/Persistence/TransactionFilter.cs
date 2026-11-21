using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Zawed.Api.Platform.Persistence;

/// <summary>
/// One transaction per request (the playbook's <c>TransactionActionFilter</c>), adapted to ZAWED:
/// <list type="bullet">
/// <item>every action gets it, reads included, because row-level security needs the scope to show any
/// row (Java services were <c>@Transactional</c>, read-only ones too);</item>
/// <item>it begins after authorisation and model binding (a refused or invalid request never opens one)
/// and sets the caller's scope (<see cref="RequestScope"/>);</item>
/// <item>an exception, or a result with a status of 400 or more, rolls everything back; otherwise EF
/// changes are saved and the transaction commits <b>before</b> the response is written, so a client never
/// sees a success that did not commit;</item>
/// <item>work queued with <see cref="DbTx.AfterCommit"/> runs after the commit only;</item>
/// <item>an action marked <see cref="OwnTransactionsAttribute"/> is skipped: it opens its own with
/// <see cref="ITransactions"/>.</item>
/// </list>
/// </summary>
internal sealed class TransactionFilter(ITransactions transactions, DbSession session, ILogger<TransactionFilter> logger)
    : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>Outermost action filter: it wraps the idempotency filter and everything else.</summary>
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<OwnTransactionsAttribute>().Any())
        {
            await next();
            return;
        }
        var tx = await transactions.BeginAsync(RequestScope.Of(context.HttpContext), context.HttpContext.RequestAborted);
        session.Attach(tx);
        try
        {
            var executed = await next();
            if (executed.Exception is not null && !executed.ExceptionHandled)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return;
            }
            var status = StatusOf(executed.Result);
            if (status >= 400)
            {
                logger.LogDebug("{Action} answered {Status}: rolling back", context.ActionDescriptor.DisplayName, status);
                await tx.RollbackAsync(CancellationToken.None);
                return;
            }
            await tx.CommitAsync(CancellationToken.None);
        }
        finally
        {
            session.Detach();
            await tx.DisposeAsync();
        }
    }

    private static int StatusOf(IActionResult? result) => result switch
    {
        IStatusCodeActionResult { StatusCode: { } code } => code,
        ObjectResult { Value: Microsoft.AspNetCore.Mvc.ProblemDetails problem } => problem.Status ?? 500,
        _ => 200,
    };
}

/// <summary>
/// The action manages its own transactions through <see cref="ITransactions"/> (per-item sync, deadlock
/// retry, streaming exports, the POS channel's reviewed escalation). The per-request transaction is not opened.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OwnTransactionsAttribute : Attribute;
