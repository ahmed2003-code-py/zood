using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Zawed.Api.Platform.OpenApi;

/// <summary>
/// The API description at <c>/api/v1/openapi.json</c>, shaped like the Java one (springdoc plus
/// <c>OpenApiConfig</c>), because the web app generates its TypeScript types from it and the parity test
/// compares the two operation by operation:
/// <list type="bullet">
/// <item>title "ZAWED API", version <c>v1</c>, one server <c>/</c>, the same description;</item>
/// <item>the three security schemes (<c>userToken</c>, <c>deviceToken</c>, <c>agentSession</c>), and on each
/// operation the requirement the Java document gives it, from the same path rules;</item>
/// <item><c>ApiError</c> (and <c>FieldIssue</c>) in the components, and a <c>default</c> response "Any refusal
/// or failure" with it on every operation;</item>
/// <item>a property is <c>required</c> when it cannot be null, as the Java document marks it.</item>
/// </list>
/// </summary>
public static class ZawedOpenApi
{
    public const string DocumentPath = "/api/v1/openapi.json";
    public const string UserToken = "userToken";
    public const string DeviceToken = "deviceToken";
    public const string AgentSession = "agentSession";

    private const string Description =
        "Fleet fuel management platform. Money is integer piastres, fuel volume is integer millilitres, timestamps are ISO-8601 UTC. "
        + "Every error has the ApiError shape, with a stable `code`.\n\n**Trying it here.** Sign in with POST /api/v1/auth/login (every role, "
        + "Super Admin included), finish two-step verification with /api/v1/auth/mfa/verify where asked, then press **Authorize** and paste "
        + "the `accessToken` into `userToken`. A POS route needs a device token (`deviceToken`) and, for fuelings, the agent's session "
        + "(`agentSession`) instead. Lists are paginated with `cursor` and `limit`.";

    /// <summary>Calls that need no credential (Java <c>OpenApiConfig.PUBLIC</c>).</summary>
    public static readonly IReadOnlySet<string> Public = new HashSet<string>
    {
        "/api/v1/health", "/api/v1/health/liveness", "/api/v1/health/readiness", "/api/v1/meta", "/api/v1/openapi.json",
        "/api/v1/receipts/{id}", "/api/v1/auth/login", "/api/v1/auth/mfa/verify", "/api/v1/auth/mfa/enroll/start",
        "/api/v1/auth/mfa/enroll/confirm", "/api/v1/auth/refresh", "/api/v1/auth/logout", "/api/v1/setup/super-admin",
        "/api/v1/pos/activate", "/api/v1/pos/token", "/api/v1/public/demo-requests",
    };

    /// <summary>POS calls made by the device alone (Java <c>OpenApiConfig.DEVICE_ONLY</c>).</summary>
    public static readonly IReadOnlySet<string> DeviceOnly = new HashSet<string>
    {
        "/api/v1/pos/agents/login", "/api/v1/pos/snapshot", "/api/v1/pos/sync",
    };

    public static IServiceCollection AddZawedOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddSchemaTransformer(MarkRequired);
            options.AddDocumentTransformer(Shape);
        });
        return services;
    }

    private static async Task Shape(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo { Title = "ZAWED API", Version = "v1", Description = Description };
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [UserToken] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "A user's access token: `accessToken` from POST /api/v1/auth/login (every role), after /api/v1/auth/mfa/verify "
                    + "for roles with two-step verification. Valid 15 minutes.",
            },
            [DeviceToken] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "A POS device token from POST /api/v1/pos/token (signed with the device's own key, ADR 0010). Valid 15 minutes. "
                    + "A user token is never accepted here.",
            },
            [AgentSession] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Agent-Session",
                Description = "The agent's session from POST /api/v1/pos/agents/login, sent with the device token.",
            },
        };
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        var apiError = await context.GetOrCreateSchemaAsync(typeof(Web.ApiError), null, ct);
        apiError.Description = "The shape of every refusal and failure";
        document.Components.Schemas["ApiError"] = apiError;
        if (!document.Components.Schemas.ContainsKey("FieldIssue"))
        {
            document.Components.Schemas["FieldIssue"] = await context.GetOrCreateSchemaAsync(typeof(Web.FieldIssue), null, ct);
        }

        foreach (var (path, item) in document.Paths)
        {
            if (item.Operations is null)
            {
                continue;
            }
            foreach (var operation in item.Operations.Values)
            {
                operation.Security = Security(path, document);
                operation.Responses ??= new OpenApiResponses();
                operation.Responses["default"] = new OpenApiResponse
                {
                    Description = "Any refusal or failure",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = new OpenApiSchemaReference("ApiError", document) },
                    },
                };
            }
        }
    }

    private static List<OpenApiSecurityRequirement> Security(string path, OpenApiDocument document)
    {
        OpenApiSecurityRequirement Requirement(params string[] schemes)
        {
            var requirement = new OpenApiSecurityRequirement();
            foreach (var scheme in schemes)
            {
                requirement[new OpenApiSecuritySchemeReference(scheme, document)] = [];
            }
            return requirement;
        }
        if (Public.Contains(path))
        {
            return [];
        }
        if (DeviceOnly.Contains(path))
        {
            return [Requirement(DeviceToken)];
        }
        return path.StartsWith("/api/v1/pos/", StringComparison.Ordinal) ? [Requirement(DeviceToken, AgentSession)] : [Requirement(UserToken)];
    }

    /// <summary>Non-nullable properties are required, as the Java document marks them.</summary>
    private static Task MarkRequired(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (context.JsonPropertyInfo is not null || schema.Properties is null || schema.Properties.Count == 0)
        {
            return Task.CompletedTask;
        }
        var nullability = new NullabilityInfoContext();
        foreach (var property in context.JsonTypeInfo.Properties)
        {
            if (property.AttributeProvider is not PropertyInfo info || !schema.Properties.ContainsKey(property.Name))
            {
                continue;
            }
            var ignoredWhenNull = info.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>()?.Condition
                == System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            var nullable = info.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(info.PropertyType) is not null
                : nullability.Create(info).ReadState != NullabilityState.NotNull;
            if (!nullable && !ignoredWhenNull)
            {
                schema.Required ??= new HashSet<string>();
                schema.Required.Add(property.Name);
            }
        }
        return Task.CompletedTask;
    }
}
