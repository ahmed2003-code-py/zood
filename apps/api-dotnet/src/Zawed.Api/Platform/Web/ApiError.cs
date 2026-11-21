using System.Text.Json.Serialization;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// The one shape every refusal and failure leaves this API in (the Java <c>ApiError</c>, byte for byte
/// in field names and null handling). <c>code</c> is stable and machine-readable: clients choose their
/// Arabic or English wording from it and never from <c>message</c>, which is an English fallback for
/// logs and tools. <c>requestId</c> matches the <c>X-Request-Id</c> header and the log line.
/// </summary>
public sealed record ApiError(
    int StatusCode,
    string Code,
    string Message,
    bool Retryable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FieldIssue>? Fields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object>? Params = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds = null);

/// <summary>One input's complaint in a validation failure.</summary>
public sealed record FieldIssue(
    string Field,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
