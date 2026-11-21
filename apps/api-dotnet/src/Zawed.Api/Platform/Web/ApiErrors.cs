using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// The single writer of the error envelope. Every path that refuses a request (exceptions, model
/// validation, authentication, authorisation, the canonical path filter, CORS, rate limits, unknown
/// routes) ends here, so every refusal has the same body and the request id.
/// </summary>
public static class ApiErrors
{
    public const string ContentType = "application/json";

    public static ApiError Build(HttpContext context, int status, string code, string message, string? field = null,
        IReadOnlyList<FieldIssue>? fields = null, IReadOnlyDictionary<string, object>? @params = null,
        int? retryAfterSeconds = null, bool? retryable = null) =>
        new(status, code, message, retryable ?? ErrorCodes.RetryableByDefault(status), RequestIds.Current(context), field,
            fields is { Count: > 0 } ? fields : null, @params is { Count: > 0 } ? @params : null, retryAfterSeconds);

    public static async Task WriteAsync(HttpContext context, int status, string code, string message, string? field = null,
        IReadOnlyList<FieldIssue>? fields = null, IReadOnlyDictionary<string, object>? @params = null,
        int? retryAfterSeconds = null, bool? retryable = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }
        var body = Build(context, status, code, message, field, fields, @params, retryAfterSeconds, retryable);
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        if (retryAfterSeconds is { } seconds)
        {
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var json = context.RequestServices?.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? ZawedJson.Options;
        await JsonSerializer.SerializeAsync(context.Response.Body, body, json, context.RequestAborted);
    }

    /// <summary>The generic envelope for a bare status: the code of its category and its reason phrase.</summary>
    public static Task WriteStatusAsync(HttpContext context, int status) =>
        WriteAsync(context, status, ErrorCodes.Generic(status), ErrorCodes.ReasonPhrase(status));
}
