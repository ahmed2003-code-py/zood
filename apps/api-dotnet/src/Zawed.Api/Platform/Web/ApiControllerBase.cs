using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Base of every controller (playbook <c>ApiController</c>): thin actions that take a DTO, call one
/// service and turn its <see cref="Result"/> into HTTP with <see cref="From(Result)"/>. Refusals use the
/// ZAWED envelope (<see cref="ApiError"/>), never the playbook's <c>{ success, message }</c>, because the
/// web app and the POS read <c>code</c>.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult From(Result result) => result.IsSuccess
        ? StatusCode(result.Status)
        : new ApiErrorResult(result.Status, result.Error!);

    protected IActionResult From<T>(Result<T> result) => result.IsSuccess
        ? result.Status == 204 ? NoContent() : StatusCode(result.Status, result.Data)
        : new ApiErrorResult(result.Status, result.Error!);
}

/// <summary>An <see cref="IActionResult"/> that writes the <see cref="ApiError"/> envelope.</summary>
public sealed class ApiErrorResult(int status, ResultError error) : IActionResult, IStatusCodeActionResult
{
    public int? StatusCode => status;

    public Task ExecuteResultAsync(ActionContext context) =>
        ApiErrors.WriteAsync(context.HttpContext, status, error.Code, error.Message, error.Field, error.Fields, error.Params);
}
