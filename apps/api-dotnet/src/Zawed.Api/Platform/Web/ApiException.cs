namespace Zawed.Api.Platform.Web;

/// <summary>
/// A refusal with a stable code, thrown from below a service (a store, the money path, a filter) where
/// returning a <see cref="Result"/> is not practical. Services prefer <see cref="Result{T}"/>; both reach
/// the client as the same <see cref="ApiError"/>. The message is an English fallback, never UI copy.
/// Params carry the numbers a translated sentence needs, never internal ids or secrets.
/// </summary>
public class ApiException : Exception
{
    public ApiException(int status, string code, string message, string? field = null,
        IReadOnlyDictionary<string, object>? @params = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Field = field;
        Params = @params is { Count: > 0 } ? new Dictionary<string, object>(@params) : null;
    }

    public int Status { get; }
    public string Code { get; }
    public string? Field { get; }
    public IReadOnlyDictionary<string, object>? Params { get; }

    /// <summary>
    /// For a row that does not exist and for a row outside the caller's scope alike: a caller must not be
    /// able to tell the two apart (invariant 8).
    /// </summary>
    public static ApiException NotFound(string entity) => new(404, ErrorCodes.NotFound, entity + " not found");
}

/// <summary>
/// A validation failure found below the controller (it needs the stored row, for example). Answers the
/// same <c>400 VALIDATION_FAILED</c> with <c>fields</c> as model validation, so a client handles both alike.
/// </summary>
public sealed class FieldsInvalidException(IReadOnlyList<FieldIssue> fields)
    : ApiException(400, ErrorCodes.ValidationFailed, "The request is not valid")
{
    public IReadOnlyList<FieldIssue> Fields { get; } = fields.ToList();
}

/// <summary>429 with a <c>Retry-After</c> header and <c>retryAfterSeconds</c> (Java <c>RateLimitedException</c>).</summary>
public sealed class RateLimitedException(long retryAfterSeconds, string code = "RATE_LIMITED", string message = "Too many requests")
    : ApiException(429, code, message)
{
    public long RetryAfterSeconds { get; } = retryAfterSeconds;
}
