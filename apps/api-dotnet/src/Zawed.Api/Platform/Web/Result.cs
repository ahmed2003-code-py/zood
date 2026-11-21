namespace Zawed.Api.Platform.Web;

/// <summary>
/// What a service returns (playbook "Result pattern", adapted to ZAWED's error contract): success with a
/// status (200, 201 or 204), or a refusal carrying exactly what <see cref="ApiError"/> needs (status,
/// stable <c>code</c>, English message, optional <c>field</c>, <c>fields</c> and <c>params</c>).
/// <see cref="ApiControllerBase.From(Result)"/> turns it into HTTP, and the per-request transaction
/// rolls back on any status of 400 or more, so a refusal never leaves half a write behind.
/// </summary>
public class Result
{
    protected Result(int status, ResultError? error)
    {
        Status = status;
        Error = error;
    }

    public int Status { get; }
    public ResultError? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result Ok() => new(200, null);
    public static Result NoContent() => new(204, null);

    public static Result Fail(int status, string code, string message, string? field = null,
        IReadOnlyDictionary<string, object>? @params = null, IReadOnlyList<FieldIssue>? fields = null) =>
        new(status, new ResultError(code, message, field, fields, @params));

    /// <summary>Unknown and out-of-scope look the same (invariant 8).</summary>
    public static Result NotFound(string entity) => Fail(404, ErrorCodes.NotFound, entity + " not found");
    public static Result Conflict(string code, string message) => Fail(409, code, message);
    public static Result Forbidden(string code = ErrorCodes.Forbidden, string message = "You do not have permission to do this") =>
        Fail(403, code, message);
    public static Result Invalid(params FieldIssue[] fields) =>
        Fail(400, ErrorCodes.ValidationFailed, "The request is not valid", fields: fields);
}

/// <summary>A refusal's content, as <see cref="ApiError"/> will carry it.</summary>
public sealed record ResultError(string Code, string Message, string? Field, IReadOnlyList<FieldIssue>? Fields,
    IReadOnlyDictionary<string, object>? Params);

public sealed class Result<T> : Result
{
    private Result(int status, T? data, ResultError? error) : base(status, error) => Data = data;

    public T? Data { get; }

    public static Result<T> Ok(T data) => new(200, data, null);
    public static Result<T> Created(T data) => new(201, data, null);

    public static new Result<T> Fail(int status, string code, string message, string? field = null,
        IReadOnlyDictionary<string, object>? @params = null, IReadOnlyList<FieldIssue>? fields = null) =>
        new(status, default, new ResultError(code, message, field, fields, @params));

    public static new Result<T> NotFound(string entity) => Fail(404, ErrorCodes.NotFound, entity + " not found");
    public static new Result<T> Conflict(string code, string message) => Fail(409, code, message);
    public static new Result<T> Forbidden(string code = ErrorCodes.Forbidden, string message = "You do not have permission to do this") =>
        Fail(403, code, message);
    public static new Result<T> Invalid(params FieldIssue[] fields) =>
        Fail(400, ErrorCodes.ValidationFailed, "The request is not valid", fields: fields);

    /// <summary>A refusal of another type carried over unchanged.</summary>
    public static Result<T> From(Result failed) =>
        failed.Error is { } e ? new(failed.Status, default, e) : throw new InvalidOperationException("Not a failure");

    public static implicit operator Result<T>(T data) => Ok(data);
}
