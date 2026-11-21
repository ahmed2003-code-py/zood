namespace Zawed.Api.Platform.Web;

/// <summary>
/// Category codes: what a refusal carries when nothing more specific was named. Same table as the Java
/// <c>ErrorCodes</c>; a refusal that deserves its own sentence in the UI carries its own code.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string InternalError = "INTERNAL_ERROR";

    public static string Generic(int status) => status switch
    {
        400 => "BAD_REQUEST",
        401 => Unauthenticated,
        403 => Forbidden,
        404 => NotFound,
        405 => "METHOD_NOT_ALLOWED",
        406 => "NOT_ACCEPTABLE",
        409 => "CONFLICT",
        410 => "GONE",
        413 => "PAYLOAD_TOO_LARGE",
        415 => "UNSUPPORTED_MEDIA_TYPE",
        422 => "UNPROCESSABLE",
        429 => "RATE_LIMITED",
        502 => "UPSTREAM_ERROR",
        503 => "SERVICE_UNAVAILABLE",
        504 => "UPSTREAM_TIMEOUT",
        _ => status >= 500 ? InternalError : "BAD_REQUEST",
    };

    /// <summary>Sending the same request again can only help when the failure was not about the request.</summary>
    public static bool RetryableByDefault(int status) => status == 429 || status >= 500;

    /// <summary>The reason phrase only: nothing from the request is echoed back (Java <c>safeMessage</c>).</summary>
    public static string ReasonPhrase(int status)
    {
        var phrase = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
        // Spring's wording for the two that differ in ASP.NET Core.
        return status switch
        {
            413 => "Content Too Large",
            422 => "Unprocessable Entity",
            _ => string.IsNullOrEmpty(phrase) ? "Request failed" : phrase,
        };
    }
}
