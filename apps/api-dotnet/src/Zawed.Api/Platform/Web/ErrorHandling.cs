using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// Turns every exception into the <see cref="ApiError"/> envelope (Java <c>GlobalExceptionHandler</c>).
/// Nothing internal reaches a response: an unexpected failure is logged with its stack trace and answered
/// with a generic 500 that carries only the request id. Database outcomes the playbook maps (principle 7):
/// a stale <c>xmin</c> or a deadlock or serialization failure that escaped its retry answers
/// <c>409 CONFLICT</c> (retryable), an unhandled unique violation <c>409 CONFLICT</c>, and a cross-scope
/// write <c>404 NOT_FOUND</c> (invariant 8).
/// </summary>
public sealed class ZawedExceptionHandler(ILogger<ZawedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        switch (exception)
        {
            case RateLimitedException limited:
                await ApiErrors.WriteAsync(context, 429, limited.Code, limited.Message, @params: limited.Params,
                    retryAfterSeconds: (int)Math.Min(int.MaxValue, limited.RetryAfterSeconds), retryable: true);
                return true;
            case FieldsInvalidException invalid:
                await ApiErrors.WriteAsync(context, 400, invalid.Code, invalid.Message, fields: invalid.Fields);
                return true;
            case ApiException api:
                await ApiErrors.WriteAsync(context, api.Status, api.Code, api.Message, api.Field, @params: api.Params);
                return true;
            case CrossScopeWriteException cross:
                logger.LogWarning("Blocked a cross-scope write of {Entity} on {Path}", cross.Entity, context.Request.Path);
                await ApiErrors.WriteAsync(context, 404, ErrorCodes.NotFound, "Not found");
                return true;
            case DbUpdateConcurrencyException:
                await ApiErrors.WriteAsync(context, 409, "CONFLICT", "The data was changed by someone else; reload and try again",
                    retryable: true);
                return true;
            case BadHttpRequestException bad:
                await ApiErrors.WriteStatusAsync(context, bad.StatusCode);
                return true;
            case JsonException:
                await ApiErrors.WriteStatusAsync(context, 400);
                return true;
        }
        if (DbErrors.IsRetryableConflict(exception))
        {
            await ApiErrors.WriteAsync(context, 409, "CONFLICT", "Another operation happened at the same moment; try again", retryable: true);
            return true;
        }
        if (DbErrors.IsUniqueViolation(exception))
        {
            logger.LogWarning("Unhandled unique violation on {Method} {Path}: {Constraint}", context.Request.Method, context.Request.Path,
                DbErrors.Postgres(exception)?.ConstraintName);
            await ApiErrors.WriteAsync(context, 409, "CONFLICT", "Conflict");
            return true;
        }
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            return true; // the client went away; nobody reads an answer
        }
        logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        await ApiErrors.WriteAsync(context, 500, ErrorCodes.InternalError, "Unexpected error");
        return true;
    }
}

/// <summary>
/// Model binding and validation failures, in the Java API's terms:
/// <list type="bullet">
/// <item>a fraction for a whole-number field: <c>400 VALIDATION_FAILED</c>, <c>INTEGER_REQUIRED</c> on that field;</item>
/// <item>any other unreadable body (broken JSON, an unknown property, a wrong type, no body):
/// <c>400 BAD_REQUEST</c>, "Bad Request", nothing from the request echoed;</item>
/// <item>a broken validation rule: <c>400 VALIDATION_FAILED</c> with one <see cref="FieldIssue"/> per rule, its
/// code taken from the ZAWED validation attribute (<see cref="Validation"/>), <c>NOT_BLANK</c> for <c>NotBlank</c>.</item>
/// </list>
/// </summary>
public static class ModelStateErrors
{
    /// <summary>The message MVC's binders give a value they cannot convert (set by <see cref="MarkConversionFailures"/>).</summary>
    public const string NotConvertible = "ZAWED_NOT_CONVERTIBLE";

    /// <summary>Makes every "this value is not valid" message of model binding recognisable, so it answers BAD_REQUEST.</summary>
    public static void MarkConversionFailures(Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.DefaultModelBindingMessageProvider messages)
    {
        messages.SetAttemptedValueIsInvalidAccessor((_, _) => NotConvertible);
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => NotConvertible);
        messages.SetUnknownValueIsInvalidAccessor(_ => NotConvertible);
        messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => NotConvertible);
        messages.SetValueIsInvalidAccessor(_ => NotConvertible);
        messages.SetValueMustBeANumberAccessor(_ => NotConvertible);
        messages.SetNonPropertyValueMustBeANumberAccessor(() => NotConvertible);
    }

    public static IActionResult Respond(ActionContext context)
    {
        var entries = context.ModelState.Where(e => e.Value is { Errors.Count: > 0 }).ToList();
        foreach (var (key, entry) in entries)
        {
            foreach (var error in entry!.Errors)
            {
                if (Find<IntegerRequiredException>(error.Exception) is not null
                    || error.ErrorMessage.StartsWith(IntegerRequiredException.Marker, StringComparison.Ordinal))
                {
                    return Error(400, ErrorCodes.ValidationFailed, "The request is not valid",
                        [new FieldIssue(FieldPath(key) ?? "", "INTEGER_REQUIRED", "must be a whole number")]);
                }
            }
        }
        // Input formatter errors are keyed by their JSON path ("$", "$.field") or by the empty key (no body).
        // A path or query value that does not convert (a malformed id, an unknown enum name) is Spring's type mismatch:
        // the same plain 400 BAD_REQUEST, not a validation failure.
        var unreadable = entries.Any(e => e.Key.Length == 0 || e.Key.StartsWith('$')
            || e.Value!.Errors.Any(x => x.Exception is not null || x.ErrorMessage == NotConvertible));
        if (unreadable)
        {
            return Error(400, "BAD_REQUEST", "Bad Request", null);
        }
        var fields = new List<FieldIssue>();
        foreach (var (key, entry) in entries)
        {
            foreach (var error in entry!.Errors)
            {
                var (code, message) = Validation.Decode(error.ErrorMessage);
                fields.Add(new FieldIssue(FieldPath(key) ?? key, code, message));
            }
        }
        return Error(400, ErrorCodes.ValidationFailed, "The request is not valid", fields);
    }

    private static IActionResult Error(int status, string code, string message, IReadOnlyList<FieldIssue>? fields) =>
        new ApiErrorResult(status, new ResultError(code, message, null, fields, null));

    /// <summary>
    /// <c>$.items[0].volumeMl</c>, <c>Items[0].VolumeMl</c> (a record parameter's CLR name) or
    /// <c>items[0].volumeMl</c> all become <c>items[0].volumeMl</c>: the JSON name, as the client sent it.
    /// </summary>
    public static string? FieldPath(string key)
    {
        var path = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key == "$" ? "" : key;
        if (path.Length == 0)
        {
            return null;
        }
        return string.Join('.', path.Split('.').Select(segment =>
            segment.Length > 0 && char.IsUpper(segment[0]) ? char.ToLowerInvariant(segment[0]) + segment[1..] : segment));
    }

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
            {
                return match;
            }
        }
        return null;
    }
}
